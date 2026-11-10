using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DesktopDock.Models;

namespace DesktopDock.Views
{
    /// <summary>
    /// 壁纸组件外观设置：字体、颜色、字号、位置、倾角、时钟样式。
    /// 改完即时生效并自动保存。
    /// </summary>
    public class HudSettingsWindow : Window
    {
        private static readonly Brush AccentText = new SolidColorBrush(Color.FromRgb(0xE9, 0xC9, 0x7F));
        private static readonly Brush BodyText = new SolidColorBrush(Color.FromRgb(0xF3, 0xE9, 0xD6));

        private readonly AppSettings _s;

        private readonly ComboBox _font = new ComboBox();
        private readonly CheckBox _custom = new CheckBox { Content = "自定义颜色（关掉 = 跟随壁纸明暗自动配色）" };
        private readonly CheckBox _showCover = new CheckBox { Content = "显示专辑封面" };
        private readonly CheckBox _showTrans = new CheckBox { Content = "显示歌词译文（双语歌词时）" };
        private readonly TextBox _text = new TextBox();
        private readonly TextBox _sub = new TextBox();
        private readonly TextBox _accent = new TextBox();
        private readonly TextBox _lyricsDir = new TextBox();

        private readonly ComboBox _clockStyle = new ComboBox();
        private readonly CheckBox _clockCustom = new CheckBox { Content = "时间组件用自定义颜色" };
        private readonly CheckBox _wPerf = new CheckBox { Content = "性能块" };
        private readonly CheckBox _wMusic = new CheckBox { Content = "播放器" };
        private readonly CheckBox _wLyrics = new CheckBox { Content = "歌词" };
        private readonly CheckBox _wBeat = new CheckBox { Content = "节奏条" };
        private readonly CheckBox _wCpu = new CheckBox { Content = "CPU" };
        private readonly CheckBox _wRam = new CheckBox { Content = "RAM" };
        private readonly CheckBox _wGpu = new CheckBox { Content = "GPU（合计）" };
        private readonly CheckBox _wDisk = new CheckBox { Content = "磁盘（DISK 行）" };
        private readonly CheckBox _wGpu0 = new CheckBox { Content = "GPU0（核显）" };
        private readonly CheckBox _wGpu1 = new CheckBox { Content = "GPU1（独显）" };
        private readonly TextBox _clockMain = new TextBox();
        private readonly TextBox _clockSub = new TextBox();

        private readonly Slider _titleSize = new Slider { Minimum = 8, Maximum = 40, TickFrequency = 1, IsSnapToTickEnabled = true };
        private readonly Slider _artistSize = new Slider { Minimum = 8, Maximum = 40, TickFrequency = 1, IsSnapToTickEnabled = true };
        private readonly Slider _timeSize = new Slider { Minimum = 8, Maximum = 30, TickFrequency = 1, IsSnapToTickEnabled = true };
        private readonly Slider _lyricSize = new Slider { Minimum = 9, Maximum = 40, TickFrequency = 1, IsSnapToTickEnabled = true };
        private readonly Slider _perfSize = new Slider { Minimum = 8, Maximum = 24, TickFrequency = 1, IsSnapToTickEnabled = true };
        private readonly Slider _barWidth = new Slider { Minimum = 30, Maximum = 600, TickFrequency = 2, IsSnapToTickEnabled = true };
        private readonly Slider _lyricWidth = new Slider { Minimum = 0, Maximum = 1200, TickFrequency = 10, IsSnapToTickEnabled = true };
        private readonly Slider _playerWidth = new Slider { Minimum = 160, Maximum = 1200, TickFrequency = 10, IsSnapToTickEnabled = true };
        private readonly Slider _beatBarWidth = new Slider { Minimum = 1, Maximum = 12, TickFrequency = 1, IsSnapToTickEnabled = true };
        private readonly Slider _beatN = new Slider { Minimum = 8, Maximum = 128, TickFrequency = 2, IsSnapToTickEnabled = true };
        private readonly Slider _beatH = new Slider { Minimum = 8, Maximum = 90, TickFrequency = 2, IsSnapToTickEnabled = true };

        private readonly Slider _offX = new Slider { Minimum = -4000, Maximum = 4000, TickFrequency = 10, IsSnapToTickEnabled = true };
        private readonly Slider _offY = new Slider { Minimum = -4000, Maximum = 4000, TickFrequency = 10, IsSnapToTickEnabled = true };
        private readonly Slider _tilt = new Slider { Minimum = -30, Maximum = 30, TickFrequency = 1, IsSnapToTickEnabled = true };

        private readonly Slider _perfX = new Slider { Minimum = -800, Maximum = 800, TickFrequency = 5, IsSnapToTickEnabled = true };
        private readonly Slider _perfY = new Slider { Minimum = -800, Maximum = 800, TickFrequency = 5, IsSnapToTickEnabled = true };
        private readonly Slider _musX = new Slider { Minimum = -800, Maximum = 800, TickFrequency = 5, IsSnapToTickEnabled = true };
        private readonly Slider _musY = new Slider { Minimum = -800, Maximum = 800, TickFrequency = 5, IsSnapToTickEnabled = true };
        private readonly Slider _lyrX = new Slider { Minimum = -800, Maximum = 800, TickFrequency = 5, IsSnapToTickEnabled = true };
        private readonly Slider _lyrY = new Slider { Minimum = -800, Maximum = 800, TickFrequency = 5, IsSnapToTickEnabled = true };
        private readonly Slider _beatX = new Slider { Minimum = -800, Maximum = 800, TickFrequency = 5, IsSnapToTickEnabled = true };
        private readonly Slider _beatY = new Slider { Minimum = -800, Maximum = 800, TickFrequency = 5, IsSnapToTickEnabled = true };

        private readonly Slider _tiltPerf = new Slider { Minimum = -30, Maximum = 30, TickFrequency = 1, IsSnapToTickEnabled = true };
        private readonly Slider _tiltMusic = new Slider { Minimum = -30, Maximum = 30, TickFrequency = 1, IsSnapToTickEnabled = true };
        private readonly Slider _tiltLyric = new Slider { Minimum = -30, Maximum = 30, TickFrequency = 1, IsSnapToTickEnabled = true };
        private readonly Slider _tiltBeat = new Slider { Minimum = -30, Maximum = 30, TickFrequency = 1, IsSnapToTickEnabled = true };

        private readonly TextBlock _hint = new TextBlock { Opacity = 0.55, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        private bool _loading = true;

        public HudSettingsWindow(AppSettings settings)
        {
            _s = settings;

            Title = "壁纸组件外观";
            Width = 470;
            Height = Math.Min(980, SystemParameters.WorkArea.Height - 60);
            SizeToContent = SizeToContent.Manual;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x0E));
            Foreground = BodyText;
            FontFamily = new FontFamily("Microsoft YaHei UI");

            BuildUi();
            LoadFromSettings();

            _loading = false;
            WireEvents();
        }

        // ───────────────────────── UI ─────────────────────────

        private void BuildUi()
        {
            var panel = new StackPanel { Margin = new Thickness(20) };

            panel.Children.Add(Label("字体"));
            _font.Height = 28;
            _font.Foreground = Brushes.Black;
            _font.Background = Brushes.White;
            var itemStyle = new Style(typeof(ComboBoxItem));
            itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
            itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
            itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 3, 8, 3)));
            _font.ItemContainerStyle = itemStyle;
            _font.Items.Add("（默认：微软雅黑）");
            try
            {
                using (var ifc = new System.Drawing.Text.InstalledFontCollection())
                {
                    foreach (var name in ifc.Families.Select(f => f.Name).Distinct().OrderBy(n => n))
                        _font.Items.Add(name);
                }
            }
            catch (Exception ex)
            {
                DesktopDock.Services.SettingsStore.Log("枚举字体失败: " + ex.Message);
            }
            panel.Children.Add(_font);

            panel.Children.Add(Label("颜色", 14));
            _custom.Margin = new Thickness(0, 4, 0, 6);
            _custom.Foreground = BodyText;
            panel.Children.Add(_custom);

            _showCover.Margin = new Thickness(0, 0, 0, 6);
            _showCover.Foreground = BodyText;
            panel.Children.Add(_showCover);

            _showTrans.Margin = new Thickness(0, 0, 0, 8);
            _showTrans.Foreground = BodyText;
            panel.Children.Add(_showTrans);

            panel.Children.Add(Row("主文字", _text, true));
            panel.Children.Add(Row("次文字", _sub, true));
            panel.Children.Add(Row("强调色", _accent, true));
            panel.Children.Add(new TextBlock
            {
                Text = "格式 #RRGGBB，例如 #F2FEFF",
                Opacity = 0.45,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 8)
            });

            panel.Children.Add(Label("歌词文件夹", 14));
            panel.Children.Add(Row("目录", _lyricsDir, false, true));
            panel.Children.Add(new TextBlock
            {
                Text = "外部播放器（QQ音乐等）按「歌名 - 歌手.lrc」在这里找歌词",
                Opacity = 0.45,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 8)
            });

            panel.Children.Add(Label("时钟样式", 14));
            _clockStyle.Height = 28;
            _clockStyle.Foreground = Brushes.Black;
            _clockStyle.Background = Brushes.White;
            _clockStyle.ItemContainerStyle = itemStyle;
            _clockStyle.Items.Add("横排（默认）");
            _clockStyle.Items.Add("竖排（时分上下）");
            _clockStyle.Items.Add("极简（只显示时间）");
            _clockStyle.Items.Add("大字（时间放大）");
            panel.Children.Add(_clockStyle);

            panel.Children.Add(Label("时间组件配色", 14));
            _clockCustom.Margin = new Thickness(0, 0, 0, 8);
            _clockCustom.Foreground = BodyText;
            panel.Children.Add(_clockCustom);
            panel.Children.Add(Row("时间主色", _clockMain, true));
            panel.Children.Add(Row("日期/秒", _clockSub, true));

            panel.Children.Add(Label("组件开关", 14));
            foreach (var cb in new[] { _wPerf, _wMusic, _wLyrics, _wBeat, _wCpu, _wRam, _wGpu, _wDisk, _wGpu0, _wGpu1 })
            {
                cb.Margin = new Thickness(18, 0, 0, 5);
                cb.Foreground = BodyText;
                panel.Children.Add(cb);
            }

            panel.Children.Add(Label("字号", 14));
            panel.Children.Add(SliderRow("曲名字号", _titleSize));
            panel.Children.Add(SliderRow("歌手字号", _artistSize));
            panel.Children.Add(SliderRow("进度/状态字号", _timeSize));
            panel.Children.Add(SliderRow("歌词字号", _lyricSize));
            panel.Children.Add(SliderRow("性能条字号", _perfSize));

            panel.Children.Add(Label("尺寸", 14));
            panel.Children.Add(SliderRow("性能条长度", _barWidth));
            panel.Children.Add(SliderRow("歌词块宽度(0=自适应)", _lyricWidth));
            panel.Children.Add(SliderRow("播放器宽度", _playerWidth));

            panel.Children.Add(Label("节奏条", 14));
            panel.Children.Add(SliderRow("柱子数量", _beatN));
            panel.Children.Add(SliderRow("节奏条高度", _beatH));
            panel.Children.Add(SliderRow("节奏柱粗细", _beatBarWidth));

            panel.Children.Add(Label("整体位置与倾角", 14));
            panel.Children.Add(SliderRow("左右", _offX));
            panel.Children.Add(SliderRow("上下", _offY));
            panel.Children.Add(SliderRow("旋转(±30°)", _tilt));

            panel.Children.Add(Label("各组件位置", 14));
            panel.Children.Add(SliderRow("性能 左右", _perfX));
            panel.Children.Add(SliderRow("性能 上下", _perfY));
            panel.Children.Add(SliderRow("播放器 左右", _musX));
            panel.Children.Add(SliderRow("播放器 上下", _musY));
            panel.Children.Add(SliderRow("歌词 左右", _lyrX));
            panel.Children.Add(SliderRow("歌词 上下", _lyrY));
            panel.Children.Add(SliderRow("节奏条 左右", _beatX));
            panel.Children.Add(SliderRow("节奏条 上下", _beatY));

            panel.Children.Add(Label("各组件旋转", 14));
            panel.Children.Add(SliderRow("性能", _tiltPerf));
            panel.Children.Add(SliderRow("播放器", _tiltMusic));
            panel.Children.Add(SliderRow("歌词", _tiltLyric));
            panel.Children.Add(SliderRow("节奏条", _tiltBeat));

            _hint.Text = "改完即时生效并自动保存。想回到跟随壁纸明暗，把「自定义颜色」取消勾选即可。";
            _hint.Margin = new Thickness(0, 14, 0, 0);
            _hint.Foreground = BodyText;
            panel.Children.Add(_hint);

            var reset = MakeButton("恢复默认外观", Color.FromRgb(0x3A, 0x2A, 0x2A));
            AnimateButton(reset);
            reset.Click += (s, e) => ResetToDefaults();
            panel.Children.Add(reset);

            var close = MakeButton("关闭", Color.FromRgb(0x2A, 0x3A, 0x3E));
            AnimateButton(close);
            close.Click += (s, e) => Close();
            panel.Children.Add(close);

            Content = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(0, 0, 6, 0)
            };
        }

        private static Button MakeButton(string text, Color bg)
        {
            return new Button
            {
                Content = text,
                Height = 32,
                Margin = new Thickness(0, 14, 0, 0),
                Background = new SolidColorBrush(bg),
                Foreground = BodyText,
                BorderThickness = new Thickness(0)
            };
        }

        private static TextBlock Label(string text, double top = 0)
        {
            return new TextBlock
            {
                Text = text,
                Margin = new Thickness(0, top, 0, 6),
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                Foreground = AccentText
            };
        }

        private static FrameworkElement Row(string label, TextBox box, bool pickColor = false, bool pickFolder = false)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            g.ColumnDefinitions.Add(new ColumnDefinition());

            var t = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.8,
                Foreground = BodyText
            };
            Grid.SetColumn(box, 1);
            g.Children.Add(t);
            g.Children.Add(box);

            if (pickFolder)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
                var btn = new Button
                {
                    Content = "…",
                    Width = 26,
                    Height = 26,
                    Margin = new Thickness(8, 0, 0, 0),
                    Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x3A, 0x3E)),
                    Foreground = BodyText,
                    BorderThickness = new Thickness(0),
                    ToolTip = "选择文件夹"
                };
                Grid.SetColumn(btn, 2);
                g.Children.Add(btn);
                btn.Click += (s, e) =>
                {
                    using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
                    {
                        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                            box.Text = dlg.SelectedPath;
                    }
                };
            }

            if (pickColor)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
                var swatch = new Border
                {
                    Width = 26,
                    Height = 26,
                    CornerRadius = new CornerRadius(4),
                    Margin = new Thickness(8, 0, 0, 0),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255)),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = "点这里打开调色盘"
                };
                Grid.SetColumn(swatch, 2);
                g.Children.Add(swatch);

                Action sync = () => swatch.Background = new SolidColorBrush(ParseColor(box.Text, Colors.White));
                box.TextChanged += (s, e) => sync();
                sync();

                swatch.MouseLeftButtonDown += (s, e) =>
                {
                    using (var dlg = new System.Windows.Forms.ColorDialog())
                    {
                        dlg.FullOpen = true;
                        dlg.AnyColor = true;
                        var cur = ParseColor(box.Text, Colors.White);
                        dlg.Color = System.Drawing.Color.FromArgb(cur.R, cur.G, cur.B);
                        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                        {
                            var c = dlg.Color;
                            box.Text = string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
                        }
                    }
                };
            }

            return g;
        }

        private static Color ParseColor(string hex, Color fallback)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(hex))
                {
                    string s = hex.Trim().TrimStart('#');
                    if (s.Length == 6)
                        return Color.FromRgb(
                            Convert.ToByte(s.Substring(0, 2), 16),
                            Convert.ToByte(s.Substring(2, 2), 16),
                            Convert.ToByte(s.Substring(4, 2), 16));
                }
            }
            catch { }
            return fallback;
        }

        private static FrameworkElement SliderRow(string label, Slider slider)
        {
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });

            var t = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.85,
                Foreground = BodyText
            };
            var v = new TextBlock
            {
                Text = "0",
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = BodyText
            };
            slider.ValueChanged += (s, e) => v.Text = Math.Round(slider.Value).ToString();
            v.Text = Math.Round(slider.Value).ToString();

            Grid.SetColumn(slider, 1);
            Grid.SetColumn(v, 2);
            g.Children.Add(t);
            g.Children.Add(slider);
            g.Children.Add(v);
            return g;
        }

        /// <summary>按钮：悬停放大 / 按下回缩，120ms 缓动</summary>
        private static void AnimateButton(Button b)
        {
            var scale = new ScaleTransform(1, 1);
            b.RenderTransform = scale;
            b.RenderTransformOrigin = new Point(0.5, 0.5);

            Action<double, int> to = (target, ms) =>
            {
                var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(ms))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            };

            b.MouseEnter += (s, e) => to(1.035, 120);
            b.MouseLeave += (s, e) => to(1.0, 120);
            b.PreviewMouseLeftButtonDown += (s, e) => to(0.96, 80);
            b.PreviewMouseLeftButtonUp += (s, e) => to(1.035, 120);
        }

        // ───────────────────────── 数据 ─────────────────────────

        private void LoadFromSettings()
        {
            _font.SelectedItem = string.IsNullOrWhiteSpace(_s.HudFontFamilyName)
                ? "（默认：微软雅黑）" : _s.HudFontFamilyName;
            if (_font.SelectedIndex < 0) _font.SelectedIndex = 0;

            _custom.IsChecked = _s.HudCustomColors;
            _showCover.IsChecked = _s.HudShowCover;
            _showTrans.IsChecked = _s.HudShowTranslation;
            _text.Text = _s.HudTextColor;
            _sub.Text = _s.HudSubColor;
            _accent.Text = _s.HudAccentColor;
            _lyricsDir.Text = _s.LyricsFolder;

            _clockStyle.SelectedIndex = _s.WidgetStyle;
            if (_clockStyle.SelectedIndex < 0) _clockStyle.SelectedIndex = 0;
            _clockCustom.IsChecked = _s.WidgetCustomColors;
            _wPerf.IsChecked = _s.InfoHudShowPerf;
            _wMusic.IsChecked = _s.InfoHudShowMusic;
            _wLyrics.IsChecked = _s.HudShowLyrics;
            _wBeat.IsChecked = _s.HudShowBeat;
            _wCpu.IsChecked = _s.HudShowCpu;
            _wRam.IsChecked = _s.HudShowRam;
            _wGpu.IsChecked = _s.HudShowGpu;
            _wDisk.IsChecked = _s.HudShowDisk;
            _wGpu0.IsChecked = _s.HudShowGpu0;
            _wGpu1.IsChecked = _s.HudShowGpu1;
            _clockMain.Text = _s.WidgetTextColor;
            _clockSub.Text = _s.WidgetSubColor;

            _titleSize.Value = _s.HudTitleFontSize;
            _artistSize.Value = _s.HudArtistFontSize;
            _timeSize.Value = _s.HudTimeFontSize;
            _lyricSize.Value = _s.HudLyricFontSize;
            _perfSize.Value = _s.HudPerfFontSize;
            _barWidth.Value = _s.HudPerfBarWidth;
            _lyricWidth.Value = _s.HudLyricWidth;
            _playerWidth.Value = _s.HudPlayerWidth;
            _beatN.Value = _s.HudBeatBars;
            _beatH.Value = _s.HudBeatHeight;
            _beatBarWidth.Value = _s.HudBeatBarWidth;

            _offX.Value = _s.HudOffsetX;
            _offY.Value = _s.HudOffsetY;
            _tilt.Value = _s.HudTilt;
            _perfX.Value = _s.HudPerfOffsetX;
            _perfY.Value = _s.HudPerfOffsetY;
            _musX.Value = _s.HudMusicOffsetX;
            _musY.Value = _s.HudMusicOffsetY;
            _lyrX.Value = _s.HudLyricOffsetX;
            _lyrY.Value = _s.HudLyricOffsetY;
            _beatX.Value = _s.HudBeatOffsetX;
            _beatY.Value = _s.HudBeatOffsetY;
            _tiltPerf.Value = _s.HudTiltPerf;
            _tiltMusic.Value = _s.HudTiltMusic;
            _tiltLyric.Value = _s.HudTiltLyric;
            _tiltBeat.Value = _s.HudTiltBeat;
        }

        private void WireEvents()
        {
            _font.SelectionChanged += (s, e) =>
            {
                if (_loading) return;
                _s.HudFontFamilyName = _font.SelectedIndex <= 0 ? "" : (_font.SelectedItem as string ?? "");
            };

            _custom.Click += (s, e) => { if (!_loading) _s.HudCustomColors = _custom.IsChecked == true; };
            _showCover.Click += (s, e) => { if (!_loading) _s.HudShowCover = _showCover.IsChecked == true; };
            _showTrans.Click += (s, e) => { if (!_loading) _s.HudShowTranslation = _showTrans.IsChecked == true; };

            _text.TextChanged += (s, e) => { if (!_loading) { AutoCustom(); _s.HudTextColor = _text.Text; } };
            _sub.TextChanged += (s, e) => { if (!_loading) { AutoCustom(); _s.HudSubColor = _sub.Text; } };
            _accent.TextChanged += (s, e) => { if (!_loading) { AutoCustom(); _s.HudAccentColor = _accent.Text; } };
            _lyricsDir.TextChanged += (s, e) => { if (!_loading) _s.LyricsFolder = _lyricsDir.Text; };

            _clockStyle.SelectionChanged += (s, e) =>
            {
                if (!_loading && _clockStyle.SelectedIndex >= 0) _s.WidgetStyle = _clockStyle.SelectedIndex;
            };
            _clockCustom.Click += (s, e) => { if (!_loading) _s.WidgetCustomColors = _clockCustom.IsChecked == true; };
            _wPerf.Click += (s, e) => { if (!_loading) _s.InfoHudShowPerf = _wPerf.IsChecked == true; };
            _wMusic.Click += (s, e) => { if (!_loading) _s.InfoHudShowMusic = _wMusic.IsChecked == true; };
            _wLyrics.Click += (s, e) => { if (!_loading) _s.HudShowLyrics = _wLyrics.IsChecked == true; };
            _wBeat.Click += (s, e) => { if (!_loading) _s.HudShowBeat = _wBeat.IsChecked == true; };
            _wDisk.Click += (s, e) => { if (!_loading) _s.HudShowDisk = _wDisk.IsChecked == true; };
            _wCpu.Click += (s, e) => { if (!_loading) _s.HudShowCpu = _wCpu.IsChecked == true; };
            _wRam.Click += (s, e) => { if (!_loading) _s.HudShowRam = _wRam.IsChecked == true; };
            _wGpu.Click += (s, e) => { if (!_loading) _s.HudShowGpu = _wGpu.IsChecked == true; };
            _wGpu0.Click += (s, e) => { if (!_loading) _s.HudShowGpu0 = _wGpu0.IsChecked == true; };
            _wGpu1.Click += (s, e) => { if (!_loading) _s.HudShowGpu1 = _wGpu1.IsChecked == true; };
            _clockMain.TextChanged += (s, e) => { if (!_loading) _s.WidgetTextColor = _clockMain.Text; };
            _clockSub.TextChanged += (s, e) => { if (!_loading) _s.WidgetSubColor = _clockSub.Text; };

            _titleSize.ValueChanged += (s, e) => { if (!_loading) _s.HudTitleFontSize = _titleSize.Value; };
            _artistSize.ValueChanged += (s, e) => { if (!_loading) _s.HudArtistFontSize = _artistSize.Value; };
            _timeSize.ValueChanged += (s, e) => { if (!_loading) _s.HudTimeFontSize = _timeSize.Value; };
            _lyricSize.ValueChanged += (s, e) => { if (!_loading) _s.HudLyricFontSize = _lyricSize.Value; };
            _perfSize.ValueChanged += (s, e) => { if (!_loading) _s.HudPerfFontSize = _perfSize.Value; };
            _barWidth.ValueChanged += (s, e) => { if (!_loading) _s.HudPerfBarWidth = _barWidth.Value; };
            _lyricWidth.ValueChanged += (s, e) => { if (!_loading) _s.HudLyricWidth = _lyricWidth.Value; };
            _playerWidth.ValueChanged += (s, e) => { if (!_loading) _s.HudPlayerWidth = _playerWidth.Value; };
            _beatN.ValueChanged += (s, e) => { if (!_loading) _s.HudBeatBars = _beatN.Value; };
            _beatH.ValueChanged += (s, e) => { if (!_loading) _s.HudBeatHeight = _beatH.Value; };
            _beatBarWidth.ValueChanged += (s, e) => { if (!_loading) _s.HudBeatBarWidth = _beatBarWidth.Value; };

            _offX.ValueChanged += (s, e) => { if (!_loading) _s.HudOffsetX = Math.Round(_offX.Value); };
            _offY.ValueChanged += (s, e) => { if (!_loading) _s.HudOffsetY = Math.Round(_offY.Value); };
            _tilt.ValueChanged += (s, e) => { if (!_loading) _s.HudTilt = _tilt.Value; };
            _perfX.ValueChanged += (s, e) => { if (!_loading) _s.HudPerfOffsetX = _perfX.Value; };
            _perfY.ValueChanged += (s, e) => { if (!_loading) _s.HudPerfOffsetY = _perfY.Value; };
            _musX.ValueChanged += (s, e) => { if (!_loading) _s.HudMusicOffsetX = _musX.Value; };
            _musY.ValueChanged += (s, e) => { if (!_loading) _s.HudMusicOffsetY = _musY.Value; };
            _lyrX.ValueChanged += (s, e) => { if (!_loading) _s.HudLyricOffsetX = _lyrX.Value; };
            _lyrY.ValueChanged += (s, e) => { if (!_loading) _s.HudLyricOffsetY = _lyrY.Value; };
            _beatX.ValueChanged += (s, e) => { if (!_loading) _s.HudBeatOffsetX = _beatX.Value; };
            _beatY.ValueChanged += (s, e) => { if (!_loading) _s.HudBeatOffsetY = _beatY.Value; };
            _tiltPerf.ValueChanged += (s, e) => { if (!_loading) _s.HudTiltPerf = _tiltPerf.Value; };
            _tiltMusic.ValueChanged += (s, e) => { if (!_loading) _s.HudTiltMusic = _tiltMusic.Value; };
            _tiltLyric.ValueChanged += (s, e) => { if (!_loading) _s.HudTiltLyric = _tiltLyric.Value; };
            _tiltBeat.ValueChanged += (s, e) => { if (!_loading) _s.HudTiltBeat = _tiltBeat.Value; };
        }

        private void AutoCustom()
        {
            if (_s.HudCustomColors) return;
            _s.HudCustomColors = true;
            _custom.IsChecked = true;
        }

        private void ResetToDefaults()
        {
            _loading = true;

            _s.HudCustomColors = false;
            _s.HudTextColor = "#F2FEFF";
            _s.HudSubColor = "#BFFCFF";
            _s.HudAccentColor = "#9CF4FF";
            _s.HudFontFamilyName = "";
            _s.HudShowCover = true;
            _s.HudShowTranslation = true;
            _s.HudTitleFontSize = 14;
            _s.HudArtistFontSize = 11;
            _s.HudTimeFontSize = 10;
            _s.HudLyricFontSize = 15;
            _s.HudPerfFontSize = 11;
            _s.HudPerfBarWidth = 96;
            _s.HudLyricWidth = 0;
            _s.HudPlayerWidth = 340;
            _s.HudBeatBars = 32;
            _s.HudBeatHeight = 26;
            _s.HudBeatBarWidth = 4;
            _s.HudOffsetX = 0;
            _s.HudOffsetY = 0;
            _s.HudTilt = 0;
            _s.HudPerfOffsetX = 0; _s.HudPerfOffsetY = 0;
            _s.HudMusicOffsetX = 0; _s.HudMusicOffsetY = 0;
            _s.HudLyricOffsetX = 0; _s.HudLyricOffsetY = 0;
            _s.HudBeatOffsetX = 0; _s.HudBeatOffsetY = 0;
            _s.HudTiltPerf = 0; _s.HudTiltMusic = 0; _s.HudTiltLyric = 0; _s.HudTiltBeat = 0;
            _s.WidgetCustomColors = false;
            _s.WidgetTextColor = "#FFFFFF";
            _s.WidgetSubColor = "#FFFFFF";
            _s.WidgetStyle = 0;

            LoadFromSettings();
            _loading = false;
            _hint.Text = "已恢复默认。";
        }
    }
}
