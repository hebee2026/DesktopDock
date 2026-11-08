using System;
using System.Windows;
using System.Windows.Interop;
using DesktopDock.Services;

namespace DesktopDock.Views
{
    /// <summary>屏幕最左侧那条 7px 的发光提示条：纯视觉提示，鼠标可直接穿透。</summary>
    public partial class EdgeTabWindow : Window
    {
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private IntPtr _hwnd;
        private string _device = "";
        private bool _visible = true;

        public EdgeTabWindow()
        {
            InitializeComponent();
            Width = 7;
            Height = 148;
        }

        public void InitializeWindow()
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(_hwnd);
            Native.DisableWindowFrame(_hwnd);

            long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            ex |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));

            MoveTo(ScreenInfo.Primary);
            SetVisible(true);
        }

        public void SetVisible(bool visible)
        {
            _visible = visible;
            if (_hwnd == IntPtr.Zero) return;

            double sc = _scale <= 0 ? 1.0 : _scale;
            int w = (int)Math.Round(Width * sc);
            int h = (int)Math.Round(Height * sc);
            int x = visible ? _left : _left - w - 30;
            int y = _top + Math.Max(0, (_workHeight - h) / 2);

            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE);
        }

        private int _left, _top, _workHeight;
        private double _scale = 1.0;

        public void MoveTo(ScreenInfo mon)
        {
            if (_hwnd == IntPtr.Zero) return;
            if (_device == mon.Device && _left == mon.WorkLeft) return;

            _device = mon.Device;
            _left = mon.WorkLeft;
            _top = mon.WorkTop;
            _workHeight = mon.WorkHeight;
            _scale = mon.Scale;
            SetVisible(_visible);
        }
    }
}
