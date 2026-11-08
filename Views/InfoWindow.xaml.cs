using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
    /// 壁纸左下角的信息条：CPU / RAM / GPU + 正在播放的音乐。
    ///
    /// 和壁纸时间一样是「桌面级」窗口：插在 Progman 之上、普通窗口之下，
    /// 只在桌面显示，鼠标穿透；字色按壁纸明暗自动切换。
    /// </summary>
    public partial class InfoWindow : Window
    {
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const double MarginLogical = 52;

        private static readonly Color Ink = Color.FromRgb(0x1B, 0x1F, 0x27);

        private readonly AppSettings _s;
        private readonly MediaWatcher _media = new MediaWatcher();
        private readonly LocalPlayer _local = new LocalPlayer();
        private readonly DispatcherTimer _timer;
        private DispatcherTimer _beatTimer;
        private readonly AudioSpectrum _spectrum = new AudioSpectrum();

        private PlaylistWindow _playlist;
        private bool _seeking;

        private IntPtr _hwnd;
        private bool _lightBackground = true;
        private bool _placedLogged;

        public InfoWindow(AppSettings settings)
        {
            InitializeComponent();
            _s = settings;
            _s.PropertyChanged += OnSettingChanged;

            _media.Updated += UpdateMusic;
            _local.Changed += UpdateMusic;

            ProgressHit.MouseLeftButtonDown += ProgressHit_MouseLeftButtonDown;
            ProgressHit.MouseMove += ProgressHit_MouseMove;
            ProgressHit.MouseLeftButtonUp += ProgressHit_MouseLeftButtonUp;

            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += (s, e) =>
            {
                SystemMonitor.Sample();
                UpdatePerf();
                _media.Poll();
            };

            // 歌词/节奏条要更细的刷新
            BuildBeatBars();
            _beatTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(90)
            };
            _beatTimer.Tick += (s, e) => TickBeat();

            ApplySettings();
            ApplyPalette();
            UpdatePerf();

            Loaded += (s, e) =>
            {
                Place();
                SampleBackground();
                ApplyPalette();
            };
        }

        /// <summary>Note: 必须在 Show() 之前调用（采样时窗口还没画上去）</summary>
        public void InitializeWindow()
        {
            _hwnd = new WindowInteropHelper(this).EnsureHandle();

            Native.MakeToolWindow(_hwnd);
            Native.DisableWindowFrame(_hwnd);

            long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();

            // 不再整窗穿透：播放/暂停/上一首/下一首按钮要能点到。
            // WS_EX_NOACTIVATE 保留 —— 点按钮不会把桌面窗口抢到前台。
            ex &= ~WS_EX_TRANSPARENT;
            ex |= WS_EX_NOACTIVATE;
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));

            SystemMonitor.Sample();
            UpdatePerf();

            _media.Start();
            _timer.Start();
            _spectrum.Start();
            _beatTimer.Start();

            Place();
        }

        // ───────────────────────── 位置 ─────────────────────────

        public void Place()
        {
            if (_hwnd == IntPtr.Zero) return;

            ScreenInfo mon = ScreenInfo.Primary;
            double sc = mon.Scale <= 0 ? 1.0 : mon.Scale;

            double dipW = ActualWidth;
            double dipH = ActualHeight;
            if (dipW <= 0 || dipH <= 0) return;

            int w = (int)Math.Ceiling(dipW * sc);
            int h = (int)Math.Ceiling(dipH * sc);
            int margin = (int)Math.Round(MarginLogical * sc);

            int x = mon.WorkLeft + margin;
            int y = mon.WorkTop + mon.WorkHeight - h - margin;

            // 自定义位置偏移（dip → px）
            x += (int)Math.Round(_s.HudOffsetX * sc);
            y += (int)Math.Round(_s.HudOffsetY * sc);

            if (y < mon.WorkTop) y = mon.WorkTop;

            IntPtr progman = Native.FindWindow("Progman", null);

            double leftDip = x / sc;
            double topDip = y / sc;
            if (Math.Abs(Left - leftDip) > 0.5) Left = leftDip;
            if (Math.Abs(Top - topDip) > 0.5) Top = topDip;

            Native.SetWindowPos(_hwnd, progman == IntPtr.Zero ? Native.HWND_BOTTOM : progman,
                x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

            if (!_placedLogged)
            {
                _placedLogged = true;
                SettingsStore.Log(string.Format("壁纸信息定位: {0}x{1} dip → {2},{3} px (scale {4:0.##})",
                    dipW, dipH, x, y, sc));
            }
        }

        public void EnsureOnDesktop()
        {
            if (_hwnd == IntPtr.Zero) return;

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
                case nameof(AppSettings.InfoHudEnabled):
                    Visibility = _s.InfoHudEnabled ? Visibility.Visible : Visibility.Collapsed;
                    if (_s.InfoHudEnabled) Place();
                    break;

                case nameof(AppSettings.InfoHudShowMusic):
                case nameof(AppSettings.InfoHudShowPerf):
                case nameof(AppSettings.InfoHudFontSize):
                case nameof(AppSettings.InfoHudScale):
                case nameof(AppSettings.HudFontFamilyName):
                case nameof(AppSettings.HudPerfFontSize):
                case nameof(AppSettings.HudLyricFontSize):
                case nameof(AppSettings.HudPerfBarWidth):
                case nameof(AppSettings.HudShowCover):
                    ApplySettings();
                    break;

                case nameof(AppSettings.HudCustomColors):
                case nameof(AppSettings.HudTextColor):
                case nameof(AppSettings.HudSubColor):
                case nameof(AppSettings.HudAccentColor):
                    ApplyPalette();
                    break;

                case nameof(AppSettings.HudOffsetX):
                case nameof(AppSettings.HudOffsetY):
                    Place();
                    break;

                case nameof(AppSettings.LyricsFolder):
                    _lyricPath = "";
                    UpdateMusic();
                    break;

                case nameof(AppSettings.MusicFolder):
                    _playlist?.Reload();
                    break;
            }
        }

        private void ApplySettings()
        {
            TextElement.SetFontFamily(Stack, ResolveFont());

            double s = Math.Max(0.6, Math.Min(2.2, _s.InfoHudScale));
            double size = _s.InfoHudFontSize * s;

            // 整体缩放：字号、封面、细线、间距一起变
            foreach (TextBlock label in new[] { CpuLabel, RamLabel, GpuLabel })
            {
                label.FontSize = Math.Max(8, size * 0.86);
                label.Opacity = 0.72;
            }

            foreach (TextBlock value in new[] { CpuValue, RamValue, GpuValue })
                value.FontSize = Math.Max(9, size * 0.98);

            TitleText.FontSize = Math.Max(10, size * 1.12);
            ArtistText.FontSize = Math.Max(8, size * 0.9);
            CoverGlyph.FontSize = Math.Max(14, size * 1.8);
            TimeText.FontSize = Math.Max(8, size * 0.76);
            StateText.FontSize = Math.Max(8, size * 0.76);

            double cover = 54 * s;
            CoverBox.Width = cover;
            CoverBox.Height = cover;
            CoverBox.CornerRadius = new CornerRadius(10 * s);

            double track = 54 * s;
            Thickness gap = new Thickness(8 * s, 0, 8 * s, 0);
            CpuTrack.Width = RamTrack.Width = GpuTrack.Width = track;
            CpuTrack.Margin = RamTrack.Margin = GpuTrack.Margin = gap;

            Thickness blockGap = new Thickness(0, 0, 26 * s, 0);
            CpuBlock.Margin = blockGap;
            RamBlock.Margin = blockGap;

            // 自定义字体 / 字号
            var fam = string.IsNullOrWhiteSpace(_s.HudFontFamilyName)
                ? (TryFindResource("AppFont") as System.Windows.Media.FontFamily)
                : new System.Windows.Media.FontFamily(_s.HudFontFamilyName);
            if (fam == null) fam = new System.Windows.Media.FontFamily(_s.FontFamilyName ?? "Microsoft YaHei UI");
            if (!string.IsNullOrWhiteSpace(_s.HudFontFamilyName))
            {
                foreach (var el in new FrameworkElement[] { TitleText, ArtistText, TimeText, StateText,
                             CpuLabel, RamLabel, GpuLabel, CpuValue, RamValue, GpuValue,
                             LyricPrev, LyricCur, LyricNext })
                    el.SetValue(TextBlock.FontFamilyProperty, fam);
            }
            LyricCur.FontSize = _s.HudLyricFontSize * s;
            // 预留歌词区高度：有歌词/没歌词窗口高度一致，组件不会跳动
            LyricBlock.MinHeight = (_s.HudLyricFontSize * 1.5) * 1.0
                                   + (_s.HudLyricFontSize * 0.72 * 1.5) * 2 + 8 * s;
            LyricPrev.FontSize = Math.Max(8, _s.HudLyricFontSize * 0.72) * s;
            LyricNext.FontSize = LyricPrev.FontSize;
            CpuLabel.FontSize = RamLabel.FontSize = GpuLabel.FontSize = _s.HudPerfFontSize * s;
            CpuValue.FontSize = RamValue.FontSize = GpuValue.FontSize = _s.HudPerfFontSize * s;

            // 性能条长度单独可调
            double barW = Math.Max(30, _s.HudPerfBarWidth) * s;
            CpuTrack.Width = barW; RamTrack.Width = barW; GpuTrack.Width = barW;

            MusicBlock.Margin = new Thickness(0, 18 * s, 0, 0);
            MusicText.Margin = new Thickness(12 * s, 0, 0, 0);
            MusicText.Width = 340 * s;

            ProgressHit.Margin = new Thickness(0, 6 * s, 0, 0);
            ProgressHit.Height = Math.Max(8, 14 * s);
            ProgressTrack.Height = Math.Max(1, 2 * s);
            ProgressTrack.CornerRadius = new CornerRadius(Math.Max(0.5, 1 * s));
            ProgressFill.CornerRadius = ProgressTrack.CornerRadius;

            PerfBlock.Visibility = _s.InfoHudShowPerf ? Visibility.Visible : Visibility.Collapsed;
            MusicBlock.Visibility = _s.InfoHudShowMusic ? Visibility.Visible : Visibility.Collapsed;
            RefreshLyricsVisibility();

            LayoutTracker();
        }

        /// <summary>内容变了要重排（窗口尺寸是 SizeToContent）</summary>
        private void LayoutTracker()
        {
            Place();
            UpdateMusic();
        }

        private FontFamily ResolveFont()
        {
            try
            {
                return new FontFamily(_s.FontFamilyName);
            }
            catch
            {
                return new FontFamily("Microsoft YaHei UI");
            }
        }

        // ───────────────────────── 内容 ─────────────────────────

        private void UpdatePerf()
        {
            SetBar(CpuValue, CpuFill, CpuTrack, SystemMonitor.CpuPercent);
            SetBar(RamValue, RamFill, RamTrack, SystemMonitor.RamPercent);
            SetBar(GpuValue, GpuFill, GpuTrack, SystemMonitor.GpuPercent);
        }

        private static void SetBar(TextBlock value, Border fill, Border track, double percent)
        {
            percent = Math.Max(0, Math.Min(100, percent));
            value.Text = Math.Round(percent) + "%";

            double width = track.ActualWidth > 0 ? track.ActualWidth : track.Width;
            double target = Math.Max(0, width * percent / 100.0);

            // 宽度用动画过渡，数值变化时是滑过去而不是跳过去
            var anim = new System.Windows.Media.Animation.DoubleAnimation(
                target, TimeSpan.FromMilliseconds(600))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                },
                FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd
            };
            fill.BeginAnimation(FrameworkElement.WidthProperty, anim);
        }

        private void UpdateMusic()
        {
            // 本地列表里点开的歌优先显示（能精确 seek）
            if (_local.HasTrack)
            {
                LoadLyrics(_local.CurrentPath);

                // 本地文件没有系统封面，走 Shell 缩略图拿内嵌专辑图（同一张只取一次）
                BitmapSource localCover = null;
                try { localCover = IconLoader.Get(_local.CurrentPath, 256); } catch { }

                ShowTrack(_local.Title, _local.Artist, _local.IsPlaying, _local.Position, _local.Duration, localCover,
                    _local.Failed ? "无法播放（格式不支持）" : (_local.IsPlaying ? "正在播放" : "已暂停"));
                return;
            }

            NowPlaying now = _media.Current;

            if (!now.HasSession || string.IsNullOrWhiteSpace(now.Title))
            {
                // 没在放歌也常驻：显示占位状态，不整块收起来
                LoadLyrics("");
                ShowTrack("未在播放", "打开任意播放器后自动接管", false, TimeSpan.Zero, TimeSpan.Zero, null, "空闲");
                return;
            }

            string artist = string.IsNullOrWhiteSpace(now.Artist) ? now.Album : now.Artist;
            LoadLyricsForTrack(now.Title, artist);
            ShowTrack(now.Title, artist, now.IsPlaying, now.Position, now.Duration, now.Cover,
                now.IsPlaying ? "正在播放" : "已暂停");
        }

        private void ShowTrack(string title, string artist, bool playing, TimeSpan position, TimeSpan duration,
            BitmapSource cover, string state)
        {
            MusicBlock.Visibility = _s.InfoHudShowMusic ? Visibility.Visible : Visibility.Collapsed;
            RefreshLyricsVisibility();

            TitleText.Text = title;

            // 没有歌手时就别留一行空白（否则标题和进度条之间会空一大截）
            ArtistText.Text = artist ?? "";
            ArtistText.Visibility = string.IsNullOrWhiteSpace(artist) ? Visibility.Collapsed : Visibility.Visible;

            CoverImage.Source = cover;
            CoverGlyph.Visibility = cover == null ? Visibility.Visible : Visibility.Collapsed;
            CoverBox.Visibility = _s.HudShowCover ? Visibility.Visible : Visibility.Collapsed;

            double ratio = duration.TotalSeconds > 0
                ? Math.Max(0, Math.Min(1, position.TotalSeconds / duration.TotalSeconds))
                : 0;

            double trackWidth = ProgressTrack.ActualWidth > 0 ? ProgressTrack.ActualWidth : 340;
            double targetWidth = trackWidth * ratio;

            if (_seeking)
            {
                // 拖动时跟手，不要动画拖后腿
                ProgressFill.BeginAnimation(FrameworkElement.WidthProperty, null);
                ProgressFill.Width = targetWidth;
            }
            else
            {
                // 跳转/换歌时用动画滑过去，不是瞬间跳
                ProgressFill.BeginAnimation(FrameworkElement.WidthProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(
                        targetWidth, TimeSpan.FromMilliseconds(260))
                    {
                        EasingFunction = new System.Windows.Media.Animation.CubicEase
                        {
                            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                        }
                    });
            }

            TimeText.Text = Format(position) + " / " + (duration.TotalSeconds > 0 ? Format(duration) : "--:--");
            StateText.Text = state;

            // 图标跟着状态走：播放中显示暂停，暂停时显示播放
            PlayButton.Content = playing ? "\uE769" : "\uE768";

            UpdateLyrics(position);
            _playing = playing;
            _position = position;

            Place();
        }

        // ───────────────────────── 歌词 + 节奏条 ─────────────────────────

        private readonly System.Collections.Generic.List<Border> _beatBars =
            new System.Collections.Generic.List<Border>();
        private readonly System.Collections.Generic.List<(double t, string text)> _lyrics =
            new System.Collections.Generic.List<(double, string)>();
        private string _lyricPath = "";
        private bool _playing;
        private TimeSpan _position;
        private int _lyricIndex = -1;

        private void BuildBeatBars()
        {
            var accent = System.Windows.Media.Color.FromRgb(0x9C, 0xF4, 0xFF);
            for (int i = 0; i < 32; i++)
            {
                var b = new Border
                {
                    Width = 4,
                    Height = 4,
                    Margin = new Thickness(0, 0, 3, 0),
                    CornerRadius = new CornerRadius(2),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Background = new System.Windows.Media.SolidColorBrush(accent),
                    Opacity = 0.55
                };
                BeatBlock.Children.Add(b);
                _beatBars.Add(b);
            }
        }

        /// <summary>本地文件：读同目录同名 .lrc</summary>
        private void LoadLyrics(string audioPath)
        {
            if (string.Equals(_lyricPath, audioPath, StringComparison.OrdinalIgnoreCase)) return;
            _lyricPath = audioPath;
            _lyrics.Clear();
            _lyricIndex = -1;
            LyricBlock.Visibility = Visibility.Collapsed;
            if (string.IsNullOrEmpty(audioPath)) return;

            try
            {
                string lrc = System.IO.Path.ChangeExtension(audioPath, ".lrc");
                if (System.IO.File.Exists(lrc)) ParseLrc(lrc);
            }
            catch { }
        }

        /// <summary>
        /// 外部播放器（QQ音乐等）：按「歌名 - 歌手」去歌词文件夹里找同名 .lrc。
        /// 找不到就隐藏歌词块，不显示假歌词。
        /// </summary>
        private void LoadLyricsForTrack(string title, string artist)
        {
            string key = "T|" + title + "|" + artist;
            if (string.Equals(_lyricPath, key, StringComparison.OrdinalIgnoreCase)) return;
            _lyricPath = key;
            _lyrics.Clear();
            _lyricIndex = -1;
            LyricBlock.Visibility = Visibility.Collapsed;

            if (string.IsNullOrWhiteSpace(title)) return;

            try
            {
                // 没设置就默认用「我的音乐\Lyrics」
                string dir = _s.LyricsFolder;
                if (string.IsNullOrWhiteSpace(dir))
                    dir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Lyrics");
                if (!System.IO.Directory.Exists(dir)) return;

                // 常见命名：歌名.lrc / 歌名 - 歌手.lrc / 歌手 - 歌名.lrc
                string[] names =
                {
                    title + ".lrc",
                    title + " - " + artist + ".lrc",
                    artist + " - " + title + ".lrc",
                };

                foreach (string n in names)
                {
                    string p = System.IO.Path.Combine(dir, n);
                    if (System.IO.File.Exists(p)) { ParseLrc(p); return; }
                }

                // 再按「文件名包含歌名」模糊匹配一次
                foreach (string f in System.IO.Directory.GetFiles(dir, "*.lrc"))
                {
                    string baseName = System.IO.Path.GetFileNameWithoutExtension(f);
                    if (baseName.IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        ParseLrc(f);
                        return;
                    }
                }
            }
            catch { }
        }

        private void ParseLrc(string path)
        {
            try
            {
                foreach (string raw in System.IO.File.ReadAllLines(path, System.Text.Encoding.UTF8))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(raw, @"^\[(\d+):(\d+(?:\.\d+)?)\](.*)$");
                    if (!m.Success) continue;
                    double t = int.Parse(m.Groups[1].Value) * 60 + double.Parse(m.Groups[2].Value,
                        System.Globalization.CultureInfo.InvariantCulture);
                    string text = m.Groups[3].Value.Trim();
                    if (text.Length > 0) _lyrics.Add((t, text));
                }
                _lyrics.Sort((a, b) => a.t.CompareTo(b.t));
                RefreshLyricsVisibility();
            }
            catch { }
        }

        /// <summary>
        /// 歌词块显隐统一在这里：
        /// 有歌词 → Visible；没歌词但正在显示播放器 → Hidden（占位，避免窗口高度变化导致整块上移）；
        /// 播放器本身隐藏 → Collapsed。
        /// </summary>
        private void RefreshLyricsVisibility()
        {
            if (!_s.InfoHudShowMusic)
            {
                LyricBlock.Visibility = Visibility.Collapsed;
                return;
            }

            LyricBlock.Visibility = _lyrics.Count > 0 ? Visibility.Visible : Visibility.Hidden;
        }

        private void UpdateLyrics(TimeSpan position)
        {
            if (_lyrics.Count == 0) return;

            int idx = -1;
            for (int i = 0; i < _lyrics.Count; i++)
            {
                if (_lyrics[i].t <= position.TotalSeconds + 0.15) idx = i; else break;
            }
            if (idx == _lyricIndex) return;
            _lyricIndex = idx;

            LyricPrev.Text = idx > 0 ? _lyrics[idx - 1].text : "";
            LyricCur.Text = idx >= 0 && idx < _lyrics.Count ? _lyrics[idx].text : "";
            LyricNext.Text = idx + 1 < _lyrics.Count ? _lyrics[idx + 1].text : "";

            // 换句时淡入一下
            var anim = new System.Windows.Media.Animation.DoubleAnimation(0.25, 1.0, TimeSpan.FromMilliseconds(260));
            LyricCur.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        /// <summary>节奏条：按播放位置推一个律动（不是 FFT，只求跟得上拍子）</summary>
        private void TickBeat()
        {
            bool show = _playing && _beatBars.Count > 0 && MusicBlock.Visibility == Visibility.Visible;
            BeatBlock.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;

            _spectrum.Update();
            double t = _position.TotalSeconds;
            bool live = _spectrum.HasSignal;

            for (int i = 0; i < _beatBars.Count; i++)
            {
                double h;
                if (live)
                {
                    // 真频谱：32 段直接对应 32 根柱
                    float v = _spectrum.Bands[Math.Min(AudioSpectrum.BandCount - 1, i)];
                    h = 3 + Math.Pow(v, 1.35) * 21;
                }
                else
                {
                    double phase = t * 3.1 + i * 0.42;
                    h = 4 + (Math.Sin(phase) * 0.5 + 0.5) * 18
                             * (0.55 + 0.45 * (Math.Sin(t * 1.3 + i * 0.11) * 0.5 + 0.5));
                }
                _beatBars[i].BeginAnimation(FrameworkElement.HeightProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(h, TimeSpan.FromMilliseconds(live ? 55 : 90)));
                _beatBars[i].Opacity = live ? 0.55 + _spectrum.Bands[i] * 0.45 : 0.55;
            }
        }

        // ───────────────────────── 进度拖动 ─────────────────────────

        private void ProgressHit_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _seeking = true;
            ProgressHit.CaptureMouse();
            SeekFromPointer(e);
            e.Handled = true;
        }

        private void ProgressHit_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_seeking) return;
            SeekFromPointer(e);
        }

        private void ProgressHit_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!_seeking) return;
            _seeking = false;
            ProgressHit.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void SeekFromPointer(System.Windows.Input.MouseEventArgs e)
        {
            double width = ProgressHit.ActualWidth;
            if (width <= 0) return;

            double ratio = Math.Max(0, Math.Min(1, e.GetPosition(ProgressHit).X / width));
            SeekTo(ratio);
        }

        private void SeekTo(double ratio)
        {
            if (_local.HasTrack)
            {
                if (_local.Duration > TimeSpan.Zero)
                    _local.Seek(TimeSpan.FromTicks((long)(_local.Duration.Ticks * ratio)));
                return;
            }

            NowPlaying now = _media.Current;
            if (!now.HasSession || now.Duration <= TimeSpan.Zero) return;

            _ = _media.SeekAsync(TimeSpan.FromTicks((long)(now.Duration.Ticks * ratio)));
        }

        // ───────────────────────── 播放控制 ─────────────────────────

        private async void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if (_local.HasTrack)
            {
                _local.TogglePlayPause();
                return;
            }

            await _media.TogglePlayPauseAsync();
            UpdateMusic();
        }

        private async void NextButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if (_local.HasTrack)
            {
                _local.Next();
                return;
            }

            await _media.NextAsync();
            UpdateMusic();
        }

        private async void PrevButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if (_local.HasTrack)
            {
                _local.Previous();
                return;
            }

            await _media.PreviousAsync();
            UpdateMusic();
        }

        private void ListButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if (_playlist == null) _playlist = new PlaylistWindow(_s, _local);
            _playlist.ShowNear(this);
        }

        protected override void OnClosed(EventArgs e)
        {
            try { _playlist?.Close(); } catch { }
            base.OnClosed(e);
        }

        /// <summary>调试用：--media-test 时依次验证 播放/暂停 → 下一首</summary>
        public async void MediaSelfTest()
        {
            await Task.Delay(2500);   // 等媒体会话管理器就绪

            bool toggled = await _media.TogglePlayPauseAsync();
            SettingsStore.Log("媒体自检：播放/暂停 -> " + toggled);

            await Task.Delay(1500);
            NowPlaying now = _media.Current;
            SettingsStore.Log("媒体自检：状态=" + (now.IsPlaying ? "playing" : "paused")
                              + " 曲目=" + now.Title
                              + " " + Format(now.Position) + "/" + Format(now.Duration));

            bool next = await _media.NextAsync();
            SettingsStore.Log("媒体自检：下一首 -> " + next);

            await Task.Delay(1200);
            _media.Poll();
        }

        private static string Format(TimeSpan time)
        {
            if (time < TimeSpan.Zero) time = TimeSpan.Zero;
            return time.TotalHours >= 1
                ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        }

        // ───────────────────────── 配色 ─────────────────────────

        private void SampleBackground()
        {
            try
            {
                ScreenInfo mon = ScreenInfo.Primary;
                double sc = mon.Scale <= 0 ? 1.0 : mon.Scale;

                double dipW = ActualWidth;
                double dipH = ActualHeight;
                if (double.IsNaN(dipW) || dipW <= 0) dipW = Math.Max(320, _s.InfoHudFontSize * 22);
                if (double.IsNaN(dipH) || dipH <= 0) dipH = Math.Max(90, _s.InfoHudFontSize * 6);

                int w = (int)Math.Ceiling(dipW * sc);
                int h = (int)Math.Ceiling(dipH * sc);
                int margin = (int)Math.Round(MarginLogical * sc);
                int x = mon.WorkLeft + margin;
                int y = mon.WorkTop + mon.WorkHeight - h - margin;

                BitmapSource shot = GlassCapture.Capture(x, Math.Max(0, y), w, h);
                if (shot == null) return;

                int stride = shot.PixelWidth * 4;
                var pixels = new byte[stride * shot.PixelHeight];
                shot.CopyPixels(pixels, stride, 0);

                double sum = 0;
                int count = 0;
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
                SettingsStore.Log("壁纸信息亮度采样失败: " + ex.Message);
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

        private void ApplyPalette()
        {
            bool light = _lightBackground;

            // 自定义配色优先
            if (_s.HudCustomColors)
            {
                Color cMain = ParseHex(_s.HudTextColor, Color.FromRgb(0xF2, 0xFE, 0xFF), 0xE6);
                Color cSub = ParseHex(_s.HudSubColor, Color.FromRgb(0xBF, 0xFC, 0xFF), 0x99);
                Color cAcc = ParseHex(_s.HudAccentColor, Color.FromRgb(0x9C, 0xF4, 0xFF), 0xFF);

                var mB = Solid(cMain); var sB = Solid(cSub); var aB = Solid(cAcc);
                foreach (TextBlock b in new[] { TitleText, CpuValue, RamValue, GpuValue, LyricCur })
                    b.Foreground = mB;
                foreach (TextBlock b in new[] { CpuLabel, RamLabel, GpuLabel, TimeText, StateText, ArtistText, LyricPrev, LyricNext })
                    b.Foreground = sB;
                foreach (Button b in new[] { PrevButton, PlayButton, NextButton })
                    b.Foreground = mB;
                foreach (Border b in new[] { CpuTrack, RamTrack, GpuTrack, ProgressTrack })
                    b.Background = Solid(Color.FromArgb(0x33, cAcc.R, cAcc.G, cAcc.B));
                foreach (Border b in new[] { CpuFill, RamFill, GpuFill, ProgressFill })
                    b.Background = aB;
                foreach (var bar in _beatBars) bar.Background = aB;
                Stack.Effect = null;
                return;
            }

            Color main = light
                ? Color.FromArgb(0xE6, Ink.R, Ink.G, Ink.B)
                : Color.FromArgb(0xF0, 255, 255, 255);

            Color dim = light
                ? Color.FromArgb(0x99, Ink.R, Ink.G, Ink.B)
                : Color.FromArgb(0xA6, 255, 255, 255);

            Color faint = light
                ? Color.FromArgb(0x33, Ink.R, Ink.G, Ink.B)
                : Color.FromArgb(0x3D, 255, 255, 255);

            Brush mainBrush = Solid(main);
            Brush dimBrush = Solid(dim);
            Brush faintBrush = Solid(faint);

            foreach (TextBlock block in new[] { TitleText, CpuValue, RamValue, GpuValue })
                block.Foreground = mainBrush;

            foreach (TextBlock block in new[] { CpuLabel, RamLabel, GpuLabel, TimeText, StateText })
                block.Foreground = dimBrush;

            ArtistText.Foreground = dimBrush;
            CoverGlyph.Foreground = faintBrush;

            foreach (Button button in new[] { PrevButton, PlayButton, NextButton })
                button.Foreground = mainBrush;

            foreach (Border track in new[] { CpuTrack, RamTrack, GpuTrack, ProgressTrack })
                track.Background = faintBrush;

            foreach (Border fill in new[] { CpuFill, RamFill, GpuFill })
                fill.Background = dimBrush;

            ProgressFill.Background = mainBrush;

            Stack.Effect = light
                ? new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 14,
                    ShadowDepth = 0,
                    Color = Colors.White,
                    Opacity = 0.5
                }
                : new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 22,
                    ShadowDepth = 0,
                    Color = Colors.Black,
                    Opacity = 0.75
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
