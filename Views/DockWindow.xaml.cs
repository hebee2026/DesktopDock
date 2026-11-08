using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopDock.Models;
using DesktopDock.Services;

namespace DesktopDock.Views
{
    public partial class DockWindow : Window
    {
        private static readonly CultureInfo Zh = new CultureInfo("zh-CN");
        private static readonly FontFamily SymbolFont = new FontFamily("Segoe UI Symbol");
        private static readonly Brush MutedBrush = Solid(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        private static readonly Brush SoftBrush = Solid(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF));
        private static readonly Brush AccentBrush = Solid(Color.FromRgb(0xE8, 0xC7, 0x9B));

        private readonly AppSettings _s;
        private readonly WeatherService _weather = new WeatherService();
        private readonly ShortcutScanner _scanner = new ShortcutScanner();
        private readonly List<ShortcutTile> _tiles = new List<ShortcutTile>();
        private readonly List<ShortcutTile> _folderTiles = new List<ShortcutTile>();
        private readonly List<Button> _lodButtons = new List<Button>();
        private readonly List<Button> _widgetLodButtons = new List<Button>();
        private readonly Dictionary<string, Brush> _brushCache = new Dictionary<string, Brush>();
        private readonly DispatcherTimer _clockTimer;

        private List<ShortcutItem> _items = new List<ShortcutItem>();
        private DispatcherTimer _glassTimer;
        private DispatcherTimer _hideTimer;
        private IntPtr _hwnd;
        private bool _open;
        private bool _pinned;
        private bool _suppress;
        private bool _hasForecast;
        private bool _weatherBusy;
        private bool _glassReady;
        private DateTime _lastWeatherFetch = DateTime.MinValue;
        private WeatherInfo _weatherInfo;
        private ScreenInfo _mon = ScreenInfo.Primary;
        private string _folderPath;
        private string _folderRoot;

        public DockWindow(AppSettings settings)
        {
            InitializeComponent();
            _s = settings;
            _s.PropertyChanged += OnSettingChanged;

            _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _clockTimer.Tick += (s, e) => UpdateClock();
            _clockTimer.Start();

            _scanner.Changed += () => Dispatcher.BeginInvoke(new Action(RefreshShortcuts));

            ApplySettings(true);

            PreviewKeyDown += OnPreviewKeyDown;
        }

        private bool _settingsBuilt;

        /// <summary>设置面板较重（要枚举系统字体），首次打开时才构建</summary>
        public void OpenSettings()
        {
            EnsureSettingsPanel();
            SettingsLayer.Visibility = Visibility.Visible;
        }

        private void EnsureSettingsPanel()
        {
            if (_settingsBuilt) return;
            _settingsBuilt = true;
            BuildSettingsPanel();
            TextElement.SetFontFamily(SettingsLayer, ResolveFont(_s.FontFamilyName));
        }

        private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != Native.WM_DESKTOPDOCK_SHOW) return IntPtr.Zero;

            handled = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Native.GetCursorPos(out Native.POINT pt))
                    _mon = ScreenInfo.FromPoint(pt.X, pt.Y);

                OpenDock(_mon);
                if (App.Current is App app) app.SyncEdgeTab();
            }));

            return IntPtr.Zero;
        }

        public bool IsOpen => _open;

        public bool Pinned => _pinned;

        /// <summary>窗口当前物理像素矩形（触发/收起判定用）</summary>
        public Rect CurrentRectPx { get; private set; }

        public void InitializeWindow()
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(_hwnd);
            Native.DisableWindowFrame(_hwnd);

            // 接住「第二个实例发来的唤出请求」
            HwndSource source = HwndSource.FromHwnd(_hwnd);
            if (source != null) source.AddHook(WndProcHook);

            _mon = ScreenInfo.Primary;
            ApplyGeometry(_mon);
            PlaceWindow(_mon);
            SlideX.X = -Width - 60;
            Native.ShowWindow(_hwnd, Native.SW_HIDE);

            _scanner.StartWatching();
            RefreshShortcuts();

            var cached = WeatherService.LoadCache();
            if (cached != null) ApplyWeather(cached);

            UpdateClock();
            _ = RefreshWeatherAsync(false);
        }

        // ───────────────────────── 展开 / 收起 ─────────────────────────

        public void OpenDock(ScreenInfo mon)
        {
            if (_open) return;
            _open = true;
            _mon = mon;
            ApplyGeometry(mon);
            PlaceWindow(mon);
            HideFolder();
            SettingsStore.Log(string.Format(
                "OPEN mon={0} scale={1:0.##} work={2},{3} {4}x{5} win={6:0.#}x{7:0.#} glass={8}",
                mon.Device, mon.Scale, mon.WorkLeft, mon.WorkTop, mon.WorkWidth, mon.WorkHeight,
                Width, Height, _s.GlassEffect));

            // 先把面板摆到屏幕外（此时内容在窗口外、完全透明，不遮挡桌面），
            // 抓一张背后桌面的毛玻璃底图，再开始滑入。
            SlideX.BeginAnimation(TranslateTransform.XProperty, null);
            SlideX.X = -Width - 60;
            CaptureGlass(mon);
            ShowWindowSafe();

            SlideTo(0, 220, EasingMode.EaseOut);
            _clockTimer.Start();
            UpdateClock();
            _ = RefreshWeatherAsync(false);
        }

        public void CloseDock()
        {
            if (!_open) return;
            _open = false;
            SettingsStore.Log("CLOSE");
            SlideTo(-Width - 60, 170, EasingMode.EaseIn, () => HideAfterAnimation(70));
            _clockTimer.Stop();
        }

        public void ToggleDock(ScreenInfo mon)
        {
            if (_open) CloseDock();
            else OpenDock(mon);
        }

        public void SetPinned(bool pinned)
        {
            _pinned = pinned;
            RefreshPinVisual();
        }

        /// <summary>
        /// 纯 GPU 合成的滑入/滑出：只改 TranslateTransform，不移动窗口、不触发布局。
        /// WPF 动画跟合成时钟同步，刷新率多高就渲染多少帧。
        /// 注意：不要在动画中途抽掉内容/折叠面板，最后一帧来不及提交会留残影；
        /// 收完由 <see cref="HideAfterAnimation"/> 直接把窗口隐藏，彻底离开合成层。
        /// </summary>
        private void SlideTo(double target, int durationMs, EasingMode easing, Action completed = null)
        {
            double from = SlideX.X;
            SlideX.BeginAnimation(TranslateTransform.XProperty, null);
            SlideX.X = target;

            var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = new CubicEase { EasingMode = easing },
                FillBehavior = FillBehavior.Stop
            };

            if (completed != null) animation.Completed += (s, e) => completed();

            SlideX.BeginAnimation(TranslateTransform.XProperty, animation);
        }

        /// <summary>动画播完再隐藏窗口：多留一点时间确保最后一帧已提交给合成器</summary>
        private void HideAfterAnimation(int delayMs = 60)
        {
            if (_hideTimer == null)
            {
                _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
                _hideTimer.Tick += (s, e) =>
                {
                    _hideTimer.Stop();
                    if (_open) return;                 // 这期间又被呼出，就别藏了
                    if (_hwnd != IntPtr.Zero) Native.ShowWindow(_hwnd, Native.SW_HIDE);
                };
            }
            else
            {
                _hideTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
            }

            _hideTimer.Stop();
            _hideTimer.Start();
        }

        private void ShowWindowSafe()
        {
            if (_hideTimer != null) _hideTimer.Stop();
            if (_hwnd != IntPtr.Zero) Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
        }

        /// <summary>窗口固定在屏幕左边缘，永不逐帧移动</summary>
        private void PlaceWindow(ScreenInfo mon)
        {
            if (_hwnd == IntPtr.Zero) return;

            double sc = mon.Scale <= 0 ? 1.0 : mon.Scale;
            int w = (int)Math.Round(Width * sc);
            int h = (int)Math.Round(Height * sc);
            int x = mon.WorkLeft;
            int y = mon.WorkTop + Math.Max(0, (mon.WorkHeight - h) / 2);

            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE);
            CurrentRectPx = new Rect(x, y, w, h);
        }

        private void ApplyGeometry(ScreenInfo mon)
        {
            Width = _s.PanelWidth;
            double available = mon.WorkHeight / (mon.Scale <= 0 ? 1.0 : mon.Scale) - 24;
            Height = Math.Max(460, Math.Min(880, available));
        }

        // ───────────────────────── 设置变化 ─────────────────────────

        private void OnSettingChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_suppress) return;
            bool rebuild = e.PropertyName == nameof(AppSettings.ShortcutIconSize)
                           || e.PropertyName == nameof(AppSettings.ShortcutNameFontSize)
                           || e.PropertyName == nameof(AppSettings.PanelWidth)
                           || e.PropertyName == nameof(AppSettings.FontFamilyName);

            ApplySettings(rebuild);

            if (e.PropertyName == nameof(AppSettings.WeatherCity) || e.PropertyName == nameof(AppSettings.UseCelsius))
                _ = RefreshWeatherAsync(true);

            if (e.PropertyName == nameof(AppSettings.ShowEdgeTab) || e.PropertyName == nameof(AppSettings.OnlyOnDesktop))
                if (App.Current is App edgeApp) edgeApp.SyncEdgeTab();

            if (e.PropertyName == nameof(AppSettings.GlassEffect) || e.PropertyName == nameof(AppSettings.TransparentEffect))
            {
                if (_open && !_s.TransparentEffect) CaptureGlass(_mon);
                else
                {
                    _glassReady = false;
                    ApplyBackground();
                }
            }
        }

        private void ApplySettings(bool rebuild)
        {
            FontFamily ff = ResolveFont(_s.FontFamilyName);
            TextElement.SetFontFamily(ContentRoot, ff);
            TextElement.SetFontFamily(SettingsLayer, ff);

            bool sizeChanged = Math.Abs(Width - _s.PanelWidth) > 0.5;
            if (sizeChanged) Width = _s.PanelWidth;
            ApplyBackground();

            double cf = _s.ClockFontSize;
            ClockText.FontSize = cf;
            SecondsText.FontSize = Math.Max(9, cf * 0.44);
            PeriodText.FontSize = Math.Max(9, cf * 0.3);
            DateText.FontSize = Math.Max(10, cf * 0.37);
            CityText.FontSize = Math.Max(9, cf * 0.32);

            double wf = _s.WeatherFontSize;
            WeatherGlyph.FontSize = wf * 1.9;
            TempText.FontSize = wf * 1.5;
            CondText.FontSize = Math.Max(9, wf * 0.92);
            MetaText.FontSize = Math.Max(8, wf * 0.78);

            WeatherBox.Visibility = _s.ShowWeather ? Visibility.Visible : Visibility.Collapsed;

            ApplyLod();
            UpdateClock();
            if (_weatherInfo != null) ApplyWeather(_weatherInfo);
            if (_settingsBuilt) RefreshLodVisual();
            if (rebuild && FolderLayer.Visibility == Visibility.Visible && !string.IsNullOrEmpty(_folderPath))
                ShowFolder(_folderPath);

            if (rebuild) RebuildTiles();

            RefreshPinVisual();

            if (_open && _hwnd != IntPtr.Zero)
            {
                PlaceWindow(_mon);
                if (sizeChanged) ScheduleGlassRefresh();
            }

            LogUiState();
        }

        // ───────────────────────── 毛玻璃 ─────────────────────────

        /// <summary>抓取面板背后的桌面并做成毛玻璃底图（只在展开时做一次）</summary>
        private void CaptureGlass(ScreenInfo mon)
        {
            if (!_s.GlassEffect || _s.TransparentEffect)
            {
                _glassReady = false;
                ApplyBackground();
                return;
            }

            try
            {
                Rect rect = CurrentRectPx;
                double sc = mon.Scale <= 0 ? 1.0 : mon.Scale;
                int inset = (int)Math.Round(16 * sc);   // 让开左边缘那条提示条
                int width = (int)rect.Width - inset;
                int height = (int)rect.Height;

                BitmapSource shot = GlassCapture.Capture((int)rect.X + inset, (int)rect.Y, width, height);
                BitmapSource glass = shot == null ? null : GlassCapture.Blur(shot, 0.055, 2);

                if (glass == null)
                {
                    _glassReady = false;
                    ApplyBackground();
                    return;
                }

                var brush = new ImageBrush(glass)
                {
                    Stretch = Stretch.Fill,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top
                };
                brush.Freeze();

                Shell.Background = brush;
                _glassReady = true;
                ApplyBackground();
            }
            catch (Exception ex)
            {
                SettingsStore.Log("毛玻璃处理失败: " + ex.Message);
                _glassReady = false;
                ApplyBackground();
            }
        }

        private void ScheduleGlassRefresh()
        {
            if (_glassTimer == null)
            {
                _glassTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
                _glassTimer.Tick += (s, e) =>
                {
                    _glassTimer.Stop();
                    if (_open) CaptureGlass(_mon);
                };
            }

            _glassTimer.Stop();
            _glassTimer.Start();
        }

        /// <summary>毛玻璃底图 + 暗色蒙层；没底图时退回渐变背景</summary>
        private void ApplyBackground()
        {
            if (_s.TransparentEffect)
            {
                // 真透明：不画底、不截屏，桌面和窗口实时透出来，只用蒙层压暗
                Shell.Background = Brushes.Transparent;
                TintLayer.Background = MakeTintBrush(_s.PanelOpacity);
                return;
            }

            if (_s.GlassEffect && _glassReady)
            {
                TintLayer.Background = MakeTintBrush(_s.PanelOpacity);
                return;
            }

            Shell.Background = MakePanelBrush(_s.PanelOpacity);
            TintLayer.Background = Brushes.Transparent;
        }

        private Brush MakeTintBrush(double opacity)
        {
            byte alpha = (byte)Math.Round(Math.Max(0.0, Math.Min(1.0, opacity)) * 255);
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0.45, 1)
            };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, 0x15, 0x19, 0x21), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(alpha * 0.9), 0x0B, 0x0E, 0x14), 1));
            brush.Freeze();
            return brush;
        }

        /// <summary>诊断日志：确认 DPI / 尺寸 / 画刷 alpha 是否符合预期</summary>
        private void LogUiState()
        {
            try
            {
                DpiScale dpi = VisualTreeHelper.GetDpi(this);
                string brush = Shell.Background is ImageBrush
                    ? "glass-image"
                    : (Shell.Background is LinearGradientBrush lg ? lg.GradientStops[0].Color.ToString() : "null");
                SettingsStore.Log(string.Format(
                    "UI w={0:0.#} h={1:0.#} dpi={2:0.##} op={3:0.00} glass={4}/{5} brush={6} viewport={7:0.#} cols={8} tile={9:0.#}",
                    Width, Height, dpi.DpiScaleX, _s.PanelOpacity, _s.GlassEffect, _glassReady, brush,
                    ShortcutPanel.ActualWidth, _columns,
                    _tiles.Count > 0 ? _tiles[0].Width : 0));
            }
            catch
            {
                // 诊断失败忽略
            }
        }

        private void ApplyLod()
        {
            bool standard = _s.Lod >= 2;
            bool detailed = _s.Lod >= 3;

            SecondsText.Visibility = standard && _s.ShowSeconds ? Visibility.Visible : Visibility.Collapsed;
            MetaText.Visibility = standard ? Visibility.Visible : Visibility.Collapsed;
            CityText.Visibility = standard ? Visibility.Visible : Visibility.Collapsed;
            ForecastCard.Visibility = detailed && _s.ShowWeather && _hasForecast
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private static FontFamily ResolveFont(string name)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(name)) return new FontFamily(name);
            }
            catch
            {
                // 落回默认
            }

            return new FontFamily("Microsoft YaHei UI");
        }

        private Brush MakePanelBrush(double opacity)
        {
            byte alpha = (byte)Math.Round(Math.Max(0.0, Math.Min(1.0, opacity)) * 255);
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0.45, 1)
            };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, 0x1B, 0x20, 0x2A), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, 0x0E, 0x11, 0x17), 1));
            brush.Freeze();
            return brush;
        }

        private static Brush Solid(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private Brush BrushOf(string hex)
        {
            if (_brushCache.TryGetValue(hex, out var cached)) return cached;
            Brush brush;
            try
            {
                brush = (Brush)new BrushConverter().ConvertFromString(hex);
                if (brush.CanFreeze) brush.Freeze();
            }
            catch
            {
                brush = MutedBrush;
            }

            _brushCache[hex] = brush;
            return brush;
        }

        private void RefreshPinVisual()
        {
            PinBtn.Foreground = _pinned ? AccentBrush : Solid(Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF));
            PinBtn.ToolTip = _pinned ? "已固定：不会随鼠标离开收起" : "固定（不随鼠标离开收起）";
        }

        // ───────────────────────── 时钟 ─────────────────────────

        private void UpdateClock()
        {
            DateTime now = DateTime.Now;
            ClockText.Text = now.ToString(_s.Use24Hour ? "HH:mm" : "hh:mm", CultureInfo.InvariantCulture);
            SecondsText.Text = _s.ShowSeconds && _s.Lod >= 2
                ? ":" + now.ToString("ss", CultureInfo.InvariantCulture)
                : string.Empty;
            PeriodText.Text = _s.Use24Hour ? string.Empty : (now.Hour < 12 ? "上午" : "下午");
            DateText.Text = now.ToString("M月d日 dddd", Zh);
        }

        // ───────────────────────── 天气 ─────────────────────────

        private async Task RefreshWeatherAsync(bool force)
        {
            if (_weatherBusy || !_s.ShowWeather) return;
            if (!force && _weatherInfo != null && (DateTime.Now - _lastWeatherFetch) < TimeSpan.FromMinutes(10)) return;

            _weatherBusy = true;
            try
            {
                CondText.Text = "更新中…";
                WeatherInfo info = await _weather.FetchAsync(_s).ConfigureAwait(true);
                _lastWeatherFetch = DateTime.Now;
                ApplyWeather(info);
            }
            catch (Exception ex)
            {
                SettingsStore.Log("天气刷新异常: " + ex.Message);
                CondText.Text = "更新失败";
            }
            finally
            {
                _weatherBusy = false;
            }
        }

        private void ApplyWeather(WeatherInfo w)
        {
            if (w == null) return;
            _weatherInfo = w;

            if (w.City == "--" || (w.Daily.Count == 0 && !string.IsNullOrEmpty(w.Error) && w.Temp == 0))
            {
                WeatherGlyph.Text = "◌";
                WeatherGlyph.Foreground = MutedBrush;
                TempText.Text = "--°";
                CondText.Text = "天气不可用";
                MetaText.Text = string.Empty;
                CityText.Text = _s.WeatherCity;
                ForecastCard.Visibility = Visibility.Collapsed;
                WeatherBox.ToolTip = w.Error;
                return;
            }

            var (text, glyph, color) = WeatherCodes.Describe(w.Code, w.IsDay);
            WeatherGlyph.Text = glyph;
            WeatherGlyph.Foreground = BrushOf(color);
            TempText.Text = w.Temp.ToString("0") + "°";
            CondText.Text = text;
            MetaText.Text = string.Format("体感 {0:0}° · 湿度 {1}%\n风 {2:0} km/h", w.Feels, w.Humidity, w.Wind);

            string region = string.IsNullOrWhiteSpace(w.Region) || w.Region == w.City ? w.City : w.City + " · " + w.Region;
            CityText.Text = region;
            WeatherBox.ToolTip = region + "  " + w.UpdatedAt.ToString("HH:mm") + " 更新"
                                 + (string.IsNullOrEmpty(w.Error) ? "" : "（" + w.Error + "）");

            BuildForecast(w);
            ApplyLod();
        }

        private void BuildForecast(WeatherInfo w)
        {
            ForecastStrip.Children.Clear();
            _hasForecast = false;
            if (w?.Daily == null) return;

            foreach (DailyWeather d in w.Daily.Take(5))
            {
                var (_, glyph, color) = WeatherCodes.Describe(d.Code, true);

                var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                column.Children.Add(new TextBlock
                {
                    Text = d.Label,
                    FontSize = Math.Max(8, _s.WeatherFontSize * 0.7),
                    Foreground = MutedBrush,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                column.Children.Add(new TextBlock
                {
                    Text = glyph,
                    FontFamily = SymbolFont,
                    FontSize = Math.Max(12, _s.WeatherFontSize * 1.15),
                    Foreground = BrushOf(color),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 3, 0, 3)
                });
                column.Children.Add(new TextBlock
                {
                    Text = d.Max.ToString("0") + "° / " + d.Min.ToString("0") + "°",
                    FontSize = Math.Max(8, _s.WeatherFontSize * 0.7),
                    Foreground = SoftBrush,
                    HorizontalAlignment = HorizontalAlignment.Center
                });

                ForecastStrip.Children.Add(column);
                _hasForecast = true;
            }
        }

        // ───────────────────────── 快捷方式 ─────────────────────────

        public void RefreshShortcuts()
        {
            try
            {
                _items = _scanner.Scan();
            }
            catch (Exception ex)
            {
                SettingsStore.Log("扫描桌面失败: " + ex.Message);
                _items = new List<ShortcutItem>();
            }

            RebuildTiles();
        }

        private void RebuildTiles()
        {
            ShortcutPanel.Children.Clear();
            _tiles.Clear();

            FontFamily ff = ResolveFont(_s.FontFamilyName);
            double tileWidth = ComputeTileWidth(ShortcutPanel);

            foreach (ShortcutItem item in _items)
            {
                var tile = new ShortcutTile(item, tileWidth, _s.ShortcutIconSize, _s.ShortcutNameFontSize, ff);
                ShortcutItem captured = item;
                tile.Activated += (s, e) => Launch(captured);
                item.Tile = tile;
                _tiles.Add(tile);
                ShortcutPanel.Children.Add(tile);
            }

            ApplyFilter();
            UpdateStatus();
            LoadIconsAsync();

            // 等一次布局拿到 WrapPanel 的真实宽度后再校准列宽
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LayoutTiles();
                LogTileBounds();
            }), DispatcherPriority.Loaded);
        }

        /// <summary>把前几块磁贴的 DIP 坐标写进日志，便于自动化点击与排查</summary>
        private void LogTileBounds()
        {
            if (_tiles.Count == 0) return;

            var sb = new System.Text.StringBuilder();
            foreach (ShortcutTile tile in _tiles.Take(8))
            {
                try
                {
                    Point p = tile.TransformToAncestor(ShortcutPanel).Transform(new Point(0, 0));
                    sb.Append(string.Format("{0}@({1:0},{2:0} {3:0}x{4:0}) ",
                        tile.Item.DisplayName, p.X, p.Y, tile.ActualWidth, tile.ActualHeight));
                }
                catch
                {
                    // 未连接视觉树时忽略
                }
            }

            SettingsStore.Log("TILES " + sb);
        }

        /// <summary>
        /// 按 WrapPanel 的<b>实际</b>可用宽度算磁贴宽度（含滚动条、边距都要扣掉），
        /// 并预留 1px 余量，避免四舍五入后每行少排一个。
        /// </summary>
        private double ComputeTileWidth(FrameworkElement host)
        {
            double avail = host != null ? host.ActualWidth : 0;
            if (avail <= 0) avail = Math.Max(200, Width - 21);

            _columns = avail >= 480 ? 5 : 4;
            double tileWidth = Math.Floor(avail / _columns) - 4;
            return Math.Max(56, tileWidth - 1);
        }

        private int _columns = 4;

        /// <summary>窗口宽度或可视区变化后，仅调整磁贴宽度，不重建控件</summary>
        private void LayoutTiles()
        {
            if (_tiles.Count > 0)
            {
                double width = ComputeTileWidth(ShortcutPanel);
                foreach (ShortcutTile tile in _tiles) tile.Width = width;
            }

            if (_folderTiles.Count > 0)
            {
                double width = ComputeTileWidth(FolderPanel);
                foreach (ShortcutTile tile in _folderTiles) tile.Width = width;
            }
        }

        private void ShortcutScroll_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            LayoutTiles();
        }

        private void FolderScroll_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            LayoutTiles();
        }

        // ───────────────────────── 内置文件夹浏览 ─────────────────────────

        /// <summary>
        /// 系统外壳打不开目录时（部分环境会直接「拒绝访问」），
        /// 就用面板自己把目录内容列出来，保证点文件夹一定有反应。
        /// </summary>
        private void ShowFolder(string path, bool asRoot = false)
        {
            try
            {
                var dir = new DirectoryInfo(path);
                if (!dir.Exists) return;

                _folderPath = dir.FullName;
                if (asRoot || string.IsNullOrEmpty(_folderRoot)) _folderRoot = dir.FullName;

                FolderTitle.Text = string.IsNullOrEmpty(dir.Name) ? dir.FullName : dir.Name;
                FolderPathText.Text = dir.FullName;

                FolderPanel.Children.Clear();
                _folderTiles.Clear();

                var entries = new List<FileSystemInfo>();
                foreach (FileSystemInfo info in dir.EnumerateFileSystemInfos())
                {
                    try
                    {
                        if (info.Attributes.HasFlag(FileAttributes.Hidden)) continue;
                        if (info.Attributes.HasFlag(FileAttributes.System)) continue;
                        entries.Add(info);
                    }
                    catch
                    {
                        // 个别条目读不到就跳过
                    }
                }

                entries.Sort((a, b) =>
                {
                    bool aDir = a is DirectoryInfo;
                    bool bDir = b is DirectoryInfo;
                    if (aDir != bDir) return aDir ? -1 : 1;
                    return string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
                });

                FontFamily font = ResolveFont(_s.FontFamilyName);
                double tileWidth = ComputeTileWidth(FolderPanel);
                int iconSize = (int)Math.Max(32, _s.ShortcutIconSize);

                foreach (FileSystemInfo info in entries.Take(400))
                {
                    var item = new ShortcutItem
                    {
                        DisplayName = info.Name,
                        Path = info.FullName,
                        IsFolder = info is DirectoryInfo
                    };

                    var tile = new ShortcutTile(item, tileWidth, _s.ShortcutIconSize, _s.ShortcutNameFontSize, font);
                    ShortcutItem captured = item;
                    ShortcutTile capturedTile = tile;

                    tile.Activated += (s, e) =>
                    {
                        if (captured.IsFolder) ShowFolder(captured.Path);
                        else if (OpenPath(captured.Path) && !_pinned) CloseDock();
                    };

                    item.Tile = tile;
                    _folderTiles.Add(tile);
                    FolderPanel.Children.Add(tile);

                    IconLoader.GetAsync(item.Path, iconSize, bitmap =>
                    {
                        item.Icon = bitmap;
                        capturedTile.SetIcon(bitmap);
                    });
                }

                FolderEmpty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                FolderStatus.Text = entries.Count + " 项";
                SetBrowseMode(true);
                FolderScroll.ScrollToTop();

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    double width = ComputeTileWidth(FolderPanel);
                    foreach (ShortcutTile tile in _folderTiles) tile.Width = width;
                }), DispatcherPriority.Loaded);
            }
            catch (Exception ex)
            {
                SettingsStore.Log("打开文件夹视图失败: " + ex.Message);
            }
        }

        private void HideFolder()
        {
            FolderPanel.Children.Clear();
            _folderTiles.Clear();
            _folderPath = null;
            _folderRoot = null;
            SetBrowseMode(false);
        }

        /// <summary>文件夹浏览时把主列表整块收起来，避免两层内容叠在一起</summary>
        private void SetBrowseMode(bool browsing)
        {
            FolderLayer.Visibility = browsing ? Visibility.Visible : Visibility.Collapsed;
            SearchRow.Visibility = browsing ? Visibility.Collapsed : Visibility.Visible;
            ShortcutArea.Visibility = browsing ? Visibility.Collapsed : Visibility.Visible;
            MainFooter.Visibility = browsing ? Visibility.Collapsed : Visibility.Visible;
        }

        private void FolderBack_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_folderPath))
            {
                HideFolder();
                return;
            }

            if (!string.Equals(_folderPath, _folderRoot, StringComparison.OrdinalIgnoreCase))
            {
                DirectoryInfo parent = Directory.GetParent(_folderPath);
                if (parent != null)
                {
                    ShowFolder(parent.FullName);
                    return;
                }
            }

            HideFolder();
        }

        private void FolderExplorer_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_folderPath)) return;

            try
            {
                Process.Start(new ProcessStartInfo(_folderPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SettingsStore.Log("资源管理器打开目录失败: " + ex.Message);
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _folderPath + "\"")
                    {
                        UseShellExecute = false
                    });
                }
                catch
                {
                    // 两种都失败就只能留在内置视图里
                }
            }
        }

        private void LoadIconsAsync()
        {
            int size = (int)Math.Max(32, _s.ShortcutIconSize);

            foreach (ShortcutItem item in _items.Where(i => i.Icon == null).ToList())
            {
                ShortcutItem captured = item;
                IconLoader.GetAsync(captured.Path, size, bitmap =>
                {
                    captured.Icon = bitmap;
                    if (captured.Tile is ShortcutTile tile) tile.SetIcon(bitmap);
                });
            }
        }

        private void ApplyFilter()
        {
            string query = (SearchBox.Text ?? string.Empty).Trim();
            bool anyVisible = false;

            foreach (ShortcutTile tile in _tiles)
            {
                bool show = query.Length == 0
                            || tile.Item.DisplayName.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0
                            || tile.Item.Path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                tile.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                if (show) anyVisible = true;
            }

            SearchHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (_tiles.Count == 0)
            {
                EmptyHint.Text = "桌面还没有可收纳的项目\r\n把快捷方式放到桌面，这里会自动出现";
                EmptyHint.Visibility = Visibility.Visible;
            }
            else if (!anyVisible)
            {
                EmptyHint.Text = "没有匹配「" + query + "」的项目";
                EmptyHint.Visibility = Visibility.Visible;
            }
            else
            {
                EmptyHint.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateStatus()
        {
            StatusText.Text = _items.Count == 0
                ? "未发现桌面项目"
                : string.Format("共 {0} 项 · {1} 个文件夹", _items.Count, _items.Count(i => i.IsFolder));
        }

        private void Launch(ShortcutItem item)
        {
            SettingsStore.Log("LAUNCH " + item.Path);

            if (item.IsFolder)
            {
                TryOpenFolderExternal(item.Path);
                return;   // 文件夹先不收起，等确认资源管理器是否真的打开了
            }

            if (!OpenPath(item.Path))
            {
                StatusText.Text = "无法打开：" + item.DisplayName;
                return;
            }

            if (!_pinned) CloseDock();
        }

        /// <summary>
        /// 先试系统外壳；起进程没抛异常不代表真的出窗口，
        /// 800ms 后确认一次，没窗口就直接切到内置文件夹视图。
        /// 低完整性下外壳一定失败、explorer.exe 还会崩出错误框，所以直接走内置视图。
        /// </summary>
        private void TryOpenFolderExternal(string path)
        {
            string integrity = App.Integrity;

            if (integrity == "Low")
            {
                SettingsStore.Log("低权限(Low IL)：跳过系统外壳，直接用内置文件夹视图");
                ShowFolder(path, true);
                return;
            }

            bool started = false;

            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                started = true;
            }
            catch (Exception ex)
            {
                SettingsStore.Log("shell 打开目录失败: " + ex.Message);
            }

            // explorer.exe 不能在高完整性下运行（会 0xc0000142 崩溃），所以只在普通权限时兜底
            if (!started && integrity != "High")
            {
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"")
                    {
                        UseShellExecute = false,
                        WorkingDirectory = path
                    });
                    started = true;
                }
                catch (Exception ex)
                {
                    SettingsStore.Log("explorer 打开目录失败: " + ex.Message);
                }
            }

            if (!started)
            {
                ShowFolder(path, true);
                return;
            }

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();

                if (ExplorerWindowExists(path))
                {
                    if (!_pinned) CloseDock();
                    return;
                }

                SettingsStore.Log("资源管理器没出窗口，切换为内置文件夹视图: " + path);
                ShowFolder(path, true);
            };
            timer.Start();
        }

        /// <summary>枚举顶层窗口，看看有没有为该文件夹打开的资源管理器窗口</summary>
        private static bool ExplorerWindowExists(string folderPath)
        {
            string name;
            try
            {
                name = System.IO.Path.GetFileName(folderPath.TrimEnd('\\'));
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrEmpty(name)) return false;

            bool found = false;
            try
            {
                Native.EnumWindows((h, l) =>
                {
                    if (!Native.IsWindowVisible(h)) return true;

                    var classBuffer = new System.Text.StringBuilder(64);
                    if (Native.GetClassName(h, classBuffer, classBuffer.Capacity) <= 0) return true;

                    string className = classBuffer.ToString();
                    if (className != "CabinetWClass" && className != "ExploreWClass") return true;

                    var titleBuffer = new System.Text.StringBuilder(512);
                    Native.GetWindowText(h, titleBuffer, titleBuffer.Capacity);
                    if (titleBuffer.ToString().IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) return true;

                    found = true;
                    return false;
                }, IntPtr.Zero);
            }
            catch
            {
                // 枚举失败就当没找到
            }

            return found;
        }

        /// <summary>打开任意文件（快捷方式、exe、文档都走这条）</summary>
        private static bool OpenPath(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                SettingsStore.Log("启动失败 " + path + ": " + ex.Message);
                return false;
            }
        }

        // ───────────────────────── 事件 ─────────────────────────

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            if (SettingsLayer.Visibility == Visibility.Visible) SettingsLayer.Visibility = Visibility.Collapsed;
            else CloseDock();
            e.Handled = true;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void RefreshBtn_Click(object sender, RoutedEventArgs e)
        {
            RefreshShortcuts();
            StatusText.Text = "已重新扫描 · " + DateTime.Now.ToString("HH:mm:ss");
        }

        private void OpenDesktopBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory))
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                SettingsStore.Log("打开桌面失败: " + ex.Message);
            }
        }

        private void SettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsLayer.Visibility == Visibility.Visible) SettingsLayer.Visibility = Visibility.Collapsed;
            else OpenSettings();
        }

        private void CloseSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            SettingsLayer.Visibility = Visibility.Collapsed;
        }

        private void PinBtn_Click(object sender, RoutedEventArgs e)
        {
            _pinned = !_pinned;
            RefreshPinVisual();
        }

        private void QuitBtn_Click(object sender, RoutedEventArgs e)
        {
            (App.Current as App)?.RequestQuit();
        }

        // ───────────────────────── 权限 / 自启修复 ─────────────────────────

        /// <summary>安装到本机（%LOCALAPPDATA%）并重启——低权限最彻底的修复方式</summary>
        private void InstallToLocal_Click(object sender, RoutedEventArgs e)
        {
            (App.Current as App)?.RepairPermissionAndRestart();
        }

        /// <summary>清掉 Low 完整性标签后原地重启</summary>
        private void FixIntegrityLabel_Click(object sender, RoutedEventArgs e)
        {
            if (InstallLocation.ClearLowLabel(AppContext.BaseDirectory) &&
                InstallLocation.ClearLowLabel(Environment.ProcessPath))
            {
                MessageBox.Show(
                    "已清除本程序目录的 Low 完整性标签。\n重启后权限会恢复正常；如果下次启动又是低权限，"
                    + "说明有其他工具在反复写入这个目录的 ACL，建议点『安装到本机』换个位置。",
                    "桌面收纳盒", MessageBoxButton.OK, MessageBoxImage.Information);
                (App.Current as App)?.RepairPermissionAndRestart(restartOnly: true);
            }
            else
            {
                MessageBox.Show("清除标签失败：没有权限修改该目录。请改用『安装到本机』。",
                    "桌面收纳盒", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>修复自启：清掉旧计划任务 / 重复通道，只保留一条</summary>
        private void RepairAutoStart_Click(object sender, RoutedEventArgs e)
        {
            var app = App.Current as App;
            if (app == null) return;

            if (app.RepairAutoStart(out string message))
            {
                _s.StartWithWindows = true;
                _s.AutoStartPreferred = "run";
                MessageBox.Show(
                    "自启已修复：现在只保留「HKCU Run」一条通道，登录时以普通权限启动。\n"
                    + "旧的管理员计划任务已清理（如果还有残留，重启资源管理器或重新登录后生效）。",
                    "桌面收纳盒", MessageBoxButton.OK, MessageBoxImage.Information);
                RebuildSettingsPanel();
            }
            else
            {
                MessageBox.Show("修复失败：" + message, "桌面收纳盒", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>改用计划任务（最高权限，登录后静默启动，不弹 UAC）</summary>
        private void UseScheduledTask_Click(object sender, RoutedEventArgs e)
        {
            var app = App.Current as App;
            if (app == null) return;

            if (app.UseScheduledTask(out string message))
            {
                _s.StartWithWindows = true;
                _s.AutoStartPreferred = "task";
                MessageBox.Show("已改用计划任务自启：登录后会以最高可用权限静默启动。",
                    "桌面收纳盒", MessageBoxButton.OK, MessageBoxImage.Information);
                RebuildSettingsPanel();
            }
            else
            {
                MessageBox.Show("切换失败：" + message, "桌面收纳盒", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
        {
            (App.Current as App)?.OpenDataFolder();
        }

        private void StartWidgetMove_Click(object sender, RoutedEventArgs e)
        {
            (App.Current as App)?.StartWidgetMove();
        }

        private static string CurrentMusicFolder(AppSettings settings)
        {
            return string.IsNullOrWhiteSpace(settings.MusicFolder)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
                : settings.MusicFolder;
        }

        private FrameworkElement MusicFolderRow()
        {
            var path = new TextBlock
            {
                Text = CurrentMusicFolder(_s),
                FontSize = 10.5,
                Foreground = MutedBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 6)
            };

            var button = new Button
            {
                Content = "选择音乐文件夹…",
                Style = (Style)FindResource("GhostButton"),
                Height = 30,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            button.Click += (s, e) =>
            {
                try
                {
                    using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
                    {
                        dialog.Description = "选择存放音乐的文件夹（mp3 / wav / m4a 等）";
                        dialog.SelectedPath = CurrentMusicFolder(_s);
                        dialog.ShowNewFolderButton = false;

                        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                        {
                            _s.MusicFolder = dialog.SelectedPath;
                            path.Text = dialog.SelectedPath;
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("选择文件夹失败：" + ex.Message, "桌面收纳盒");
                }
            };

            var stack = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };
            stack.Children.Add(new TextBlock
            {
                Text = "音乐文件夹（播放列表）",
                FontSize = 12.5,
                Foreground = SoftBrush,
                Margin = new Thickness(0, 0, 0, 4)
            });
            stack.Children.Add(path);
            stack.Children.Add(button);
            return stack;
        }

        private void ResetWidgetPos_Click(object sender, RoutedEventArgs e)
        {
            var defaults = new AppSettings();
            _s.WidgetMarginRight = defaults.WidgetMarginRight;
            _s.WidgetMarginTop = defaults.WidgetMarginTop;
            RebuildSettingsPanel();
        }

        /// <summary>重建设置面板（改完自启方式后刷新显示）</summary>
        private void RebuildSettingsPanel()
        {
            _settingsBuilt = false;
            EnsureSettingsPanel();
        }

        private void ResetSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            var defaults = new AppSettings();
            _suppress = true;
            try
            {
                _s.FontFamilyName = defaults.FontFamilyName;
                _s.ClockFontSize = defaults.ClockFontSize;
                _s.WeatherFontSize = defaults.WeatherFontSize;
                _s.ShortcutNameFontSize = defaults.ShortcutNameFontSize;
                _s.ShortcutIconSize = defaults.ShortcutIconSize;
                _s.PanelWidth = defaults.PanelWidth;
                _s.PanelOpacity = defaults.PanelOpacity;
                _s.GlassEffect = defaults.GlassEffect;
                _s.TransparentEffect = defaults.TransparentEffect;
                _s.Lod = defaults.Lod;
                _s.ShowSeconds = defaults.ShowSeconds;
                _s.Use24Hour = defaults.Use24Hour;
                _s.ShowWeather = defaults.ShowWeather;
                _s.WeatherCity = defaults.WeatherCity;
                _s.UseCelsius = defaults.UseCelsius;
                _s.EdgeTriggerWidth = defaults.EdgeTriggerWidth;
                _s.AutoHideDelayMs = defaults.AutoHideDelayMs;
                _s.ShowEdgeTab = defaults.ShowEdgeTab;
                _s.WidgetEnabled = defaults.WidgetEnabled;
                _s.WidgetLod = defaults.WidgetLod;
                _s.WidgetFontFamilyName = defaults.WidgetFontFamilyName;
                _s.WidgetFontSize = defaults.WidgetFontSize;
                _s.WidgetMarginRight = defaults.WidgetMarginRight;
                _s.WidgetMarginTop = defaults.WidgetMarginTop;
                _s.InfoHudEnabled = defaults.InfoHudEnabled;
                _s.InfoHudShowPerf = defaults.InfoHudShowPerf;
                _s.InfoHudShowMusic = defaults.InfoHudShowMusic;
                _s.InfoHudFontSize = defaults.InfoHudFontSize;
                _s.InfoHudScale = defaults.InfoHudScale;
                _s.MusicFolder = defaults.MusicFolder;
                _s.HideDesktopIcons = false;
                _s.StartWithWindows = false;
            }
            finally
            {
                _suppress = false;
            }

            DesktopIcons.RestoreIfHidden();
            AutoStart.Disable(out _);
            if (App.Current is App app) app.SyncEdgeTab();

            _settingsBuilt = true;
            BuildSettingsPanel();
            ApplySettings(true);
            _ = RefreshWeatherAsync(true);
        }

        // ───────────────────────── 设置面板（代码构建，便于逐项即时生效）─────────────────────────

        private TextBlock SectionLabel(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Solid(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(0, 16, 0, 8)
            };
        }

        private FrameworkElement SliderRow(string label, double min, double max, double value,
            string suffix, Action<double> onChanged, string format = "0.#")
        {
            var valueText = new TextBlock
            {
                Text = value.ToString(format, CultureInfo.InvariantCulture) + suffix,
                FontSize = 11.5,
                Foreground = MutedBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };

            var header = new Grid();
            header.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 12.5,
                Foreground = SoftBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(valueText);

            var slider = new Slider
            {
                Minimum = min,
                Maximum = max,
                Value = value,
                Focusable = false,
                Margin = new Thickness(0, 2, 0, 0)
            };
            slider.ValueChanged += (s, e) =>
            {
                valueText.Text = e.NewValue.ToString(format, CultureInfo.InvariantCulture) + suffix;
                onChanged(e.NewValue);
            };

            var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            stack.Children.Add(header);
            stack.Children.Add(slider);
            return stack;
        }

        private FrameworkElement CheckRow(string label, bool value, Action<bool> onChanged)
        {
            var box = new CheckBox
            {
                Content = label,
                IsChecked = value,
                FontSize = 12.5,
                Foreground = SoftBrush,
                Margin = new Thickness(0, 5, 0, 5),
                VerticalContentAlignment = VerticalAlignment.Center,
                Focusable = false
            };
            box.Checked += (s, e) => onChanged(true);
            box.Unchecked += (s, e) => onChanged(false);
            return box;
        }

        private FrameworkElement FontPickRow()
        {
            return FontPickRow("字体", _s.FontFamilyName, v => _s.FontFamilyName = v);
        }

        /// <summary>字体下拉：主界面和壁纸组件各用一份</summary>
        private FrameworkElement FontPickRow(string title, string current, Action<string> apply)
        {
            var combo = new ComboBox
            {
                Style = (Style)FindResource("DarkCombo"),
                Height = 32,
                FontSize = 12.5,
                MaxDropDownHeight = 320,
                Margin = new Thickness(0, 0, 0, 12)
            };

            ComboBoxItem selected = null;
            foreach (FontFamily family in Fonts.SystemFontFamilies.OrderBy(f => f.Source, StringComparer.CurrentCulture))
            {
                var item = new ComboBoxItem
                {
                    Content = family.Source,
                    FontFamily = family,
                    FontSize = 13,
                    Padding = new Thickness(6, 3, 6, 3)
                };
                combo.Items.Add(item);
                if (string.Equals(family.Source, current, StringComparison.OrdinalIgnoreCase))
                    selected = item;
            }

            if (selected != null) combo.SelectedItem = selected;

            combo.SelectionChanged += (s, e) =>
            {
                if (_suppress) return;
                if (combo.SelectedItem is ComboBoxItem item && item.Content is string name)
                    apply(name);
            };

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12.5,
                Foreground = SoftBrush,
                Margin = new Thickness(0, 0, 0, 6)
            });
            stack.Children.Add(combo);
            return stack;
        }

        private FrameworkElement LodRow()
        {
            return LodRow("信息层级（LOD）", _s.Lod, _lodButtons, v => _s.Lod = v, RefreshLodVisual);
        }

        private FrameworkElement WidgetLodRow()
        {
            return LodRow("显示层级（时间 / +日期 / +星期）", _s.WidgetLod, _widgetLodButtons,
                v => _s.WidgetLod = v, RefreshWidgetLodVisual);
        }

        private FrameworkElement LodRow(string title, int current, List<Button> buttons,
            Action<int> apply, Action refresh)
        {
            var grid = new UniformGrid { Columns = 3 };
            buttons.Clear();

            string[] labels = { "简洁", "标准", "丰富" };
            for (int i = 1; i <= 3; i++)
            {
                int lod = i;
                var button = new Button
                {
                    Content = labels[i - 1],
                    Style = (Style)FindResource("GhostButton"),
                    Height = 32,
                    Margin = new Thickness(i == 1 ? 0 : 6, 0, 0, 0)
                };
                button.Click += (s, e) =>
                {
                    apply(lod);
                    refresh();
                };
                buttons.Add(button);
                grid.Children.Add(button);
            }

            refresh();

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12.5,
                Foreground = SoftBrush,
                Margin = new Thickness(0, 0, 0, 6)
            });
            stack.Children.Add(grid);
            return stack;
        }

        private void RefreshLodVisual()
        {
            ApplyLodVisual(_lodButtons, _s.Lod);
        }

        private void RefreshWidgetLodVisual()
        {
            ApplyLodVisual(_widgetLodButtons, _s.WidgetLod);
        }

        private static void ApplyLodVisual(List<Button> buttons, int activeLod)
        {
            for (int i = 0; i < buttons.Count; i++)
            {
                bool active = (i + 1) == activeLod;
                buttons[i].Background = active ? AccentBrush : Solid(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
                buttons[i].Foreground = active ? Solid(Color.FromRgb(0x1A, 0x1D, 0x24)) : SoftBrush;
                buttons[i].FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        private FrameworkElement CityRow()
        {
            var textBox = new TextBox
            {
                Text = _s.WeatherCity,
                Height = 32,
                FontSize = 12.5,
                Foreground = SoftBrush,
                CaretBrush = SoftBrush,
                Background = Solid(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                BorderBrush = Solid(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 0, 8, 0),
                VerticalContentAlignment = VerticalAlignment.Center
            };

            var apply = new Button
            {
                Content = "应用",
                Style = (Style)FindResource("GhostButton"),
                Height = 32,
                Margin = new Thickness(8, 0, 0, 0),
                Width = 58
            };

            Action commit = () =>
            {
                string value = (textBox.Text ?? string.Empty).Trim();
                if (value.Length > 0) _s.WeatherCity = value;
                _ = RefreshWeatherAsync(true);
            };

            apply.Click += (s, e) => commit();
            textBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    commit();
                    e.Handled = true;
                }
            };

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(textBox, 0);
            Grid.SetColumn(apply, 1);
            row.Children.Add(textBox);
            row.Children.Add(apply);

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = "城市（支持中文/英文，如 杭州、Tokyo）",
                FontSize = 12.5,
                Foreground = SoftBrush,
                Margin = new Thickness(0, 0, 0, 6)
            });
            stack.Children.Add(row);
            stack.Children.Add(new TextBlock
            {
                Text = "数据来源 Open-Meteo，无需 API Key",
                FontSize = 10.5,
                Foreground = Solid(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(0, 6, 0, 0)
            });
            return stack;
        }

        /// <summary>设置页顶部：权限状态 + 修复按钮</summary>
        private FrameworkElement PermissionSection()
        {
            bool low = App.IsLowIntegrity;
            var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };

            var border = new Border
            {
                Background = new SolidColorBrush(low
                    ? Color.FromArgb(0x33, 0xFF, 0x8A, 0x5C)
                    : Color.FromArgb(0x1F, 0x7A, 0xD0, 0x9A)),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(11, 9, 11, 9),
                Margin = new Thickness(0, 0, 0, 8)
            };

            string title = low ? "⚠ 当前是低权限模式（Low IL）" : "✓ 权限正常 · " + App.Integrity;
            string body = low
                ? "托盘、开机自启、隐藏桌面图标、用资源管理器打开文件夹都会被系统拒绝。\n"
                  + "真正的原因通常不是杀毒软件，而是程序所在目录被打了 Low 完整性标签"
                  + "（从下载目录、沙箱/解压工具目录直接运行最常见）。换到本机目录即可根治。"
                : "程序位置：" + (InstallLocation.IsRunningInstalled ? InstallLocation.Root : AppContext.BaseDirectory);

            border.Child = new TextBlock
            {
                Text = title + "\n" + body,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 17,
                Foreground = new SolidColorBrush(low
                    ? Color.FromArgb(0xF0, 0xFF, 0xD9, 0x9A)
                    : Color.FromArgb(0xD9, 0xC9, 0xF0, 0xDD))
            };

            stack.Children.Add(border);

            if (low || !InstallLocation.IsRunningInstalled)
            {
                stack.Children.Add(SmallButton(
                    low ? "安装到本机并重启（推荐）" : "安装到本机（%LOCALAPPDATA%）",
                    InstallToLocal_Click));
            }

            if (low)
                stack.Children.Add(SmallButton("只清除 Low 标签并重启", FixIntegrityLabel_Click));

            stack.Children.Add(SmallButton("打开数据 / 日志目录", OpenDataFolder_Click));

            return stack;
        }

        private Button SmallButton(string text, RoutedEventHandler handler)
        {
            var button = new Button
            {
                Content = text,
                Style = (Style)FindResource("GhostButton"),
                Height = 30,
                FontSize = 11.5,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 6)
            };

            button.Click += handler;
            return button;
        }

        private FrameworkElement Note(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Solid(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(0, 1, 0, 1)
            };
        }

        private FrameworkElement ButtonRow(string leftText, RoutedEventHandler left,
            string rightText, RoutedEventHandler right)
        {
            var grid = new Grid { Margin = new Thickness(0, 8, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var leftButton = new Button
            {
                Content = leftText,
                Style = (Style)FindResource("GhostButton"),
                Height = 30,
                FontSize = 11,
                Margin = new Thickness(0, 0, 4, 0)
            };
            leftButton.Click += left;

            var rightButton = new Button
            {
                Content = rightText,
                Style = (Style)FindResource("GhostButton"),
                Height = 30,
                FontSize = 11,
                Margin = new Thickness(4, 0, 0, 0)
            };
            rightButton.Click += right;

            Grid.SetColumn(leftButton, 0);
            Grid.SetColumn(rightButton, 1);
            grid.Children.Add(leftButton);
            grid.Children.Add(rightButton);
            return grid;
        }

        private void BuildSettingsPanel()
        {
            bool previous = _suppress;
            _suppress = true;

            try
            {
                var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

                // 权限状态：一眼看清哪些功能被系统限制，以及一键修复
                panel.Children.Add(PermissionSection());

                panel.Children.Add(SectionLabel("外观"));
                panel.Children.Add(FontPickRow());
                panel.Children.Add(SliderRow("时钟字号", 18, 64, _s.ClockFontSize, " pt", v => _s.ClockFontSize = v));
                panel.Children.Add(SliderRow("天气字号", 10, 26, _s.WeatherFontSize, " pt", v => _s.WeatherFontSize = v));
                panel.Children.Add(SliderRow("快捷方式名称字号", 9, 18, _s.ShortcutNameFontSize, " pt", v => _s.ShortcutNameFontSize = v));
                panel.Children.Add(SliderRow("快捷方式图标尺寸", 28, 64, _s.ShortcutIconSize, " px", v => _s.ShortcutIconSize = v));
                panel.Children.Add(LodRow());

                panel.Children.Add(SectionLabel("布局与行为"));
                panel.Children.Add(SliderRow("面板宽度", 340, 560, _s.PanelWidth, " px", v => _s.PanelWidth = Math.Round(v)));
                panel.Children.Add(SliderRow("蒙层浓度", 0.0, 1.0, _s.PanelOpacity, "", v => _s.PanelOpacity = v, "0.00"));
                panel.Children.Add(CheckRow("毛玻璃背景（实时模糊桌面）", _s.GlassEffect, v => _s.GlassEffect = v));
                panel.Children.Add(CheckRow("透明效果（不截屏，桌面实时透出）", _s.TransparentEffect, v => _s.TransparentEffect = v));
                panel.Children.Add(Note("透明效果开启后忽略毛玻璃：面板不再截取桌面，背后真实的窗口、动态壁纸直接透出来，只用「蒙层浓度」压暗。"));
                panel.Children.Add(SliderRow("左边缘触发宽度", 1, 16, _s.EdgeTriggerWidth, " px", v => _s.EdgeTriggerWidth = (int)Math.Round(v)));
                panel.Children.Add(SliderRow("自动收起延迟", 200, 3000, _s.AutoHideDelayMs, " ms", v => _s.AutoHideDelayMs = (int)(Math.Round(v / 50.0) * 50)));
                panel.Children.Add(CheckRow("显示秒", _s.ShowSeconds, v => _s.ShowSeconds = v));
                panel.Children.Add(CheckRow("24 小时制", _s.Use24Hour, v => _s.Use24Hour = v));
                panel.Children.Add(CheckRow("显示天气", _s.ShowWeather, v => _s.ShowWeather = v));
                panel.Children.Add(CheckRow("仅在桌面生效（前台是其它软件时不弹出）", _s.OnlyOnDesktop, v => _s.OnlyOnDesktop = v));
                panel.Children.Add(CheckRow("显示左边缘提示条", _s.ShowEdgeTab, v => _s.ShowEdgeTab = v));

                panel.Children.Add(SectionLabel("桌面收纳"));
                panel.Children.Add(CheckRow("隐藏桌面图标（让桌面只剩这个收纳盒）", _s.HideDesktopIcons, v =>
                {
                    _s.HideDesktopIcons = v;
                    if (!DesktopIcons.SetVisible(!v))
                        SettingsStore.Log("隐藏桌面图标失败：找不到桌面列表视图");
                }));
                panel.Children.Add(SectionLabel("开机自启"));
                panel.Children.Add(CheckRow("开机自动启动", _s.StartWithWindows, v =>
                {
                    _s.StartWithWindows = v;

                    if (v)
                    {
                        AutoStartMode mode = string.Equals(_s.AutoStartPreferred, "task", StringComparison.OrdinalIgnoreCase)
                            ? AutoStartMode.Task
                            : AutoStartMode.RunKey;

                        if (!AutoStart.Enable(mode, out string enableMessage))
                        {
                            MessageBox.Show("开启自启失败：\n" + enableMessage, "桌面收纳盒",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                            _s.StartWithWindows = false;
                            RebuildSettingsPanel();
                        }

                        return;
                    }

                    // 关不掉（计划任务需要管理员授权被取消）就把开关恢复，别显示成"已关闭"
                    if (!AutoStart.Disable(out string disableMessage))
                    {
                        MessageBox.Show(disableMessage + "\n\n可以点下面的『修复自启』用管理员权限清理。",
                            "桌面收纳盒", MessageBoxButton.OK, MessageBoxImage.Warning);
                        _s.StartWithWindows = true;
                        RebuildSettingsPanel();
                    }
                }));

                panel.Children.Add(Note("当前通道：" + AutoStart.Describe()));
                panel.Children.Add(Note("指向：" + AutoStart.TargetExe));

                panel.Children.Add(ButtonRow(
                    "修复自启（清理重复通道）", RepairAutoStart_Click,
                    "改用计划任务（最高权限）", UseScheduledTask_Click));

                panel.Children.Add(SectionLabel("壁纸时间（右上角）"));
                panel.Children.Add(CheckRow("显示壁纸时间", _s.WidgetEnabled, v => _s.WidgetEnabled = v));
                panel.Children.Add(FontPickRow(
                    "时间字体",
                    string.IsNullOrWhiteSpace(_s.WidgetFontFamilyName) ? _s.FontFamilyName : _s.WidgetFontFamilyName,
                    v => _s.WidgetFontFamilyName = v));
                panel.Children.Add(SliderRow("时间字号", 18, 140, _s.WidgetFontSize, " pt", v => _s.WidgetFontSize = v));
                panel.Children.Add(WidgetLodRow());
                panel.Children.Add(SliderRow("距右边", 0, 2400, _s.WidgetMarginRight, " px", v => _s.WidgetMarginRight = Math.Round(v)));
                panel.Children.Add(SliderRow("距顶部", 0, 1400, _s.WidgetMarginTop, " px", v => _s.WidgetMarginTop = Math.Round(v)));
                panel.Children.Add(ButtonRow("拖动定位（鼠标直接拖）", StartWidgetMove_Click, "回到右上角", ResetWidgetPos_Click));
                panel.Children.Add(Note("无底板、直接压在壁纸上，只在桌面显示（被窗口挡住、点击穿透）。字号拉大后日期/星期等比放大。"));

                panel.Children.Add(SectionLabel("壁纸信息（左下角）"));
                panel.Children.Add(CheckRow("显示左下角信息条", _s.InfoHudEnabled, v => _s.InfoHudEnabled = v));
                panel.Children.Add(CheckRow("CPU / RAM / GPU", _s.InfoHudShowPerf, v => _s.InfoHudShowPerf = v));
                panel.Children.Add(CheckRow("正在播放的音乐", _s.InfoHudShowMusic, v => _s.InfoHudShowMusic = v));
                panel.Children.Add(SliderRow("整体大小", 60, 220, _s.InfoHudScale * 100, " %",
                    v => _s.InfoHudScale = Math.Round(v) / 100.0, "0"));
                panel.Children.Add(MusicFolderRow());
                panel.Children.Add(Note("音乐：左下角优先显示本地列表里点开的歌；没有本地曲目时显示系统「正在播放」（浏览器、Spotify、网易云等）。进度条可以直接拖，点 ♪ 右边的列表按钮打开播放列表。"));

                panel.Children.Add(SectionLabel("天气"));

                panel.Children.Add(CityRow());
                panel.Children.Add(new Border { Height = 10 });
                panel.Children.Add(CheckRow("摄氏（取消则显示华氏）", _s.UseCelsius, v =>
                {
                    _s.UseCelsius = v;
                    _ = RefreshWeatherAsync(true);
                }));

                panel.Children.Add(new Border { Height = 14 });

                var quitButton = new Button
                {
                    Content = "退出程序",
                    Style = (Style)FindResource("GhostButton"),
                    Height = 30,
                    Margin = new Thickness(0, 6, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = 96
                };
                quitButton.Click += (s, e) => (App.Current as App)?.RequestQuit();
                panel.Children.Add(quitButton);

                SettingsScroll.Content = panel;
            }
            finally
            {
                _suppress = previous;
            }
        }
    }
}
