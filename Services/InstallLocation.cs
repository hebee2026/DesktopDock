using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace DesktopDock.Services
{
    /// <summary>
    /// 程序"装在哪"的管理。
    ///
    /// 背景：Windows 的强制完整性控制（MIC）会让新进程继承**可执行文件的完整性标签**。
    /// 如果 exe 放在被打过 Low 标签的目录里（下载目录、某些沙箱/解压工具留下的 ACL、
    /// 上一级目录带 <c>Mandatory Label\Low Mandatory Level</c> 的情况），那么不管怎么启动——
    /// 双击、启动文件夹、计划任务、甚至 UAC 提升——进程都会被系统钉在 Low IL：
    /// HKCU 注册表、启动文件夹、隐藏桌面图标、ShellExecute 打开文件夹全部被拒。
    /// 表现和安全软件拦截一模一样，很容易误判。
    ///
    /// 对策：把程序自我安装到 <c>%LOCALAPPDATA%\Programs\DesktopDock</c>（用户目录、
    /// 默认 Medium 标签、不会被清理下载目录时误删），从那里重启。安装完顺便清一次标签，
    /// 保证以后开机自启也落在干净的位置上。
    /// </summary>
    public static class InstallLocation
    {
        /// <summary>安装根目录：%LOCALAPPDATA%\Programs\DesktopDock</summary>
        public static string Root
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(local))
                {
                    string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    local = Path.Combine(profile, "AppData", "Local");
                }

                return Path.Combine(local, "Programs", "DesktopDock");
            }
        }

        public static string ExePath => Path.Combine(Root, "DesktopDock.exe");

        /// <summary>安装目录里的 data（便携模式兜底用）</summary>
        public static string PortableDataDir => Path.Combine(Root, "data");

        /// <summary>当前进程是不是就是从安装目录启动的</summary>
        public static bool IsRunningInstalled
        {
            get
            {
                try
                {
                    string current = Environment.ProcessPath;
                    if (string.IsNullOrEmpty(current)) return false;
                    return string.Equals(
                        Path.GetFullPath(current),
                        Path.GetFullPath(ExePath),
                        StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>安装目录是否已经有一份可用副本</summary>
        public static bool IsInstalled
        {
            get
            {
                try { return File.Exists(ExePath); }
                catch { return false; }
            }
        }

        /// <summary>是否应该把当前这份复制到安装目录</summary>
        public static bool ShouldMigrate(string[] args)
        {
            if (HasArg(args, "--portable") || HasArg(args, "--no-migrate")) return false;
            if (IsRunningInstalled) return false;

            string current = Environment.ProcessPath;
            if (string.IsNullOrEmpty(current)) return false;

            // 已经在安装目录里只是文件名大小写不同之类，别自己复制自己
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(current));
                if (string.Equals(dir, Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch
            {
                // 忽略：照常判断
            }

            return true;
        }

        /// <summary>
        /// 复制到安装目录并启动新副本。成功返回 true，调用方应当退出当前进程。
        /// </summary>
        public static bool MigrateAndRelaunch(string[] args, out string message)
        {
            try
            {
                string source = AppContext.BaseDirectory;
                Directory.CreateDirectory(Root);

                // 旧计划任务/启动文件夹可能还指向这份"下载目录副本"。如果本机已经装好
                // 而且更新，就不再覆盖，直接转发过去——避免每次登录都重复复制一遍。
                bool upToDate = IsInstalled && IsInstalledNewerThan(Environment.ProcessPath);

                int copied = 0;
                if (!upToDate)
                {
                    // 正在运行的旧副本会锁住 exe/dll，必须先请它退出，否则 File.Copy 直接抛
                    // "另一个程序正在使用此文件"，结果就是"更新了但没生效"。
                    if (IsInstalled)
                    {
                        SignalRunningInstanceToQuit();
                        WaitUntilUnlocked(ExePath, 6000);
                    }

                    copied = CopyPayload(source, Root);
                    MigrateUserData(source);
                    ClearLowLabel(Root);
                    StripZoneIdentifier(ExePath);
                    TryCreateStartMenuShortcut();
                }

                var psi = new ProcessStartInfo(ExePath)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Root
                };

                foreach (string a in args)
                {
                    if (IsInternalArg(a)) continue;
                    psi.ArgumentList.Add(a);
                }

                // 让新副本去接管单实例（旧副本马上退出）
                psi.ArgumentList.Add("--takeover");

                Process.Start(psi);
                message = upToDate
                    ? "本机副本已是最新，直接转发到 " + Root
                    : "已安装到 " + Root + "（复制 " + copied + " 项）";
                return true;
            }
            catch (Exception ex)
            {
                message = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 低权限兜底：清掉当前目录与 exe 的 Low 标签，然后借一条"更干净的启动路径"重启。
        ///
        /// 注意不能用 Process.Start 直接拉自己：Low 父进程创建的子进程仍然是 Low，
        /// 清了标签也白清。所以优先走计划任务（Task Scheduler 创建进程），没有任务时
        /// 借 explorer.exe 转发（explorer 是 Medium）。
        /// </summary>
        public static bool TryRepairLowIntegrityInPlace(string[] args, out string message)
        {
            try
            {
                if (RecentlyRelaunched())
                {
                    message = "刚刚已经重启过一次，避免循环，本次不再重启";
                    return false;
                }

                string exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe))
                {
                    message = "取不到程序路径";
                    return false;
                }

                string dir = Path.GetDirectoryName(exe);
                bool cleared = false;
                if (!string.IsNullOrEmpty(dir)) cleared |= ClearLowLabel(dir);
                cleared |= ClearLowLabel(exe);
                StripZoneIdentifier(exe);

                if (!cleared)
                {
                    message = "没有权限修改完整性标签（Low 进程无法把标签改高）。"
                              + "请双击程序目录里的『修复权限.bat』。";
                    return false;
                }

                MarkRelaunched();
                WriteTakeoverFlag();

                bool launched = AutoStart.TryRunTask();

                if (!launched)
                {
                    // explorer.exe 是 Medium，由它来启动就不会被文件标签压低
                    var psi = new ProcessStartInfo("explorer.exe", "\"" + exe + "\"")
                    {
                        UseShellExecute = false,
                        WorkingDirectory = dir ?? ""
                    };

                    Process.Start(psi);
                    launched = true;
                }

                message = launched ? "已清除 Low 标签并重新启动" : "标签已清除，但重启失败";
                return launched;
            }
            catch (Exception ex)
            {
                message = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private static string RelaunchGuardFile =>
            Path.Combine(Path.GetTempPath(), "DesktopDock-relaunch-guard.txt");

        private static string TakeoverFlagFile =>
            Path.Combine(Path.GetTempPath(), "DesktopDock-takeover.flag");

        /// <summary>
        /// 借 explorer / 计划任务重启时没法传命令行参数，用一个小旗标告诉新实例
        /// "去接管单实例锁"，否则它会以为已有实例在跑，唤醒一下就退出了。
        /// </summary>
        public static void WriteTakeoverFlag()
        {
            try { File.WriteAllText(TakeoverFlagFile, DateTime.UtcNow.Ticks.ToString()); }
            catch { }
        }

        /// <summary>读取并消费接管旗标（60 秒内有效）</summary>
        public static bool ConsumeTakeoverFlag()
        {
            try
            {
                if (!File.Exists(TakeoverFlagFile)) return false;

                string text = File.ReadAllText(TakeoverFlagFile);
                File.Delete(TakeoverFlagFile);

                if (!long.TryParse(text, out long ticks)) return false;
                return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < TimeSpan.FromSeconds(60);
            }
            catch
            {
                return false;
            }
        }

        private static bool RecentlyRelaunched()
        {
            try
            {
                if (!File.Exists(RelaunchGuardFile)) return false;
                string text = File.ReadAllText(RelaunchGuardFile);
                if (!long.TryParse(text, out long ticks)) return false;
                return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < TimeSpan.FromMinutes(2);
            }
            catch
            {
                return false;
            }
        }

        private static void MarkRelaunched()
        {
            try { File.WriteAllText(RelaunchGuardFile, DateTime.UtcNow.Ticks.ToString()); }
            catch { }
        }


        /// <summary>把目录/文件的完整性标签设为 Medium（需要对该对象有写权限）</summary>
        public static bool ClearLowLabel(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            try
            {
                bool isDir = Directory.Exists(path);
                if (!isDir && !File.Exists(path)) return false;

                string arguments = "\"" + path + "\" /C /Q /setintegritylevel Medium";
                if (isDir) arguments = "\"" + path + "\" /T /C /Q /setintegritylevel Medium";

                var psi = new ProcessStartInfo("icacls.exe", arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (Process process = Process.Start(psi))
                {
                    if (process == null) return false;
                    process.StandardOutput.ReadToEnd();
                    process.StandardError.ReadToEnd();
                    process.WaitForExit(20000);
                    return process.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                SettingsStore.Log("清除完整性标签失败(" + path + "): " + ex.Message);
                return false;
            }
        }

        /// <summary>删除从网上下载时附带的 Zone.Identifier（SmartScreen 提示的来源）</summary>
        public static void StripZoneIdentifier(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                if (Native.DeleteAlternateStream(path, "Zone.Identifier"))
                    SettingsStore.Log("已移除文件的网络来源标记: " + Path.GetFileName(path));
            }
            catch
            {
                // 没有该数据流或没有权限都不影响
            }
        }

        private static bool IsInstalledNewerThan(string sourceExe)
        {
            try
            {
                if (string.IsNullOrEmpty(sourceExe)) return false;
                if (!File.Exists(ExePath) || !File.Exists(sourceExe)) return false;
                return File.GetLastWriteTimeUtc(ExePath) >= File.GetLastWriteTimeUtc(sourceExe);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>让已经在跑的副本自己退出（它收到事件会走正常退出流程，恢复桌面图标等）</summary>
        private static void SignalRunningInstanceToQuit()
        {
            try
            {
                using (EventWaitHandle quit = EventWaitHandle.OpenExisting(@"Local\DesktopDock.Quit"))
                {
                    quit.Set();
                }
            }
            catch
            {
                // 旧版本没有该事件，交给 WaitUntilUnlocked 超时处理
            }
        }

        /// <summary>等 exe 的写锁释放（最多 timeoutMs），复制才不会失败</summary>
        private static bool WaitUntilUnlocked(string path, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();

            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    return true;
                }
                catch (IOException)
                {
                    Thread.Sleep(200);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(200);
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private static int CopyPayload(string source, string destination)
        {
            int count = 0;

            foreach (string file in Directory.GetFiles(source))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.StartsWith("debug", StringComparison.OrdinalIgnoreCase)) continue;

                string target = Path.Combine(destination, name);
                CopyFile(file, target);
                count++;
            }

            foreach (string dir in Directory.GetDirectories(source))
            {
                string name = Path.GetFileName(dir);
                if (string.Equals(name, "data", StringComparison.OrdinalIgnoreCase)) continue;

                string target = Path.Combine(destination, name);
                CopyDirectory(dir, target);
                count++;
            }

            return count;
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (string file in Directory.GetFiles(source))
                CopyFile(file, Path.Combine(destination, Path.GetFileName(file)));

            foreach (string dir in Directory.GetDirectories(source))
                CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }

        private static void CopyFile(string source, string destination)
        {
            try
            {
                if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                    return;

                File.Copy(source, destination, true);
            }
            catch (Exception ex)
            {
                SettingsStore.Log("复制失败 " + Path.GetFileName(source) + ": " + ex.Message);
            }
        }

        /// <summary>把当前正在用的设置/天气缓存带到新位置（只在源文件更新时覆盖）</summary>
        private static void MigrateUserData(string sourceRoot)
        {
            try
            {
                string destination = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopDock");
                Directory.CreateDirectory(destination);

                string sourceData = Path.Combine(sourceRoot, "data");
                foreach (string name in new[] { "settings.json", "weather.json" })
                {
                    string from = Path.Combine(sourceData, name);
                    string to = Path.Combine(destination, name);
                    if (!File.Exists(from)) continue;

                    if (File.Exists(to) && File.GetLastWriteTimeUtc(to) >= File.GetLastWriteTimeUtc(from))
                        continue;

                    File.Copy(from, to, true);
                    SettingsStore.Log("已迁移配置 " + name + " → " + to);
                }
            }
            catch (Exception ex)
            {
                SettingsStore.Log("迁移配置失败: " + ex.Message);
            }
        }

        /// <summary>在开始菜单里放一个入口，方便以后从"所有应用"里找到（失败不影响主流程）</summary>
        public static void TryCreateStartMenuShortcut()
        {
            try
            {
                string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                if (string.IsNullOrWhiteSpace(programs)) return;

                string link = Path.Combine(programs, "桌面收纳盒.lnk");
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;

                object shell = Activator.CreateInstance(shellType);
                object shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { link });
                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { ExePath });
                shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { Root });
                shortcutType.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { ExePath + ",0" });
                shortcutType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "桌面收纳盒" });
                shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
                SettingsStore.Log("已创建开始菜单快捷方式: " + link);
            }
            catch (Exception ex)
            {
                SettingsStore.Log("创建开始菜单快捷方式失败: " + ex.Message);
            }
        }

        private static bool IsInternalArg(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return true;
            return arg.Equals("--takeover", StringComparison.OrdinalIgnoreCase)
                   || arg.Equals("--relaunched", StringComparison.OrdinalIgnoreCase)
                   || arg.StartsWith("--install-autostart", StringComparison.OrdinalIgnoreCase)
                   || arg.StartsWith("--repair-autostart", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasArg(string[] args, string name)
        {
            if (args == null) return false;
            foreach (string a in args)
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>给日志用的简短描述</summary>
        public static string Describe()
        {
            try
            {
                return "exe=" + Environment.ProcessPath
                       + " installed=" + IsRunningInstalled
                       + " target=" + ExePath
                       + " label=" + ReadIntegrityLabel(Environment.ProcessPath);
            }
            catch
            {
                return "unknown";
            }
        }

        /// <summary>用 icacls 读一个文件的完整性标签（只用于诊断日志）</summary>
        public static string ReadIntegrityLabel(string path)
        {
            if (string.IsNullOrEmpty(path)) return "?";

            try
            {
                var psi = new ProcessStartInfo("icacls.exe", "\"" + path + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using (Process process = Process.Start(psi))
                {
                    if (process == null) return "?";
                    string output = process.StandardOutput.ReadToEnd();
                    process.StandardError.ReadToEnd();
                    process.WaitForExit(5000);

                    foreach (string line in output.Split('\n'))
                    {
                        int index = line.IndexOf("Mandatory Label", StringComparison.OrdinalIgnoreCase);
                        if (index < 0) continue;
                        string label = line.Substring(index).Trim();
                        if (label.Contains("Low")) return "Low";
                        if (label.Contains("Medium")) return "Medium";
                        if (label.Contains("High")) return "High";
                        return label;
                    }
                }
            }
            catch
            {
                // 诊断失败无副作用
            }

            return "?";
        }
    }
}
