using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;

namespace DesktopDock.Services
{
    /// <summary>
    /// 兜底取歌名：有些播放器（QQ音乐、酷狗等）压根不注册 GSMTC，
    /// 但它们的窗口标题就是「歌名 - 歌手」，直接读标题比读系统会话更可靠。
    /// </summary>
    public static class WindowTitleReader
    {
        private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        /// <summary>认得的播放器（进程名，不带 .exe）</summary>
        public static readonly string[] Players =
        {
            "QQMusic",
            "CloudMusic",
            "KuGou",
            "KwMusic",
            "PotPlayerMini64",
            "PotPlayerMini",
        };

        /// <summary>
        /// 从播放器窗口标题解析「歌名 - 歌手」。
        /// 返回 false 表示没有可用的标题（没开播放器 / 标题里没有分隔符）。
        /// </summary>
        public static bool TryGetTrack(out string title, out string artist)
        {
            title = "";
            artist = "";

            foreach (string name in Players)
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(name); }
                catch { continue; }

                foreach (Process p in procs)
                {
                    try
                    {
                        if (!TryParse(FindTitle(p.Id, name), out title, out artist)) continue;
                        return true;
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            return false;
        }

        private static string FindTitle(int pid, string appName)
        {
            string result = "";
            EnumWindows((h, l) =>
            {
                uint owner;
                GetWindowThreadProcessId(h, out owner);
                if (owner != (uint)pid || !IsWindowVisible(h)) return true;

                int len = GetWindowTextLength(h);
                if (len <= 0) return true;

                var sb = new StringBuilder(len + 2);
                GetWindowText(h, sb, len + 2);
                string text = sb.ToString().Trim();
                if (text.Length == 0) return true;

                // 只要带分隔符的（标题 = 歌名 - 歌手），纯应用名不要
                if (text.Contains(" - ")) { result = text; return false; }
                return true;
            }, IntPtr.Zero);

            return result;
        }

        private static bool TryParse(string raw, out string title, out string artist)
        {
            title = "";
            artist = "";

            if (string.IsNullOrWhiteSpace(raw)) return false;

            int idx = raw.LastIndexOf(" - ", StringComparison.Ordinal);
            if (idx <= 0 || idx + 3 >= raw.Length) return false;

            title = raw.Substring(0, idx).Trim();
            artist = raw.Substring(idx + 3).Trim();

            // 过滤明显的非曲目标题
            if (title.Length == 0) return false;
            if (title.Contains("QQ音乐") && artist.Length == 0) return false;
            return true;
        }
    }
}
