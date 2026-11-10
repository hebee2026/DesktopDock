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
            BuildBeatBars((int)Math.Round(_s.HudBeatBars));
            BuildExtraPerfRows();
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

            // 全屏铺满：内容可以在整块屏幕内自由挪动，不会被窗口边缘裁掉
            ScreenInfo mon = ScreenInfo.Primary;
            double sc = mon.Scale <= 0 ? 1.0 : mon.Scale;
            double dipW = mon.WorkWidth / sc;
            double dipH = mon.WorkHeight / sc;

            if (Math.Abs(Width - dipW) > 0.5) Width = dipW;
            if (Math.Abs(Height - dipH) > 0.5) Height = dipH;

            double leftDip = mon.WorkLeft / sc;
            double topDip = mon.WorkTop / sc;
            if (Math.Abs(Left - leftDip) > 0.5) Left = leftDip;
            if (Math.Abs(Top - topDip) > 0.5) Top = topDip;

            IntPtr progman = Native.FindWindow("Progman", null);
            Native.SetWindowPos(_hwnd, progman == IntPtr.Zero ? Native.HWND_BOTTOM : progman,
                mon.WorkLeft, mon.WorkTop, mon.WorkWidth, mon.WorkHeight,
                Native.SWP_NOACTIVATE);

            if (!_placedLogged)
            {
                _placedLogged = true;
                SettingsStore.Log(string.Format("壁纸信息全屏覆盖: {0}x{1} dip @ {2},{3} px",
                    dipW, dipH, mon.WorkLeft, mon.WorkTop));
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


                case nameof(AppSettings.HudShowCpu):
                case nameof(AppSettings.HudShowRam):
                case nameof(AppSettings.HudShowGpu):
                case nameof(AppSettings.HudShowDisk):
                case nameof(AppSettings.HudShowGpu0):
                case nameof(AppSettings.HudShowGpu1):
                case nameof(AppSettings.HudShowLyrics):
                case nameof(AppSettings.HudShowBeat):
                case nameof(AppSettings.InfoHudShowMusic):
                case nameof(AppSettings.InfoHudShowPerf):
                case nameof(AppSettings.InfoHudFontSize):
                case nameof(AppSettings.InfoHudScale):
                case nameof(AppSettings.HudFontFamilyName):
                case nameof(AppSettings.HudPerfFontSize):
                case nameof(AppSettings.HudLyricFontSize):
                case nameof(AppSettings.HudLyricWidth):
                case nameof(AppSettings.HudPlayerWidth):
                case nameof(AppSettings.HudTitleFontSize):
                case nameof(AppSettings.HudArtistFontSize):
                case nameof(AppSettings.HudTimeFontSize):
                case nameof(AppSettings.HudBeatBarWidth):
                case nameof(AppSettings.HudTilt):
                case nameof(AppSettings.HudBeatHeight):
                    ApplySettings();
                    break;
                case nameof(AppSettings.HudBeatBars):
                    BuildBeatBars((int)Math.Round(_s.HudBeatBars));
                    ApplySettings();
                    break;
                case nameof(AppSettings.HudBeatOffsetX):
                case nameof(AppSettings.HudBeatOffsetY):
                case nameof(AppSettings.HudTiltPerf):
                case nameof(AppSettings.HudTiltMusic):
                case nameof(AppSettings.HudTiltLyric):
                case nameof(AppSettings.HudTiltBeat):
                case nameof(AppSettings.HudPerfOffsetX):
                case nameof(AppSettings.HudPerfOffsetY):
                case nameof(AppSettings.HudMusicOffsetX):
                case nameof(AppSettings.HudMusicOffsetY):
                case nameof(AppSettings.HudLyricOffsetX):
                case nameof(AppSettings.HudLyricOffsetY):
                case nameof(AppSettings.HudPerfBarWidth):
                    // 尺寸类：必须重新布局，否则只改了文字没改字号
                    ApplySettings();
                    break;

                case nameof(AppSettings.HudShowTranslation):
                    _lyricIndex = -2;
                    UpdateLyrics(_position);
                    break;
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

            // 给偏移留出空间：内容周围加 260dip 的余量，
            // 否则把组件往左上挪一点就会被窗口边缘裁掉
            // 窗口已全屏，内容只留基础边距；位移交给 Transform
            Stack.Margin = new Thickness(52, 0, 0, 52);

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
            // 空 = 微软雅黑。不能回退到「界面字体」——那是手写体，字形大一圈，HUD 会突然变大
            var fam = string.IsNullOrWhiteSpace(_s.HudFontFamilyName)
                ? new System.Windows.Media.FontFamily("Microsoft YaHei UI")
                : new System.Windows.Media.FontFamily(_s.HudFontFamilyName);
            {
                foreach (var el in new FrameworkElement[] { TitleText, ArtistText, TimeText, StateText,
                             CpuLabel, RamLabel, GpuLabel, CpuValue, RamValue, GpuValue,
                             LyricPrev, LyricCur, LyricNext })
                    el.SetValue(TextBlock.FontFamilyProperty, fam);
            }
            LyricCur.FontSize = _s.HudLyricFontSize * s;
            // 预留歌词区高度：有歌词/没歌词窗口高度一致，组件不会跳动
            // 每个组件：位移 + 独立旋转（旋转是刚体变换，组件不会被拉变形）
            Func<double, double, double, TransformGroup> build = (ox, oy, tiltDeg) =>
            {
                var g = new TransformGroup();
                g.Children.Add(new TranslateTransform(ox * s, oy * s));
                g.Children.Add(new RotateTransform(Math.Max(-30, Math.Min(30, tiltDeg))));
                return g;
            };

            PerfBlock.RenderTransform = build(_s.HudPerfOffsetX, _s.HudPerfOffsetY, _s.HudTiltPerf);
            PerfBlock.RenderTransformOrigin = new Point(0, 1);
            CounterRotateText(PerfBlock, _s.HudTilt + _s.HudTiltPerf);

            // 播放器不受整体倾角影响：反向旋转把它抵消掉，只保留位置偏移
            var musicGroup = new TransformGroup();
            musicGroup.Children.Add(new TranslateTransform(_s.HudMusicOffsetX * s, _s.HudMusicOffsetY * s));
            musicGroup.Children.Add(new RotateTransform(-_s.HudTilt));
            MusicBlock.RenderTransform = musicGroup;
            MusicBlock.RenderTransformOrigin = new Point(0, 1);
            CounterRotateText(MusicBlock, 0);   // 块已抵消整体倾角，文字无需再转

            var lyricGroup = build(_s.HudLyricOffsetX, _s.HudLyricOffsetY, _s.HudTiltLyric);
            lyricGroup.Children.Add(LyricShift);   // 保留换句滑动动画
            LyricBlock.RenderTransform = lyricGroup;
            LyricBlock.RenderTransformOrigin = new Point(0, 1);
            CounterRotateText(LyricBlock, _s.HudTilt + _s.HudTiltLyric);

            BeatBlock.RenderTransform = build(_s.HudBeatOffsetX, _s.HudBeatOffsetY, _s.HudTiltBeat);
            BeatBlock.RenderTransformOrigin = new Point(0, 1);

            // 整体位移 + 旋转
            var groupAll = new TransformGroup();
            groupAll.Children.Add(new TranslateTransform(_s.HudOffsetX * s, _s.HudOffsetY * s));
            groupAll.Children.Add(new RotateTransform(Math.Max(-30, Math.Min(30, _s.HudTilt))));
            Stack.RenderTransform = groupAll;
            Stack.RenderTransformOrigin = new Point(0, 1);

            LyricBlock.MinHeight = (_s.HudLyricFontSize * 1.5) * 1.0
                                   + (_s.HudLyricFontSize * 0.72 * 1.5) * 2 + 8 * s;
            LyricPrev.FontSize = Math.Max(8, _s.HudLyricFontSize * 0.72) * s;
            LyricNext.FontSize = LyricPrev.FontSize;
            CpuLabel.FontSize = RamLabel.FontSize = GpuLabel.FontSize = _s.HudPerfFontSize * s;
            CpuValue.FontSize = RamValue.FontSize = GpuValue.FontSize = _s.HudPerfFontSize * s;
            TitleText.FontSize = _s.HudTitleFontSize * s;
            ArtistText.FontSize = _s.HudArtistFontSize * s;
            TimeText.FontSize = _s.HudTimeFontSize * s;
            StateText.FontSize = _s.HudTimeFontSize * s;

            // 性能条长度单独可调
            double barW = Math.Max(30, _s.HudPerfBarWidth) * s;
            CpuTrack.Width = barW; RamTrack.Width = barW; GpuTrack.Width = barW;

            MusicBlock.Margin = new Thickness(0, 18 * s, 0, 0);
            MusicText.Margin = new Thickness(12 * s, 0, 0, 0);
            MusicText.Width = _s.HudPlayerWidth * s;
            ProgressHit.Width = _s.HudPlayerWidth * s;
            LyricBlock.Width = _s.HudLyricWidth > 0 ? _s.HudLyricWidth * s : double.NaN;
            foreach (var _b in _beatBars) { _b.Width = _s.HudBeatBarWidth; }
            BeatBlock.Height = Math.Max(8, _s.HudBeatHeight * s);
            ApplyExtraPerfFont(s);
            ApplyExtraPerfLayout(s);
            // 三行老的性能条：行距/条高/字号与新增行完全一致
            var rowGap = new Thickness(0, 0, 0, 8);
            CpuBlock.Margin = rowGap;
            RamBlock.Margin = rowGap;
            GpuBlock.Margin = rowGap;
            CpuTrack.Height = RamTrack.Height = GpuTrack.Height = Math.Max(2, 3 * s);

            CpuBlock.Visibility = _s.HudShowCpu ? Visibility.Visible : Visibility.Collapsed;
            RamBlock.Visibility = _s.HudShowRam ? Visibility.Visible : Visibility.Collapsed;
            GpuBlock.Visibility = _s.HudShowGpu ? Visibility.Visible : Visibility.Collapsed;

            // 各小组件独立开关
            LyricBlock.Visibility = _s.HudShowLyrics ? Visibility.Visible : Visibility.Collapsed;
            BeatBlock.Visibility = _s.HudShowBeat ? Visibility.Visible : Visibility.Collapsed;
            PerfBlock.Visibility = _s.InfoHudShowPerf ? Visibility.Visible : Visibility.Collapsed;
            MusicBlock.Visibility = _s.InfoHudShowMusic ? Visibility.Visible : Visibility.Collapsed;

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
            UpdateExtraPerf();
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

            // 封面必须和当前曲目对得上，否则宁可不显示（防止上一首的图残留）
            string trackKey = (now.Title ?? "") + "|" + (now.Artist ?? "") + "|" + (now.Album ?? "");
            var coverSafe = string.Equals(now.CoverKey, trackKey, StringComparison.Ordinal) ? now.Cover : null;
            LoadLyricsForTrack(now.Title, artist);
            ShowTrack(now.Title, artist, now.IsPlaying, now.Position, now.Duration, coverSafe,
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

            // 关闭封面时留一个音符占位，不要整块消失
            if (!_s.HudShowCover)
            {
                CoverBox.Visibility = Visibility.Visible;
                CoverImage.Source = null;
                CoverImage.Visibility = Visibility.Collapsed;
                CoverGlyph.Visibility = Visibility.Visible;
            }
            else
            {
                CoverBox.Visibility = Visibility.Visible;
                CoverImage.Visibility = Visibility.Visible;
                CoverImage.Source = cover;
                CoverGlyph.Visibility = cover == null ? Visibility.Visible : Visibility.Collapsed;
            }

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
        private readonly System.Collections.Generic.List<(double t, string text)> _trans =
            new System.Collections.Generic.List<(double, string)>();
        private string _lyricTitle = "";

        private void BuildBeatBars(int count = 32)
        {
            // 重建前必须清空：否则每拖一次滑杆就多堆一整套柱子（"调不回去"就是这个原因）
            BeatBlock.Children.Clear();
            _beatBars.Clear();

            var accent = System.Windows.Media.Color.FromRgb(0x9C, 0xF4, 0xFF);
            for (int i = 0; i < count; i++)
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
            _lyricTitle = title;

            try
            {
                // 索引：歌名（小写）→ lrc 路径，覆盖「歌词文件夹」+ 整个「音乐」文件夹
                string hit = FindLrc(GetLyricIndex(false), title, artist);
                if (hit == null)
                {
                    // 可能是刚下载的新歌，缓存里还没有 → 强制重建后重试一次
                    hit = FindLrc(GetLyricIndex(true), title, artist);
                }
                if (hit != null) ParseLrc(hit);
            }
            catch { }
        }

        /// <summary>在索引里找这首歌的 lrc；找不到返回 null</summary>
        private static string FindLrc(System.Collections.Generic.Dictionary<string, string> index,
                                      string title, string artist)
        {
            if (index == null || index.Count == 0) return null;

            string t = (title ?? "").Trim().ToLowerInvariant();
            string a = (artist ?? "").Trim().ToLowerInvariant();
            if (t.Length == 0) return null;

            string[] keys =
            {
                t,
                t + " - " + a,
                a + " - " + t,
                t + "-" + a,
                a + "-" + t,
            };
            foreach (string k in keys)
                if (index.TryGetValue(k, out string hit)) return hit;

            // 文件名里包含歌名（取最短的，通常最精确）
            string best = null;
            foreach (var kv in index)
            {
                if (kv.Key.IndexOf(t, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (best == null || kv.Key.Length < best.Length) best = kv.Key;
            }
            return best != null && index.TryGetValue(best, out string h2) ? h2 : null;
        }

        private readonly System.Collections.Generic.Dictionary<string, string> _lrcIndex =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lyricIndexAt = DateTime.MinValue;
        private string _lyricIndexKey = "";

        /// <summary>
        /// 扫描歌词：配置的歌词文件夹 + 系统「音乐」文件夹（递归）。
        /// 多数人的 .lrc 是和音频放一起的，所以不能只认一个固定目录。
        /// 索引缓存 10 分钟，避免每次换歌都扫盘。
        /// </summary>
        private System.Collections.Generic.Dictionary<string, string> GetLyricIndex(bool force = false)
        {
            string key = _s.LyricsFolder ?? "";
            if (_lrcIndex.Count > 0
                && key == _lyricIndexKey
                && (DateTime.Now - _lyricIndexAt) < TimeSpan.FromMinutes(10))
                return _lrcIndex;

            _lrcIndex.Clear();
            _lyricIndexKey = key;
            _lyricIndexAt = DateTime.Now;

            var roots = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrWhiteSpace(key) && System.IO.Directory.Exists(key)) roots.Add(key);

            string music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (System.IO.Directory.Exists(music)) roots.Add(music);

            foreach (string root in roots)
            {
                try
                {
                    foreach (string file in System.IO.Directory.EnumerateFiles(
                                 root, "*.lrc", System.IO.SearchOption.AllDirectories))
                    {
                        // 网页下载常把空格编码成 '+'，统一还原成空格，否则拆不出「歌手 - 歌名」
                        string name = System.IO.Path.GetFileNameWithoutExtension(file)
                                          .Replace('+', ' ').Trim().ToLowerInvariant();
                        if (name.Length == 0) continue;
                        if (!_lrcIndex.ContainsKey(name)) _lrcIndex[name] = file;

                        // 同时登记去掉歌手前缀/后缀的版本，方便只按歌名命中
                        int sep = name.IndexOf(" - ", StringComparison.Ordinal);
                        if (sep > 0)
                        {
                            string after = name.Substring(sep + 3).Trim();
                            string before = name.Substring(0, sep).Trim();
                            if (after.Length > 0 && !_lrcIndex.ContainsKey(after)) _lrcIndex[after] = file;
                            if (before.Length > 0 && !_lrcIndex.ContainsKey(before)) _lrcIndex[before] = file;
                        }
                    }
                }
                catch { }
            }

            return _lrcIndex;
        }

        private void ParseLrc(string path)
        {
            try
            {
                var all = new System.Collections.Generic.List<(double t, string text)>();
                foreach (string raw in System.IO.File.ReadAllLines(path, System.Text.Encoding.UTF8))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(raw, @"^\[(\d+):(\d+(?:\.\d+)?)\](.*)$");
                    if (!m.Success) continue;
                    double t = int.Parse(m.Groups[1].Value) * 60 + double.Parse(m.Groups[2].Value,
                        System.Globalization.CultureInfo.InvariantCulture);
                    string text = m.Groups[3].Value.Trim();
                    if (text.Length > 0) all.Add((t, text));
                }
                all.Sort((a, b) => a.t.CompareTo(b.t));

                // 双语歌词：按字符集分成「原文」和「译文」两路
                var cjk = new System.Collections.Generic.List<(double t, string text)>();
                var latin = new System.Collections.Generic.List<(double t, string text)>();
                foreach (var line in all)
                    (HasCjk(line.text) ? cjk : latin).Add(line);

                _trans.Clear();
                if (cjk.Count > 0 && latin.Count > 0)
                {
                    // 歌名是中文 → 原文用中文；否则原文用外文
                    bool titleCjk = HasCjk(_lyricTitle);
                    foreach (var l in (titleCjk ? cjk : latin)) _lyrics.Add(l);
                    foreach (var l in (titleCjk ? latin : cjk)) _trans.Add(l);
                }
                else
                {
                    _lyrics.AddRange(all);
                }
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

        /// <summary>是否含中日韩汉字</summary>
        private static bool HasCjk(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
                if (c >= 0x4E00 && c <= 0x9FFF) return true;
            return false;
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

            // 译文：按时间找最近的一句（±3 秒内）
            string trans = "";
            if (_s.HudShowTranslation && idx >= 0 && _trans.Count > 0)
            {
                double cur = _lyrics[idx].t;
                double bestGap = 3.0;
                foreach (var tr in _trans)
                {
                    double gap = Math.Abs(tr.t - cur);
                    if (gap < bestGap) { bestGap = gap; trans = tr.text; }
                }
            }
            LyricTrans.Text = trans;
            LyricTrans.Visibility = string.IsNullOrEmpty(trans) ? Visibility.Collapsed : Visibility.Visible;

            // 换句：整块从下往上滑入，而不是硬切
            try
            {
                double rowH = Math.Max(18, LyricCur.FontSize * 1.55);
                if (LyricShift != null)
                {
                    LyricShift.BeginAnimation(
                        System.Windows.Media.TranslateTransform.YProperty,
                        new System.Windows.Media.Animation.DoubleAnimation(
                            rowH, 0, TimeSpan.FromMilliseconds(240))
                        {
                            EasingFunction = new System.Windows.Media.Animation.CubicEase
                            {
                                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                            }
                        });
                }
                LyricCur.BeginAnimation(UIElement.OpacityProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(
                        0.35, 1.0, TimeSpan.FromMilliseconds(240)));
            }
            catch { }
        }

        /// <summary>节奏条：按播放位置推一个律动（不是 FFT，只求跟得上拍子）</summary>
        private void TickBeat()
        {
            bool show = _playing && _beatBars.Count > 0 && MusicBlock.Visibility == Visibility.Visible;
            BeatBlock.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;

            _spectrum.Update();

            double t = _position.TotalSeconds;
            int n = _beatBars.Count;

            // 先按真频谱算一遍
            var heights = new double[n];
            float peak = 0f;
            for (int k = 0; k < AudioSpectrum.BandCount; k++)
                if (_spectrum.Bands[k] > peak) peak = _spectrum.Bands[k];

            bool live = _spectrum.HasSignal && peak > 0.05f;
            for (int i = 0; i < n; i++)
            {
                if (live)
                {
                    int band = Math.Min(AudioSpectrum.BandCount - 1, i * AudioSpectrum.BandCount / Math.Max(1, n));
                    float v = _spectrum.Bands[band];
                    heights[i] = 3 + Math.Pow(v, 1.35) * 21;
                }
                else
                {
                    double phase = t * 3.1 + i * 0.42;
                    heights[i] = 4 + (Math.Sin(phase) * 0.5 + 0.5) * 18
                                   * (0.55 + 0.45 * (Math.Sin(t * 1.3 + i * 0.11) * 0.5 + 0.5));
                }
            }

            // 兜底：真频谱如果整排几乎一样高（静音/抓不到 loopback），改用律动，别让柱子"死"在那
            if (live)
            {
                double mx = heights[0], mn = heights[0];
                for (int i = 1; i < n; i++) { if (heights[i] > mx) mx = heights[i]; if (heights[i] < mn) mn = heights[i]; }
                if (mx - mn < 2.5)
                {
                    live = false;
                    for (int i = 0; i < n; i++)
                    {
                        double phase = t * 3.1 + i * 0.42;
                        heights[i] = 4 + (Math.Sin(phase) * 0.5 + 0.5) * 18
                                       * (0.55 + 0.45 * (Math.Sin(t * 1.3 + i * 0.11) * 0.5 + 0.5));
                    }
                }
            }

            for (int i = 0; i < n; i++)
            {
                _beatBars[i].BeginAnimation(FrameworkElement.HeightProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(
                        heights[i], TimeSpan.FromMilliseconds(live ? 55 : 90)));
                _beatBars[i].Opacity = live ? 0.55 + _spectrum.Bands[Math.Min(31, i * 32 / Math.Max(1, n))] * 0.45 : 0.55;
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

        /// <summary>
        /// 块被旋转后，把里面的文字反向旋转同样角度：位置跟着斜，字形保持正立。
        /// </summary>
        private static void CounterRotateText(DependencyObject root, double degrees)
        {
            foreach (TextBlock tb in FindTextBlocks(root))
            {
                if (Math.Abs(degrees) < 0.01) { tb.RenderTransform = null; continue; }
                tb.RenderTransformOrigin = new Point(0.5, 0.5);
                tb.RenderTransform = new RotateTransform(-degrees);
            }
        }

        private static System.Collections.Generic.IEnumerable<TextBlock> FindTextBlocks(DependencyObject root)
        {
            if (root == null) yield break;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is TextBlock tb) yield return tb;
                foreach (var sub in FindTextBlocks(child)) yield return sub;
            }
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
                foreach (TextBlock b in new[] { CpuLabel, RamLabel, GpuLabel, TimeText, StateText, ArtistText, LyricPrev, LyricNext, LyricTrans })
                    b.Foreground = sB;
                foreach (Button b in new[] { PrevButton, PlayButton, NextButton })
                    b.Foreground = mB;
                foreach (Border b in new[] { CpuTrack, RamTrack, GpuTrack, ProgressTrack })
                    b.Background = Solid(Color.FromArgb(0x33, cAcc.R, cAcc.G, cAcc.B));
                foreach (Border b in new[] { CpuFill, RamFill, GpuFill, ProgressFill })
                    b.Background = aB;
                foreach (var bar in _beatBars) bar.Background = aB;
                ApplyExtraPerfPalette(mB, aB, Solid(Color.FromArgb(0x33, cAcc.R, cAcc.G, cAcc.B)));
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
            ApplyExtraPerfPalette(mainBrush, dimBrush, faintBrush);   // 与 CPU/RAM/GPU 同一套色

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
