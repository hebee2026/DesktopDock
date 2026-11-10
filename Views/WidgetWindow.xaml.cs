using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopDock.Models;
using DesktopDock.Services;

namespace DesktopDock.Views
{
    /// <summary>
    /// 壁纸右上角的大时间 / 日期：没有底板，字直接压在壁纸上。
    ///
    /// 它不是普通置顶窗口：插在 Progman（桌面）上方、其它窗口下方，
    /// 所以只出现在桌面上，打开软件就被盖住；鼠标点击直接穿透到桌面。
    ///
    /// 字色按壁纸明暗自动切换：亮壁纸用墨色字 + 淡白光晕，暗壁纸用白字 + 暗光晕，
    /// 换壁纸后定时重新采样。字体、字号、显示层级都能在设置里单独调。
    /// </summary>
    public partial class WidgetWindow : Window
    {
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private static readonly CultureInfo Zh = new CultureInfo("zh-CN");
        private static readonly Color Ink = Color.FromRgb(0x1B, 0x1F, 0x27);

        private readonly AppSettings _s;
        private readonly DispatcherTimer _clockTimer;
        private readonly DispatcherTimer _paletteTimer;

        private IntPtr _hwnd;
        private bool _lightBackground = true;
        private bool _placedLogged;
        private bool _moving;
        private DispatcherTimer _moveTimeout;

        public WidgetWindow(AppSettings settings)
        {
            InitializeComponent();
            _s = settings;
            _s.PropertyChanged += OnSettingChanged;

            _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _clockTimer.Tick += (s, e) => UpdateClock();

            _paletteTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(60)
            };
            _paletteTimer.Tick += (s, e) => ResamplePalette();

            MouseLeftButtonDown += OnMouseLeftButtonDown;

            ApplySettings();

            // 构造时窗口还没显示，此刻抓到的是纯壁纸，不会被自己的字干扰
            SampleBackground();
            ApplyPalette();

            // Show() 之后窗口才有真实尺寸：定位 + 按最终尺寸补一次采样
            Loaded += (s, e) =>
            {
                Place();
                SampleBackground();
                ApplyPalette();
            };
        }

        /// <summary>注意：必须在 Show() 之前调用，否则采样会把自己算进去</summary>
        public void InitializeWindow()
        {
            // EnsureHandle 会强制创建 HWND（但不显示）；直接取 Handle 在 Show 之前是 0
            _hwnd = new WindowInteropHelper(this).EnsureHandle();

            Native.MakeToolWindow(_hwnd);
            Native.DisableWindowFrame(_hwnd);

            long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            ex |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));

            Place();
            _clockTimer.Start();
            _paletteTimer.Start();
        }

        // ───────────────────────── 位置 ─────────────────────────

        /// <summary>按设置里的边距摆放；插到 Progman 之上、其它窗口之下</summary>
        public void Place()
        {
            if (_hwnd == IntPtr.Zero || _moving) return;

            ScreenInfo mon = ScreenInfo.Primary;
            double sc = mon.Scale <= 0 ? 1.0 : mon.Scale;

            double dipW = ActualWidth;
            double dipH = ActualHeight;
            if (dipW <= 0 || dipH <= 0) return;

            int w = (int)Math.Ceiling(dipW * sc);
            int marginRight = (int)Math.Round(_s.WidgetMarginRight * sc);
            int marginTop = (int)Math.Round(_s.WidgetMarginTop * sc);

            int x = mon.WorkLeft + mon.WorkWidth - w - marginRight;
            int y = mon.WorkTop + marginTop;

            // 别拖到屏幕外
            if (x < mon.WorkLeft) x = mon.WorkLeft;
            if (y < mon.WorkTop) y = mon.WorkTop;

            IntPtr progman = Native.FindWindow("Progman", null);

            // 同步 WPF 自己的 Left/Top：否则布局一跑窗口会被拉回 XAML 里的 -3000
            double leftDip = x / sc;
            double topDip = y / sc;
            if (Math.Abs(Left - leftDip) > 0.5) Left = leftDip;
            if (Math.Abs(Top - topDip) > 0.5) Top = topDip;

            // 尺寸交给 SizeToContent，这里只挪位置 + 压到桌面正上方
            Native.SetWindowPos(_hwnd, progman == IntPtr.Zero ? Native.HWND_BOTTOM : progman,
                x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

            if (!_placedLogged)
            {
                _placedLogged = true;
                SettingsStore.Log(string.Format("壁纸时间定位: {0}x{1} dip → {2},{3} px (右 {4}/上 {5} dip, scale {6:0.##})",
                    dipW, dipH, x, y, _s.WidgetMarginRight, _s.WidgetMarginTop, sc));
            }
        }

        // ───────────────────────── 拖动定位 ─────────────────────────

        /// <summary>
        /// 进入拖动模式：临时取消鼠标穿透，让用户直接把时间拖到任意位置。
        /// 松手后把新位置换算成边距存下来，30 秒没拖就自动退出。
        /// </summary>
        public void BeginMoveMode()
        {
            if (_hwnd == IntPtr.Zero || _moving) return;

            _moving = true;

            long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            ex &= ~(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));

            IsHitTestVisible = true;
            Cursor = System.Windows.Input.Cursors.SizeAll;
            if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;

            if (_moveTimeout == null)
            {
                _moveTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                _moveTimeout.Tick += (s, e) => EndMoveMode();
            }

            _moveTimeout.Stop();
            _moveTimeout.Start();

            SettingsStore.Log("壁纸时间：进入拖动模式");
        }

        public void EndMoveMode()
        {
            if (!_moving) return;

            _moving = false;
            _moveTimeout?.Stop();

            IsHitTestVisible = false;
            Cursor = System.Windows.Input.Cursors.Arrow;

            if (_hwnd != IntPtr.Zero)
            {
                long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
                ex |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
                Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
            }

            SampleBackground();
            ApplyPalette();
            Place();

            SettingsStore.Log("壁纸时间：退出拖动模式，位置 右 " + _s.WidgetMarginRight + " / 上 " + _s.WidgetMarginTop);
        }

        private void OnMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!_moving) return;

            e.Handled = true;

            try { DragMove(); }
            catch { }

            SavePositionFromWindow();
            EndMoveMode();
        }

        /// <summary>把窗口当前屏幕位置换算成"距右边 / 距顶部"的 DIP 边距</summary>
        private void SavePositionFromWindow()
        {
            try
            {
                if (!Native.GetWindowRect(_hwnd, out Native.RECT rect)) return;

                ScreenInfo mon = ScreenInfo.Primary;
                double sc = mon.Scale <= 0 ? 1.0 : mon.Scale;

                double right = (mon.WorkLeft + mon.WorkWidth - rect.Right) / sc;
                double top = (rect.Top - mon.WorkTop) / sc;

                _s.WidgetMarginRight = Math.Round(Math.Max(0, right));
                _s.WidgetMarginTop = Math.Round(Math.Max(0, top));
            }
            catch (Exception ex)
            {
                SettingsStore.Log("保存壁纸时间位置失败: " + ex.Message);
            }
        }

        /// <summary>桌面被激活时（点桌面 / Win+D）确认自己还在桌面正上方</summary>
        public void EnsureOnDesktop()
        {
            if (_hwnd == IntPtr.Zero || !_s.WidgetEnabled) return;

            IntPtr foreground = Native.GetForegroundWindow();
            if (foreground == IntPtr.Zero) return;

            var buffer = new System.Text.StringBuilder(64);
            if (Native.GetClassName(foreground, buffer, buffer.Capacity) <= 0) return;

            string cls = buffer.ToString();
            if (cls == "Progman" || cls == "WorkerW" || cls == "SHELLDLL_DefView")
                Place();
        }

        // ───────────────────────── 设置 ─────────────────────────

        private void OnSettingChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(AppSettings.WidgetEnabled):
                    Visibility = _s.WidgetEnabled ? Visibility.Visible : Visibility.Collapsed;
                    if (_s.WidgetEnabled) Place();
                    break;

                case nameof(AppSettings.WidgetStyle):
                    ApplyStyle();
                    ApplyPalette();
                    break;

                case nameof(AppSettings.WidgetLod):
                case nameof(AppSettings.WidgetFontSize):
                case nameof(AppSettings.WidgetFontFamilyName):
                    ApplySettings();
                    break;

                case nameof(AppSettings.WidgetCustomColors):
                case nameof(AppSettings.WidgetTextColor):
                case nameof(AppSettings.WidgetSubColor):
                    ApplyPalette();
                    break;

                case nameof(AppSettings.WidgetMarginRight):
                case nameof(AppSettings.WidgetMarginTop):
                    Place();
                    SampleBackground();
                    ApplyPalette();
                    break;

                case nameof(AppSettings.ShowSeconds):
                case nameof(AppSettings.Use24Hour):
                    UpdateClock();
                    break;
            }
        }

        private void ApplySettings()
        {
            TextElement.SetFontFamily(Stack, ResolveFont());

            double baseSize = _s.WidgetFontSize;
            TimeText.FontSize = baseSize;
            SecondsText.FontSize = Math.Max(12, baseSize * 0.34);
            PeriodText.FontSize = Math.Max(11, baseSize * 0.24);
            DateText.FontSize = Math.Max(13, baseSize * 0.30);
            WeekdayText.FontSize = Math.Max(12, baseSize * 0.24);

            ApplyLod();
            UpdateClock();
        }

        private FontFamily ResolveFont()
        {
            string name = string.IsNullOrWhiteSpace(_s.WidgetFontFamilyName)
                ? _s.FontFamilyName
                : _s.WidgetFontFamilyName;

            try
            {
                return new FontFamily(name);
            }
            catch
            {
                return new FontFamily("Microsoft YaHei UI");
            }
        }

        /// <summary>LOD：1 仅时间；2 +日期；3 +星期</summary>
        private void ApplyLod()
        {
            int lod = Math.Max(1, Math.Min(3, _s.WidgetLod));

            DateText.Visibility = lod >= 2 ? Visibility.Visible : Visibility.Collapsed;
            WeekdayText.Visibility = lod >= 3 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ───────────────────────── 内容 ─────────────────────────

        private void UpdateClock()
        {
            DateTime now = DateTime.Now;

            if (_s.Use24Hour)
            {
                TimeText.Text = now.ToString("HH:mm", CultureInfo.InvariantCulture);
                PeriodText.Text = "";
            }
            else
            {
                TimeText.Text = now.ToString("h:mm", CultureInfo.InvariantCulture);
                PeriodText.Text = now.Hour < 12 ? "上午" : "下午";
            }

            SecondsText.Text = _s.ShowSeconds ? now.ToString("ss", CultureInfo.InvariantCulture) : "";
            DateText.Text = now.ToString("M月d日", Zh);
            WeekdayText.Text = now.ToString("dddd", Zh);
        }

        // ───────────────────────── 自动配色 ─────────────────────────

        /// <summary>采样壁纸区域的平均亮度：亮 → 墨色字，暗 → 白字</summary>
        private void ResamplePalette()
        {
            if (_hwnd == IntPtr.Zero || !_s.WidgetEnabled) return;
            if (Visibility != Visibility.Visible) return;

            // 只有桌面在前台时才采（否则抓到的是别的窗口，会把配色带偏）
            IntPtr foreground = Native.GetForegroundWindow();
            var buffer = new System.Text.StringBuilder(64);
            if (foreground == IntPtr.Zero || Native.GetClassName(foreground, buffer, buffer.Capacity) <= 0) return;

            string cls = buffer.ToString();
            if (cls != "Progman" && cls != "WorkerW" && cls != "SHELLDLL_DefView") return;

            SampleBackground();
            ApplyPalette();
        }

        private void SampleBackground()
        {
            try
            {
                ScreenInfo mon = ScreenInfo.Primary;
                double sc = mon.Scale <= 0 ? 1.0 : mon.Scale;

                double dipW = ActualWidth;
                double dipH = ActualHeight;

                // 还没布局过时按字号估一个够用的区域
                if (double.IsNaN(dipW) || dipW <= 0) dipW = Math.Max(220, _s.WidgetFontSize * 5.2);
                if (double.IsNaN(dipH) || dipH <= 0) dipH = Math.Max(90, _s.WidgetFontSize * 2.2);

                int w = (int)Math.Ceiling(dipW * sc);
                int h = (int)Math.Ceiling(dipH * sc);
                int marginRight = (int)Math.Round(_s.WidgetMarginRight * sc);
                int marginTop = (int)Math.Round(_s.WidgetMarginTop * sc);
                int x = mon.WorkLeft + mon.WorkWidth - w - marginRight;
                int y = mon.WorkTop + marginTop;

                if (x < mon.WorkLeft) x = mon.WorkLeft;
                if (y < mon.WorkTop) y = mon.WorkTop;

                BitmapSource shot = GlassCapture.Capture(x, y, w, h);
                if (shot == null) return;

                int stride = shot.PixelWidth * 4;
                var pixels = new byte[stride * shot.PixelHeight];
                shot.CopyPixels(pixels, stride, 0);

                double sum = 0;
                int count = 0;

                // Bgr32：步长 4*37 抽样，够快也够稳
                for (int i = 0; i + 3 < pixels.Length; i += 4 * 37)
                {
                    double lum = (0.114 * pixels[i] + 0.587 * pixels[i + 1] + 0.299 * pixels[i + 2]) / 255.0;
                    sum += lum;
                    count++;
                }

                if (count == 0) return;
                _lightBackground = sum / count > 0.55;
            }
            catch (Exception ex)
            {
                SettingsStore.Log("壁纸亮度采样失败: " + ex.Message);
            }
        }

        private static Color ParseHex(string hex, Color fallback, byte alpha)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(hex))
                {
                    string s = hex.Trim().TrimStart('#');
                    if (s.Length == 6)
                        return Color.FromArgb(alpha,
                            Convert.ToByte(s.Substring(0, 2), 16),
                            Convert.ToByte(s.Substring(2, 2), 16),
                            Convert.ToByte(s.Substring(4, 2), 16));
                }
            }
            catch { }
            return Color.FromArgb(alpha, fallback.R, fallback.G, fallback.B);
        }

        /// <summary>时钟样式：0 横排 / 1 竖排 / 2 极简 / 3 大字</summary>
        private void ApplyStyle()
        {
            if (TimeRow == null) return;

            switch (_s.WidgetStyle)
            {
                case 1:   // 竖排
                    TimeRow.Orientation = System.Windows.Controls.Orientation.Vertical;
                    TimeRow.HorizontalAlignment = HorizontalAlignment.Right;
                    DateText.Visibility = Visibility.Visible;
                    WeekdayText.Visibility = Visibility.Visible;
                    break;

                case 2:   // 极简：只留时间
                    TimeRow.Orientation = System.Windows.Controls.Orientation.Horizontal;
                    TimeRow.HorizontalAlignment = HorizontalAlignment.Right;
                    DateText.Visibility = Visibility.Collapsed;
                    WeekdayText.Visibility = Visibility.Collapsed;
                    break;

                case 3:   // 大字
                    TimeRow.Orientation = System.Windows.Controls.Orientation.Horizontal;
                    TimeRow.HorizontalAlignment = HorizontalAlignment.Right;
                    DateText.Visibility = Visibility.Visible;
                    WeekdayText.Visibility = Visibility.Visible;
                    break;

                default:  // 横排
                    TimeRow.Orientation = System.Windows.Controls.Orientation.Horizontal;
                    TimeRow.HorizontalAlignment = HorizontalAlignment.Right;
                    DateText.Visibility = Visibility.Visible;
                    WeekdayText.Visibility = Visibility.Visible;
                    break;
            }
        }

        private void ApplyPalette()
        {
            bool light = _lightBackground;

            if (_s.WidgetCustomColors)
            {
                Color main = ParseHex(_s.WidgetTextColor, Colors.White, 0xF5);
                Color sub = ParseHex(_s.WidgetSubColor, Colors.White, 0xB8);
                TimeText.Foreground = Solid(main);
                SecondsText.Foreground = Solid(sub);
                PeriodText.Foreground = SecondsText.Foreground;
                DateText.Foreground = Solid(ParseHex(_s.WidgetSubColor, Colors.White, 0xE0));
                WeekdayText.Foreground = Solid(ParseHex(_s.WidgetSubColor, Colors.White, 0xA6));
                Stack.Effect = null;
                return;
            }

            TimeText.Foreground = Solid(light ? Color.FromArgb(0xEE, Ink.R, Ink.G, Ink.B) : Color.FromArgb(0xF5, 255, 255, 255));
            SecondsText.Foreground = Solid(light ? Color.FromArgb(0xA6, Ink.R, Ink.G, Ink.B) : Color.FromArgb(0xB8, 255, 255, 255));
            PeriodText.Foreground = SecondsText.Foreground;
            DateText.Foreground = Solid(light ? Color.FromArgb(0xD9, Ink.R, Ink.G, Ink.B) : Color.FromArgb(0xE0, 255, 255, 255));
            WeekdayText.Foreground = Solid(light ? Color.FromArgb(0x99, Ink.R, Ink.G, Ink.B) : Color.FromArgb(0xA6, 255, 255, 255));

            Stack.Effect = light
                ? new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 16,
                    ShadowDepth = 0,
                    Color = Colors.White,
                    Opacity = 0.55
                }
                : new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 26,
                    ShadowDepth = 0,
                    Color = Colors.Black,
                    Opacity = 0.78
                };
        }

        private static Brush Solid(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
