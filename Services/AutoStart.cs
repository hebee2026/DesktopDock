using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace DesktopDock.Services
{
    public enum AutoStartMode
    {
        None,
        RunKey,
        Task
    }

    /// <summary>
    /// 开机自启的唯一管理者。
    ///
    /// 以前同时存在三条通道（HKCU Run、启动文件夹 .cmd、计划任务），它们会互相打架：
    /// 登录时两个实例同时启动，谁先抢到单实例锁谁留下——留下的那个往往恰好是低权限的，
    /// 于是"开机自启成功、但一直是低权限"。这里统一成一条：
    ///
    ///  - 默认 <b>HKCU Run</b>：不需要管理员、启动即 Medium，足够托盘/自启/桌面图标使用。
    ///  - 可选 <b>计划任务</b>（HighestAvailable）：需要一次 UAC 授权，之后登录静默以最高
    ///    权限启动；任务 XML 里关掉了"用电池时不启动""72 小时后强制结束"这类默认坑。
    ///
    /// 任何时刻只允许一条通道生效，另一条会被主动清理。
    /// </summary>
    public static class AutoStart
    {
        public const string TaskName = "DesktopDock_AutoStart";

        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "DesktopDock";
        private const string StartupLinkName = "桌面收纳盒.lnk";
        private const string StartupCmdName = "DesktopDock.cmd";

        public const string InstallTaskArg = "--install-autostart";
        public const string RepairArg = "--repair-autostart";
        public const string DisableArg = "--disable-autostart";

        /// <summary>自启应该指向的 exe：优先装好的那份</summary>
        public static string TargetExe
        {
            get
            {
                try
                {
                    if (InstallLocation.IsInstalled) return InstallLocation.ExePath;
                }
                catch
                {
                    // 退回当前进程
                }

                return Environment.ProcessPath ?? "";
            }
        }

        // ───────────────────────────── 查询 ─────────────────────────────

        public static bool IsRunKeyInstalled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    return key?.GetValue(RunValueName) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool IsTaskInstalled()
        {
            return QueryTask(out _, out _);
        }

        /// <summary>计划任务里记录的可执行文件路径（读不到返回空串）</summary>
        public static string TaskActionPath
        {
            get
            {
                if (!QueryTask(out string xml, out _)) return "";
                return ExtractCommand(xml);
            }
        }

        public static AutoStartMode Detect()
        {
            if (IsTaskInstalled() && IsTaskEnabled()) return AutoStartMode.Task;
            if (IsRunKeyInstalled()) return AutoStartMode.RunKey;
            return AutoStartMode.None;
        }

        /// <summary>任务被用户在任务计划程序里禁用时不算数，否则会出现"看着有自启、其实不启动"</summary>
        public static bool IsTaskEnabled()
        {
            if (!QueryTask(out string xml, out _)) return false;

            int settings = xml.IndexOf("<Settings>", StringComparison.OrdinalIgnoreCase);
            if (settings < 0) return true;

            int end = xml.IndexOf("</Settings>", settings, StringComparison.OrdinalIgnoreCase);
            string block = end > settings ? xml.Substring(settings, end - settings) : xml.Substring(settings);

            return block.IndexOf("<Enabled>false</Enabled>", StringComparison.OrdinalIgnoreCase) < 0;
        }

        /// <summary>自启配置是否需要一次管理员修复（计划任务指向了别的副本）</summary>
        public static bool NeedsRepair
        {
            get
            {
                if (!IsTaskInstalled()) return false;
                string action = TaskActionPath;
                if (string.IsNullOrEmpty(action)) return false;

                try
                {
                    return !string.Equals(
                        Path.GetFullPath(action),
                        Path.GetFullPath(TargetExe),
                        StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }
        }

        public static string Describe()
        {
            AutoStartMode mode = Detect();
            switch (mode)
            {
                case AutoStartMode.Task:
                    return "计划任务" + (NeedsRepair ? "（指向旧路径，建议修复）" : "");
                case AutoStartMode.RunKey:
                    return "HKCU Run";
                default:
                    return "未开启";
            }
        }

        // ───────────────────────────── 写入 ─────────────────────────────

        /// <summary>
        /// 启动时对账：把"实际状态"拉回到"用户设置"。不弹任何窗口，失败只记日志。
        /// </summary>
        public static void Ensure(bool enabled, string preferred)
        {
            try
            {
                if (!enabled)
                {
                    RemoveRunKey();
                    RemoveStartupFiles();

                    if (IsTaskInstalled())
                    {
                        bool removed = TryRemoveTask(out string error);
                        SettingsStore.Log(removed
                            ? "已删除计划任务自启"
                            : "计划任务自启仍在（需要管理员权限删除）: " + error);
                    }

                    return;
                }

                AutoStartMode mode = Detect();

                if (mode == AutoStartMode.Task)
                {
                    // 计划任务和 Run 键同时存在会导致登录时两个实例抢锁，必须只留一条
                    RemoveRunKey();
                    RemoveStartupFiles();

                    // 任务指向旧副本时，如果当前进程本来就是被这个任务提升起来的，
                    // 就地改写任务（不需要再弹 UAC），下次登录直接落到新位置。
                    if (NeedsRepair && AmElevated && CreateTask(out string repairMessage))
                    {
                        SettingsStore.Log("已把计划任务重指到 " + TargetExe + "（" + repairMessage + "）");
                        return;
                    }

                    if (NeedsRepair)
                        SettingsStore.Log("计划任务指向的仍是旧副本: " + TaskActionPath + "，当前应在 " + TargetExe);
                    else
                        SettingsStore.Log("自启通道：计划任务 → " + TargetExe);

                    return;
                }

                if (mode == AutoStartMode.RunKey)
                {
                    RemoveStartupFiles();
                    if (WriteRunKey(TargetExe)) SettingsStore.Log("自启通道：HKCU Run → " + TargetExe);
                    return;
                }

                // 两条都没有：优先按用户偏好处理，最后用 Run 键（不需要管理员）兜底
                if (string.Equals(preferred, "task", StringComparison.OrdinalIgnoreCase))
                {
                    if (AmElevated && CreateTask(out string created))
                    {
                        SettingsStore.Log("已补建计划任务自启: " + TargetExe + "（" + created + "）");
                        RemoveStartupFiles();
                        return;
                    }

                    SettingsStore.Log("设置里选的是计划任务，但任务不存在（可能需要管理员授权），先退回 HKCU Run");
                }

                if (WriteRunKey(TargetExe)) SettingsStore.Log("自启通道：HKCU Run → " + TargetExe);
                RemoveStartupFiles();
            }
            catch (Exception ex)
            {
                SettingsStore.Log("对账自启状态失败: " + ex.Message);
            }
        }

        /// <summary>打开自启（失败会退回 Run 键）</summary>
        public static bool Enable(AutoStartMode mode, out string message)
        {
            if (mode == AutoStartMode.Task)
            {
                if (AmElevated)
                {
                    if (CreateTask(out message))
                    {
                        RemoveRunKey();
                        RemoveStartupFiles();
                        return true;
                    }
                }
                else
                {
                    int code = RunElevatedSelf(InstallTaskArg, out message);
                    if (code == 0)
                    {
                        RemoveRunKey();
                        RemoveStartupFiles();
                        return true;
                    }

                    if (code == 2) message = "已取消管理员授权";
                }

                // 退回普通权限自启，至少能开机启动。
                // 但如果已经有计划任务在管，就别再插一条 Run 键（会变成两个实例抢锁）。
                if (IsTaskInstalled() && IsTaskEnabled() && !NeedsRepair)
                {
                    message = "当前已由计划任务负责自启";
                    return true;
                }

                if (WriteRunKey(TargetExe))
                {
                    message = (message ?? "") + "\n已改用普通权限自启（HKCU Run）。";
                    return true;
                }

                return false;
            }

            // 普通权限模式：如果已经由计划任务接管，就别再插一条 Run 键
            // （两条同时存在 = 登录时两个实例抢锁，正是之前"低权限"的根源）
            if (IsTaskInstalled() && IsTaskEnabled() && !NeedsRepair)
            {
                RemoveRunKey();
                RemoveStartupFiles();
                message = "当前已由计划任务负责自启，无需再写注册表";
                return true;
            }

            if (WriteRunKey(TargetExe))
            {
                RemoveStartupFiles();
                message = "已写入 HKCU Run";
                return true;
            }

            message = "写入注册表失败";
            return false;
        }

        public static bool Disable(out string message)
        {
            RemoveRunKey();
            RemoveStartupFiles();

            if (!IsTaskInstalled())
            {
                message = "已关闭";
                return true;
            }

            string error = "";
            if (TryRemoveTask(out error))
            {
                message = "已关闭";
                return true;
            }

            if (RunElevatedSelf(DisableArg, out error) == 0)
            {
                message = "已关闭";
                return true;
            }

            message = "计划任务需要管理员权限才能删除：" + error;
            return false;
        }

        public static bool WriteRunKey(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return false;

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null) return false;
                    key.SetValue(RunValueName, "\"" + exe + "\"", RegistryValueKind.String);
                    return true;
                }
            }
            catch (Exception ex)
            {
                SettingsStore.Log("写入启动项注册表失败: " + ex.Message);
                return false;
            }
        }

        public static void RemoveRunKey()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key?.GetValue(RunValueName) != null)
                    {
                        key.DeleteValue(RunValueName, false);
                        SettingsStore.Log("已移除 HKCU Run 启动项");
                    }
                }
            }
            catch (Exception ex)
            {
                SettingsStore.Log("移除启动项失败: " + ex.Message);
            }
        }

        public static void RemoveStartupFiles()
        {
            try
            {
                string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                if (string.IsNullOrWhiteSpace(startup)) return;

                foreach (string name in new[] { StartupLinkName, StartupCmdName })
                {
                    string file = Path.Combine(startup, name);
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                        SettingsStore.Log("已清理启动文件夹残留: " + file);
                    }
                }
            }
            catch (Exception ex)
            {
                SettingsStore.Log("清理启动文件夹失败: " + ex.Message);
            }
        }

        // ───────────────────────────── 计划任务 ─────────────────────────────

        /// <summary>创建（覆盖）计划任务。必须已有管理员权限。</summary>
        public static bool CreateTask(out string message)
        {
            string xmlPath = Path.Combine(Path.GetTempPath(), "DesktopDock-AutoStart.xml");

            try
            {
                string exe = TargetExe;
                if (string.IsNullOrEmpty(exe))
                {
                    message = "取不到程序路径";
                    return false;
                }

                string user = WindowsIdentity.GetCurrent().Name;
                File.WriteAllText(xmlPath, BuildTaskXml(user, exe), Encoding.Unicode);

                var psi = new ProcessStartInfo("schtasks.exe",
                    "/create /tn \"" + TaskName + "\" /xml \"" + xmlPath + "\" /f")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using (Process process = Process.Start(psi))
                {
                    if (process == null)
                    {
                        message = "无法启动 schtasks";
                        return false;
                    }

                    string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                    process.WaitForExit(20000);

                    if (process.ExitCode == 0 && IsTaskInstalled())
                    {
                        SettingsStore.Log("已创建计划任务自启: " + exe);
                        message = "已创建计划任务";
                        return true;
                    }

                    SettingsStore.Log("创建计划任务失败: " + output);
                    message = string.IsNullOrWhiteSpace(output) ? "schtasks 退出码 " + process.ExitCode : output.Trim();
                    return false;
                }
            }
            catch (Exception ex)
            {
                SettingsStore.Log("创建计划任务异常: " + ex.Message);
                message = ex.Message;
                return false;
            }
            finally
            {
                try { if (File.Exists(xmlPath)) File.Delete(xmlPath); } catch { }
            }
        }

        /// <summary>
        /// 任务 XML —— 逐项避开 schtasks 的默认坑：
        /// 电池上不启动、切到电池就杀进程、72 小时执行时限，都会让"自启看起来时灵时不灵"。
        /// </summary>
        public static string BuildTaskXml(string userId, string exe)
        {
            string dir = "";
            try { dir = Path.GetDirectoryName(exe) ?? ""; } catch { }

            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n"
                   + "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n"
                   + "  <RegistrationInfo>\r\n"
                   + "    <Description>桌面收纳盒 · 登录后自动启动</Description>\r\n"
                   + "    <Author>DesktopDock</Author>\r\n"
                   + "  </RegistrationInfo>\r\n"
                   + "  <Triggers>\r\n"
                   + "    <LogonTrigger>\r\n"
                   + "      <Enabled>true</Enabled>\r\n"
                   + "      <Delay>PT10S</Delay>\r\n"
                   + "    </LogonTrigger>\r\n"
                   + "  </Triggers>\r\n"
                   + "  <Principals>\r\n"
                   + "    <Principal id=\"Author\">\r\n"
                   + "      <UserId>" + SecurityElement.Escape(userId) + "</UserId>\r\n"
                   + "      <LogonType>InteractiveToken</LogonType>\r\n"
                   + "      <RunLevel>HighestAvailable</RunLevel>\r\n"
                   + "    </Principal>\r\n"
                   + "  </Principals>\r\n"
                   + "  <Settings>\r\n"
                   + "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n"
                   + "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n"
                   + "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n"
                   + "    <AllowHardTerminate>true</AllowHardTerminate>\r\n"
                   + "    <StartWhenAvailable>true</StartWhenAvailable>\r\n"
                   + "    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\r\n"
                   + "    <IdleSettings>\r\n"
                   + "      <StopOnIdleEnd>false</StopOnIdleEnd>\r\n"
                   + "      <RestartOnIdle>false</RestartOnIdle>\r\n"
                   + "    </IdleSettings>\r\n"
                   + "    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n"
                   + "    <Enabled>true</Enabled>\r\n"
                   + "    <Hidden>false</Hidden>\r\n"
                   + "    <RunOnlyIfIdle>false</RunOnlyIfIdle>\r\n"
                   + "    <WakeToRun>false</WakeToRun>\r\n"
                   + "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n"
                   + "    <Priority>7</Priority>\r\n"
                   + "    <RestartOnFailure>\r\n"
                   + "      <Interval>PT1M</Interval>\r\n"
                   + "      <Count>3</Count>\r\n"
                   + "    </RestartOnFailure>\r\n"
                   + "  </Settings>\r\n"
                   + "  <Actions Context=\"Author\">\r\n"
                   + "    <Exec>\r\n"
                   + "      <Command>" + SecurityElement.Escape(exe) + "</Command>\r\n"
                   + "      <WorkingDirectory>" + SecurityElement.Escape(dir) + "</WorkingDirectory>\r\n"
                   + "    </Exec>\r\n"
                   + "  </Actions>\r\n"
                   + "</Task>\r\n";
        }

        public static bool TryRemoveTask(out string message)
        {
            message = "";
            if (!IsTaskInstalled()) return true;

            try
            {
                using (Process process = StartTaskScheduler("/delete /tn \"" + TaskName + "\" /f"))
                {
                    if (process == null)
                    {
                        message = "无法启动 schtasks";
                        return false;
                    }

                    string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                    process.WaitForExit(15000);

                    if (process.ExitCode == 0 && !IsTaskInstalled())
                    {
                        SettingsStore.Log("已删除计划任务: " + TaskName);
                        return true;
                    }

                    message = output.Trim();
                    return false;
                }
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        /// <summary>让计划任务立刻跑一次（低权限时用来借道任务拿到干净权限）</summary>
        public static bool TryRunTask()
        {
            if (!IsTaskInstalled()) return false;

            try
            {
                using (Process process = StartTaskScheduler("/run /tn \"" + TaskName + "\""))
                {
                    if (process == null) return false;
                    process.StandardOutput.ReadToEnd();
                    process.StandardError.ReadToEnd();
                    process.WaitForExit(8000);
                    return process.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        // ───────────────────────────── 提升 ─────────────────────────────

        /// <summary>当前进程是否已提升（管理员完整令牌）</summary>
        public static bool AmElevated
        {
            get
            {
                try
                {
                    using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    {
                        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                    }
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// 以管理员身份重启自己并等待结果（会弹一次 UAC）。
        /// 返回 0 = 成功，2 = 用户取消，其它 = 失败。
        /// </summary>
        public static int RunElevatedSelf(string arguments, out string message)
        {
            message = "";

            try
            {
                string exe = InstallLocation.IsInstalled ? InstallLocation.ExePath : (Environment.ProcessPath ?? "");
                if (string.IsNullOrEmpty(exe))
                {
                    message = "取不到程序路径";
                    return 3;
                }

                var info = new Native.SHELLEXECUTEINFO
                {
                    cbSize = Marshal.SizeOf(typeof(Native.SHELLEXECUTEINFO)),
                    fMask = Native.SEE_MASK_NOCLOSEPROCESS | Native.SEE_MASK_NOASYNC,
                    lpVerb = "runas",
                    lpFile = exe,
                    lpParameters = arguments,
                    lpDirectory = Path.GetDirectoryName(exe) ?? "",
                    nShow = Native.SW_SHOWNORMAL
                };

                if (!Native.ShellExecuteEx(ref info))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 1223)
                    {
                        message = "已取消管理员授权";
                        return 2;
                    }

                    message = "启动提升进程失败，错误码 " + error;
                    return 3;
                }

                if (info.hProcess == IntPtr.Zero)
                {
                    message = "提升进程已启动，但拿不到句柄";
                    return 3;
                }

                try
                {
                    Native.WaitForSingleObject(info.hProcess, 90000);
                    if (!Native.GetExitCodeProcess(info.hProcess, out uint code))
                    {
                        message = "无法读取提升进程退出码";
                        return 3;
                    }

                    message = "提升进程退出码 " + code;
                    return (int)code;
                }
                finally
                {
                    Native.CloseHandle(info.hProcess);
                }
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return 3;
            }
        }

        // ───────────────────────────── 内部 ─────────────────────────────

        private static Process StartTaskScheduler(string arguments)
        {
            var psi = new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            return Process.Start(psi);
        }

        private static bool QueryTask(out string xml, out string error)
        {
            xml = "";
            error = "";

            try
            {
                using (Process process = StartTaskScheduler("/query /tn \"" + TaskName + "\" /xml"))
                {
                    if (process == null) return false;

                    xml = process.StandardOutput.ReadToEnd();
                    error = process.StandardError.ReadToEnd();
                    process.WaitForExit(8000);
                    return process.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string ExtractCommand(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return "";

            int start = xml.IndexOf("<Command>", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return "";

            start += "<Command>".Length;
            int end = xml.IndexOf("</Command>", start, StringComparison.OrdinalIgnoreCase);
            if (end < 0) return "";

            string raw = xml.Substring(start, end - start).Trim();
            return raw.Trim('"');
        }

        /// <summary>给提升后的助手进程用：创建计划任务</summary>
        public static int InstallTaskHelper()
        {
            try
            {
                if (!AmElevated)
                {
                    SettingsStore.Log("助手进程没有管理员权限，放弃创建计划任务");
                    return 4;
                }

                if (!CreateTask(out string message))
                {
                    SettingsStore.Log("助手创建计划任务失败: " + message);
                    return 5;
                }

                RemoveRunKey();
                RemoveStartupFiles();
                return 0;
            }
            catch (Exception ex)
            {
                SettingsStore.Log("助手进程异常: " + ex);
                return 6;
            }
        }

        /// <summary>给提升后的助手进程用：清理所有自启痕迹（用于"修复"）</summary>
        public static int RepairHelper()
        {
            try
            {
                if (!AmElevated)
                {
                    SettingsStore.Log("助手进程没有管理员权限，放弃修复自启");
                    return 4;
                }

                TryRemoveTask(out string error);
                RemoveStartupFiles();
                RemoveRunKey();

                if (IsTaskInstalled())
                {
                    SettingsStore.Log("修复失败，任务仍存在: " + error);
                    return 5;
                }

                if (!WriteRunKey(TargetExe))
                {
                    SettingsStore.Log("修复后写入 HKCU Run 失败");
                    return 6;
                }

                SettingsStore.Log("自启已修复为 HKCU Run → " + TargetExe);
                return 0;
            }
            catch (Exception ex)
            {
                SettingsStore.Log("助手进程异常: " + ex);
                return 7;
            }
        }

        /// <summary>给提升后的助手进程用：彻底关闭自启（删任务、删 Run 键、清启动文件夹）</summary>
        public static int DisableHelper()
        {
            try
            {
                if (!AmElevated)
                {
                    SettingsStore.Log("助手进程没有管理员权限，放弃关闭自启");
                    return 4;
                }

                TryRemoveTask(out string error);
                RemoveStartupFiles();
                RemoveRunKey();

                if (IsTaskInstalled())
                {
                    SettingsStore.Log("关闭自启失败，任务仍存在: " + error);
                    return 5;
                }

                SettingsStore.Log("已彻底关闭自启（任务/Run/启动文件夹都已清理）");
                return 0;
            }
            catch (Exception ex)
            {
                SettingsStore.Log("助手进程异常: " + ex);
                return 6;
            }
        }
    }
}
