using System;
using System.Runtime.InteropServices;

namespace DesktopDock.Services
{
    /// <summary>
    /// 读自己（或别的进程）的令牌完整性级别，用来诊断「托盘/注册表被拒」这类问题。
    /// Low 基本只可能来自：安全软件的受限沙箱、或者 exe 所在目录被打了 Low 完整性标签。
    /// </summary>
    public static class IntegrityLevel
    {
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr buffer, int length, out int returned);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr stringSid);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr handle);

        private const uint TokenQuery = 0x0008;
        private const int TokenIntegrityLevelClass = 25;
        private const uint ProcessQueryLimitedInformation = 0x1000;

        public static string Describe()
        {
            try
            {
                return FromToken(GetCurrentProcess());
            }
            catch
            {
                return "unknown";
            }
        }

        /// <summary>当前是否被钉在 Low IL（托盘、HKCU、桌面图标、ShellExecute 都会被拒）</summary>
        public static bool IsLow => string.Equals(Describe(), "Low", StringComparison.OrdinalIgnoreCase);

        /// <summary>读指定进程的完整性级别（读不到返回 unknown）</summary>
        public static string OfProcess(int pid)
        {
            if (pid <= 0) return "unknown";

            IntPtr process = IntPtr.Zero;
            try
            {
                process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
                if (process == IntPtr.Zero) return "unknown";
                return FromToken(process);
            }
            catch
            {
                return "unknown";
            }
            finally
            {
                if (process != IntPtr.Zero) CloseHandle(process);
            }
        }

        private static string FromToken(IntPtr process)
        {
            IntPtr token;
            if (!OpenProcessToken(process, TokenQuery, out token)) return "unknown";

            try
            {
                int size;
                GetTokenInformation(token, TokenIntegrityLevelClass, IntPtr.Zero, 0, out size);
                if (size <= 0) return "unknown";

                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!GetTokenInformation(token, TokenIntegrityLevelClass, buffer, size, out size)) return "unknown";

                    IntPtr sid = Marshal.ReadIntPtr(buffer);   // TOKEN_MANDATORY_LABEL.Label.Sid
                    IntPtr textPtr;
                    if (!ConvertSidToStringSidW(sid, out textPtr)) return "unknown";

                    try
                    {
                        string sidText = Marshal.PtrToStringUni(textPtr);
                        switch (sidText)
                        {
                            case "S-1-16-0": return "Untrusted";
                            case "S-1-16-4096": return "Low";
                            case "S-1-16-8192": return "Medium";
                            case "S-1-16-12288": return "High";
                            case "S-1-16-16384": return "System";
                            default: return sidText;
                        }
                    }
                    finally
                    {
                        LocalFree(textPtr);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }
    }
}
