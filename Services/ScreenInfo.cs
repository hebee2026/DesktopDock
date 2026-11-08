using System;

namespace DesktopDock.Services
{
    /// <summary>一块显示器的物理像素信息 + DPI 缩放</summary>
    public sealed class ScreenInfo
    {
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }
        public int WorkLeft { get; set; }
        public int WorkTop { get; set; }
        public int WorkRight { get; set; }
        public int WorkBottom { get; set; }
        public double Scale { get; set; } = 1.0;
        public string Device { get; set; } = "";

        public int WorkWidth => WorkRight - WorkLeft;
        public int WorkHeight => WorkBottom - WorkTop;

        public static ScreenInfo FromPoint(int x, int y)
        {
            var pt = new Native.POINT { X = x, Y = y };
            return FromMonitor(Native.MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */));
        }

        public static ScreenInfo Primary
        {
            get
            {
                var pt = new Native.POINT { X = 0, Y = 0 };
                var mon = Native.MonitorFromPoint(pt, 1 /* MONITOR_DEFAULTTOPRIMARY */);
                if (mon == IntPtr.Zero) mon = Native.MonitorFromPoint(pt, 2);
                return FromMonitor(mon);
            }
        }

        private static ScreenInfo FromMonitor(IntPtr hmon)
        {
            var info = new Native.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (hmon != IntPtr.Zero && Native.GetMonitorInfo(hmon, ref info))
            {
                double scale = 1.0;
                try
                {
                    uint dx, dy;
                    if (Native.GetDpiForMonitor(hmon, 0 /* MDT_EFFECTIVE_DPI */, out dx, out dy) == 0 && dx > 0)
                        scale = dx / 96.0;
                }
                catch
                {
                    scale = 1.0;
                }

                return new ScreenInfo
                {
                    Left = info.rcMonitor.Left,
                    Top = info.rcMonitor.Top,
                    Right = info.rcMonitor.Right,
                    Bottom = info.rcMonitor.Bottom,
                    WorkLeft = info.rcWork.Left,
                    WorkTop = info.rcWork.Top,
                    WorkRight = info.rcWork.Right,
                    WorkBottom = info.rcWork.Bottom,
                    Scale = scale <= 0 ? 1.0 : scale,
                    Device = info.szDevice ?? ""
                };
            }

            // 兜底：主屏 1920x1080
            return new ScreenInfo { Left = 0, Top = 0, Right = 1920, Bottom = 1080, WorkLeft = 0, WorkTop = 0, WorkRight = 1920, WorkBottom = 1040, Scale = 1.0 };
        }
    }
}
