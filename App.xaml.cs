using System;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using DesktopDock.Models;
using DesktopDock.Services;
using DesktopDock.Views;

namespace DesktopDock
{
    public partial class App : System.Windows.Application
    {
        private const string MutexName = @"Local\DesktopDock.SingleInstance";
        private const string SummonEventName = @"Local\DesktopDock.Summon";
        private const string QuitEventName = @"Local\DesktopDock.Quit";

        private Mutex _mutex;
        private bool _ownsMutex;
        private DockWindow _dock;
        private EdgeTabWindow _tab;
        private WidgetWindow _widget;
        private InfoWindow _info;
        private TrayIcon _tray;
        private DispatcherTimer _hoverTimer;
        private DispatcherTimer _widgetTimer;

        private long _hoverSince;
        private long _leaveSince;
        private string _tabDevice = "";
        private bool _tabAllowed = true;
        private EventWaitHandle _summon;
        private EventWaitHandle _quit;
        private bool _quitting;

        public static AppSettings Settings { get; private set; }

        /// <summary>当前进程完整性级别：Low / Medium / High（启动时探测一次）</summary>
        public static string Integrity { get; private set; } = "unknown";

        /// <summary>低权限模式下很多功能会被系统拒绝，界面上要明确提示并给出修复入口</summary>
        public static bool IsLowIntegrity => string.Equals(Integrity, "Low", StringComparison.OrdinalIgnoreCase);

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            string[] args = e.Args ?? Array.Empty<string>();

            // 计划任务/开机自启常把 cwd 设成 C:\Windows\System32，会让 ShellExecute 目录、
            // 相对路径解析等操作莫名其妙失败；启动时统一归位到程序目录。
            try
            {
                System.IO.Directory.SetCurrentDirectory(AppContext.BaseDirectory);
            }
            catch
            {
                // 归位失败不影响主流程
            }

            // ① 被提升起来的助手进程：只负责写自启，不创建任何界面。
            if (HasArg(args, AutoStart.InstallTaskArg))
            {
                Shutdown(AutoStart.InstallTaskHelper());
                return;
            }

            if (HasArg(args, AutoStart.RepairArg))
            {
                Shutdown(AutoStart.RepairHelper());
                return;
            }

            if (HasArg(args, AutoStart.DisableArg))
            {
                Shutdown(AutoStart.DisableHelper());
                return;
            }

            SettingsStore.Log("=== start pid=" + Environment.ProcessId
                              + " args=[" + string.Join(" ", args) + "]");

            // ② 自我安装：从下载目录/沙箱目录直接运行会让整个进程被钉在 Low IL，
            //    装到 %LOCALAPPDATA%\Programs\DesktopDock 才能正常写注册表、开机自启。
            if (InstallLocation.ShouldMigrate(args))
            {
                if (InstallLocation.MigrateAndRelaunch(args, out string installMessage))
                {
                    SettingsStore.Log("自安装完成，切换到新副本: " + installMessage);
                    Shutdown();
                    return;
                }

                SettingsStore.Log("自安装失败，继续在当前位置运行: " + installMessage);
            }

            // ③ 低权限兜底：如果已经在干净位置却仍是 Low，说明标签被别的程序写坏了，
            //    清一次标签原地重启；只试一次，避免死循环。
            if (IntegrityLevel.IsLow && !HasArg(args, "--relaunched"))
            {
                if (InstallLocation.TryRepairLowIntegrityInPlace(args, out string repairMessage))
                {
                    SettingsStore.Log("低权限自愈: " + repairMessage);
                    Shutdown();
                    return;
                }

                SettingsStore.Log("低权限自愈失败: " + repairMessage);
            }

            Integrity = IntegrityLevel.Describe();
            SettingsStore.Log("integrity=" + Integrity);
            SettingsStore.Log("launcher parent=" + Native.DescribeParent(Environment.ProcessId));
            SettingsStore.Log("location " + InstallLocation.Describe());

            // ④ 单实例：迁移/低权限重启后允许接管（等旧实例释放锁）
            bool takeover = HasArg(args, "--takeover") || InstallLocation.ConsumeTakeoverFlag();
            if (!AcquireSingleInstance(takeover))
            {
                SettingsStore.Log("已有实例在运行，广播唤出 pid=" + Environment.ProcessId);
                SummonExisting();
                Shutdown();
                return;
            }

            DispatcherUnhandledException += (s, unhandled) =>
            {
                SettingsStore.Log("未处理异常: " + unhandled.Exception);
                unhandled.Handled = true;
            };

            Settings = SettingsStore.Load();
            Settings.PropertyChanged += (s, a) => SettingsStore.SaveDebounced(Settings);

            // ⑤ 自启对账：只保留一条通道，避免登录时两个实例抢锁（抢输的往往是高权限那个）
            AutoStart.Ensure(Settings.StartWithWindows, Settings.AutoStartPreferred);

            bool openNow = HasArg(args, "--open") || HasArg(args, "--settings");
            bool showSettings = HasArg(args, "--settings");
            bool noHover = HasArg(args, "--no-hover");

            if (Settings.StartWithWindows && IsLowIntegrity)
            {
                SettingsStore.Log("低权限下无法重写自启项；等权限修好后会自动对账");
            }

            _dock = new DockWindow(Settings);
            _dock.Show();
            _dock.InitializeWindow();

            StartEventListeners();

            _tab = new EdgeTabWindow();
            _tab.Show();
            _tab.InitializeWindow();

            // 壁纸右上角的时间（贴在桌面之上、普通窗口之下）
            try
            {
                _widget = new WidgetWindow(Settings);

                // 先定位/采样壁纸再显示：否则采样会把自己的字算进去
                _widget.InitializeWindow();
                if (Settings.WidgetEnabled) _widget.Show();

                _widgetTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _widgetTimer.Tick += (s, e) =>
                {
                    _widget?.EnsureOnDesktop();
                    _info?.EnsureOnDesktop();
                };
                _widgetTimer.Start();

                SettingsStore.Log("壁纸时间已创建，enabled=" + Settings.WidgetEnabled);
            }
            catch (Exception ex)
            {
                _widget = null;
                SettingsStore.Log("创建壁纸时间失败: " + ex);
            }

            // 壁纸左下角：CPU/RAM/GPU + 正在播放
            try
            {
                _info = new InfoWindow(Settings);
                _info.InitializeWindow();
                if (Settings.InfoHudEnabled) _info.Show();

                SettingsStore.Log("壁纸信息条已创建，enabled=" + Settings.InfoHudEnabled);
            }
            catch (Exception ex)
            {
                _info = null;
                SettingsStore.Log("创建壁纸信息条失败: " + ex);
            }

            // 调试/自检：验证播放控制能否驱动系统媒体会话
            if (HasArg(args, "--media-test")) _info?.MediaSelfTest();

            try
            {
                _tray = new TrayIcon();
            _tray.HudSettingsRequested += () =>
            {
                try { new Views.HudSettingsWindow(Settings).ShowDialog(); }
                catch (Exception ex) { SettingsStore.Log("打开外观设置失败: " + ex.Message); }
            };
                _tray.ToggleRequested += ToggleDock;
                _tray.RefreshRequested += () => _dock.RefreshShortcuts();
                _tray.HideIconsToggled += () =>
                {
                    Settings.HideDesktopIcons = !Settings.HideDesktopIcons;
                    if (!DesktopIcons.SetVisible(!Settings.HideDesktopIcons))
                        _tray.ShowBalloon("桌面收纳盒", "切换桌面图标失败，可右键桌面 → 查看 → 显示桌面图标");
                    _tray.SetHideIconsChecked(Settings.HideDesktopIcons);
                };
                _tray.AutoStartToggled += () =>
                {
                    ToggleAutoStart();
                    _tray.SetAutoStartChecked(Settings.StartWithWindows);
                };
                _tray.ExitRequested += Quit;
                _tray.SetHideIconsChecked(Settings.HideDesktopIcons);
                _tray.SetAutoStartChecked(Settings.StartWithWindows);
                SettingsStore.Log("托盘图标已创建");
            }
            catch (Exception ex)
            {
                _tray = null;
                SettingsStore.Log("创建托盘图标失败: " + ex);
            }

            _hoverTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(45)
            };
            _hoverTimer.Tick += OnHoverTick;
            if (!noHover) _hoverTimer.Start();

            if (Settings.HideDesktopIcons && !DesktopIcons.SetVisible(false))
                Settings.HideDesktopIcons = false;

            SyncEdgeTab();

            // ⑥ 启动后给一次轻量提示：权限不对 / 自启指向旧路径
            if (IsLowIntegrity)
            {
                SettingsStore.Log(
                    "警告：当前以低完整性(Low IL)运行。常见原因只有一个：exe 所在目录被打了 " +
                    "Low 完整性标签（下载目录、沙箱工具留下的 ACL）。" +
                    "打开设置 → 权限与启动 → 『安装到本机并重启』即可修复。");
                _tray?.ShowBalloon("桌面收纳盒", "当前是低权限模式，托盘/自启会失效。右键托盘 → 设置 → 修复。");
            }
            else if (AutoStart.NeedsRepair)
            {
                _tray?.ShowBalloon("桌面收纳盒", "开机自启仍指向旧路径，打开设置点『修复自启』即可。");
            }

            // 调试/自检入口：--open 直接展开，--settings 展开并停在设置页
            if (openNow)
            {
                _dock.OpenDock(ScreenInfo.Primary);
                SyncEdgeTab();
                if (showSettings) _dock.OpenSettings();
            }

            // 调试/自检：直接进入壁纸时间的拖动模式（方便自动化验证）
            if (HasArg(args, "--move-widget")) StartWidgetMove();
        }

        private static bool HasArg(string[] args, string name)
        {
            if (args == null) return false;
            foreach (string a in args)
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ───────────────────────── 单实例 / 跨完整性 ─────────────────────────

        private bool AcquireSingleInstance(bool takeover)
        {
            int attempts = takeover ? 10 : 1;

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                try
                {
                    bool created;
                    _mutex = new Mutex(true, MutexName, out created);

                    if (created)
                    {
                        _ownsMutex = true;
                        return true;
                    }

                    _mutex.Dispose();
                    _mutex = null;
                }
                catch (UnauthorizedAccessException)
                {
                    // 锁被另一个完整性级别持有，连打开都做不到：当作"已有实例"
                    SettingsStore.Log("单实例锁跨完整性访问被拒（已有实例）");
                }
                catch (Exception ex)
                {
                    SettingsStore.Log("单实例锁异常: " + ex.Message);
                }

                if (!takeover) return false;

                SignalExistingToQuit();
                Thread.Sleep(400);
            }

            return false;
        }

        private static void SummonExisting()
        {
            try
            {
                using (EventWaitHandle summon = EventWaitHandle.OpenExisting(SummonEventName))
                {
                    summon.Set();
                }
            }
            catch
            {
                // 事件不存在就退回窗口消息
            }

            try
            {
                Native.PostMessage(new IntPtr(Native.HWND_BROADCAST), Native.WM_DESKTOPDOCK_SHOW,
                    IntPtr.Zero, IntPtr.Zero);
            }
            catch
            {
                // 广播失败也无所谓
            }
        }

        private static void SignalExistingToQuit()
        {
            try
            {
                using (EventWaitHandle quit = EventWaitHandle.OpenExisting(QuitEventName))
                {
                    quit.Set();
                }
            }
            catch
            {
                // 旧版本没有这个事件，忽略
            }
        }

        /// <summary>没有托盘时也能被「再次启动」唤出来：等命名事件；同时接收"请退出"指令</summary>
        private void StartEventListeners()
        {
            try
            {
                _summon = new EventWaitHandle(false, EventResetMode.AutoReset, SummonEventName);
                _quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);

                var thread = new Thread(() =>
                {
                    var handles = new WaitHandle[] { _summon, _quit };

                    while (true)
                    {
                        int index = WaitHandle.WaitAny(handles);

                        if (index == 0)
                        {
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                SettingsStore.Log("收到唤出信号");
                                Summon();
                            }));
                        }
                        else if (index == 1)
                        {
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                SettingsStore.Log("收到退出信号（被新实例接管）");
                                Quit();
                            }));
                        }
                    }
                })
                {
                    IsBackground = true,
                    Name = "DesktopDock.Events"
                };

                thread.Start();
            }
            catch (Exception ex)
            {
                SettingsStore.Log("创建唤出事件失败: " + ex.Message);
            }
        }

        // ───────────────────────── 悬停逻辑 ─────────────────────────

        /// <summary>每 45ms 看一次鼠标：贴左边就展开，离开就按延迟收起。</summary>
        private void OnHoverTick(object sender, EventArgs e)
        {
            if (_dock == null) return;
            if (!Native.GetCursorPos(out Native.POINT pt)) return;

            long now = Environment.TickCount64;

            if (!_dock.IsOpen)
            {
                ScreenInfo mon = ScreenInfo.FromPoint(pt.X, pt.Y);
                bool allowed = IsTriggerAllowed();
                bool inZone = allowed
                              && pt.X <= mon.WorkLeft + Settings.EdgeTriggerWidth
                              && pt.Y >= mon.WorkTop && pt.Y <= mon.WorkBottom;

                if (inZone)
                {
                    if (_hoverSince == 0) _hoverSince = now;
                    else if (now - _hoverSince >= 80)
                    {
                        _hoverSince = 0;
                        _leaveSince = 0;
                        _dock.OpenDock(mon);
                        SyncEdgeTab();
                    }
                }
                else
                {
                    _hoverSince = 0;
                }

                // 前台不在桌面时，把提示条也藏起来，免得「看得见却呼不出」
                if (allowed != _tabAllowed)
                {
                    _tabAllowed = allowed;
                    SyncEdgeTab();
                }

                MoveTab(mon);
            }
            else
            {
                Rect r = _dock.CurrentRectPx;
                bool inside = pt.X >= r.Left - 6 && pt.X <= r.Right + 10
                              && pt.Y >= r.Top - 10 && pt.Y <= r.Bottom + 10;

                if (inside || _dock.Pinned)
                {
                    _leaveSince = 0;
                }
                else
                {
                    if (_leaveSince == 0) _leaveSince = now;
                    else if (now - _leaveSince >= Settings.AutoHideDelayMs)
                    {
                        _leaveSince = 0;
                        _dock.CloseDock();
                        SyncEdgeTab();
                    }
                }
            }
        }

        private void Summon()
        {
            if (_dock == null) return;
            Native.GetCursorPos(out Native.POINT pt);
            _dock.OpenDock(ScreenInfo.FromPoint(pt.X, pt.Y));
            SyncEdgeTab();
        }

        /// <summary>前台不是桌面时不响应贴边（可在设置里关掉这个限制）</summary>
        private bool IsTriggerAllowed()
        {
            return Settings == null
                   || !Settings.OnlyOnDesktop
                   || DesktopHelper.IsDesktopOrSelfActive();
        }

        private void MoveTab(ScreenInfo mon)
        {
            if (_tab == null) return;
            if (_tabDevice == mon.Device) return;
            _tabDevice = mon.Device;
            _tab.MoveTo(mon);
        }

        public void SyncEdgeTab()
        {
            if (_tab == null || _dock == null) return;
            bool visible = Settings.ShowEdgeTab && !_dock.IsOpen && IsTriggerAllowed();
            _tab.SetVisible(visible);
            _tray?.SetOpenState(_dock.IsOpen);
        }

        public void ToggleDock()
        {
            if (_dock == null) return;
            if (_dock.IsOpen)
            {
                _dock.CloseDock();
            }
            else
            {
                Native.GetCursorPos(out Native.POINT pt);
                _dock.OpenDock(ScreenInfo.FromPoint(pt.X, pt.Y));
            }

            SyncEdgeTab();
        }

        // ───────────────────────── 自启与权限修复入口 ─────────────────────────

        private void ToggleAutoStart()
        {
            Settings.StartWithWindows = !Settings.StartWithWindows;

            if (!Settings.StartWithWindows)
            {
                if (!AutoStart.Disable(out string message))
                {
                    // 关不掉（大概率是计划任务需要管理员授权）就把状态恢复，别让菜单显示成已关闭
                    Settings.StartWithWindows = true;
                    _tray?.ShowBalloon("桌面收纳盒", "关闭自启失败：" + message);
                }

                return;
            }

            AutoStartMode mode = string.Equals(Settings.AutoStartPreferred, "task", StringComparison.OrdinalIgnoreCase)
                ? AutoStartMode.Task
                : AutoStartMode.RunKey;

            if (!AutoStart.Enable(mode, out string enableMessage))
                _tray?.ShowBalloon("桌面收纳盒", "开启自启失败：" + enableMessage);
        }

        /// <summary>设置页：安装到本机并重启</summary>
        public void ReinstallAndRestart()
        {
            if (InstallLocation.IsRunningInstalled)
            {
                MessageBox.Show("已经在安装目录里运行：\n" + InstallLocation.Root + "\n\n不需要再迁移。",
                    "桌面收纳盒", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (InstallLocation.MigrateAndRelaunch(Array.Empty<string>(), out string message))
            {
                SettingsStore.Log("手动迁移到本机: " + message);
                Quit();
            }
            else
            {
                MessageBox.Show("迁移失败：\n" + message, "桌面收纳盒", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// 设置页：修复低权限。优先搬到本机目录；已在本机目录就清一次 Low 标签后重启。
        /// </summary>
        public void RepairPermissionAndRestart(bool restartOnly = false)
        {
            if (!restartOnly && !InstallLocation.IsRunningInstalled)
            {
                ReinstallAndRestart();
                return;
            }

            if (InstallLocation.TryRepairLowIntegrityInPlace(Array.Empty<string>(), out string message))
            {
                SettingsStore.Log("清除 Low 标签并重启: " + message);
                Quit();
            }
            else
            {
                MessageBox.Show("修复失败：" + message + "\n\n建议改用『安装到本机并重启』。",
                    "桌面收纳盒", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>设置页：修复自启（清理旧计划任务 + 重写 Run 键，需要一次管理员授权）</summary>
        public bool RepairAutoStart(out string message)
        {
            int code = AutoStart.RunElevatedSelf(AutoStart.RepairArg, out message);
            return code == 0;
        }

        /// <summary>设置页：改用计划任务（最高权限）自启</summary>
        public bool UseScheduledTask(out string message)
        {
            int code = AutoStart.RunElevatedSelf(AutoStart.InstallTaskArg, out message);
            return code == 0;
        }

        /// <summary>设置页：让壁纸时间进入鼠标拖动模式</summary>
        public void StartWidgetMove()
        {
            if (_widget == null) return;

            _widget.BeginMoveMode();
            _tray?.ShowBalloon("桌面收纳盒", "把时间拖到想要的位置，松手保存；30 秒内不拖会自动退出。");
        }

        /// <summary>设置页：打开数据目录</summary>
        public void OpenDataFolder()
        {
            try
            {
                Process.Start(new ProcessStartInfo(SettingsStore.DataDir) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("打不开目录：" + ex.Message, "桌面收纳盒");
            }
        }

        /// <summary>没有托盘时的退出通道（设置面板里的「退出程序」按钮）</summary>
        public void RequestQuit()
        {
            Quit();
        }

        private void Quit()
        {
            _quitting = true;

            try
            {
                _hoverTimer?.Stop();
                _widgetTimer?.Stop();
                _widget?.Close();
                _info?.Close();
                DesktopIcons.RestoreIfHidden();
                _tray?.Dispose();

                if (_ownsMutex)
                {
                    _mutex?.ReleaseMutex();
                    _ownsMutex = false;
                }
            }
            catch
            {
                // 退出路径尽量不抛
            }

            Shutdown();
        }

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            DesktopIcons.RestoreIfHidden();
            base.OnSessionEnding(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (!_quitting) DesktopIcons.RestoreIfHidden();
            try { _mutex?.Dispose(); } catch { }
            base.OnExit(e);
        }
    }
}
