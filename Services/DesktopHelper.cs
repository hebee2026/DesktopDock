using System;
using System.Text;

namespace DesktopDock.Services
{
    /// <summary>判断当前前台窗口是不是桌面 / 我们自己的窗口。</summary>
    public static class DesktopHelper
    {
        private static readonly string[] DesktopClasses =
        {
            "Progman",
            "WorkerW",
            "SHELLDLL_DefView",
            "SysListView32"
        };

        public static bool IsDesktopOrSelfActive()
        {
            try
            {
                IntPtr foreground = Native.GetForegroundWindow();
                if (foreground == IntPtr.Zero) return true;

                var buffer = new StringBuilder(256);
                if (Native.GetClassName(foreground, buffer, buffer.Capacity) <= 0) return true;

                string cls = buffer.ToString();

                // 自己弹出的面板也算
                if (cls.StartsWith("HwndWrapper", StringComparison.Ordinal)) return true;

                foreach (string desktopClass in DesktopClasses)
                {
                    if (string.Equals(cls, desktopClass, StringComparison.Ordinal)) return true;
                }

                return false;
            }
            catch
            {
                // 判断失败就放行，避免功能整体失效
                return true;
            }
        }
    }
}
