using System;

namespace DesktopDock.Services
{
    /// <summary>开关「显示桌面图标」——通过隐藏桌面的 SysListView32 实现，可逆。</summary>
    public static class DesktopIcons
    {
        private static bool _hiddenByUs;

        public static bool SetVisible(bool visible)
        {
            // 低完整性进程给 explorer 的窗口发消息会被 UIPI 拦掉，先挡掉无意义的尝试
            if (App.Integrity == "Low")
            {
                SettingsStore.Log("低权限(Low IL)：无法切换桌面图标（UIPI 拦截）");
                return false;
            }

            try
            {
                IntPtr progman = Native.FindWindow("Progman", null);
                IntPtr defView = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);

                if (defView == IntPtr.Zero)
                {
                    Native.EnumWindows((h, l) =>
                    {
                        IntPtr dv = Native.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                        if (dv == IntPtr.Zero) return true;
                        defView = dv;
                        return false;
                    }, IntPtr.Zero);
                }

                if (defView == IntPtr.Zero) return false;

                IntPtr listView = Native.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
                if (listView == IntPtr.Zero) return false;

                Native.ShowWindow(listView, visible ? Native.SW_SHOW : Native.SW_HIDE);
                _hiddenByUs = !visible;
                return true;
            }
            catch (Exception ex)
            {
                SettingsStore.Log("切换桌面图标失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>退出时把我们隐藏掉的图标还回去，避免用户桌面突然空了</summary>
        public static void RestoreIfHidden()
        {
            if (!_hiddenByUs) return;
            SetVisible(true);
        }
    }
}
