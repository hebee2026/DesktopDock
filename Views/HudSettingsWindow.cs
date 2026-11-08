using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopDock.Models;

namespace DesktopDock.Views
{
    /// <summary>
    /// 壁纸 HUD（歌词 / 播放器 / 性能条）的外观设置：字体、颜色、字号、位置。
    /// 单独的窗口，不掺进收纳盒那套大设置页，改完立刻写到 settings 并即时生效。
    /// </summary>
    public class HudSettingsWindow : Window
    {
        private readonly AppSettings _s;
        private readonly ComboBox _font = new ComboBox();
        private readonly CheckBox _custom = new CheckBox { Content = "自定义颜色（关掉 = 跟随壁纸明暗自动配色）" };
        private readonly CheckBox _showCover = new CheckBox { Content = "显示专辑封面" };
        private readonly CheckBox _clockCustom = new CheckBox { Content = "时间组件用自定义颜色" };
        private readonly TextBox _clockMain = new TextBox();
        private readonly TextBox _clockSub = new TextBox();
        private readonly TextBox _lyricsDir = new TextBox();
        private readonly TextBox _text = new TextBox();
        private readonly TextBox _sub = new TextBox();
        private readonly TextBox _accent = new TextBox();
        private readonly Slider _lyricSize = new Slider { Minimum = 9, Maximum = 40, TickFrequency = 1, IsSnapToTickEnabled = true };
        private readonly Slider _perfSize = new Slider { Minimum = 8, Maximum = 24, TickFrequency = 1, IsSnapToTickEnabled = true };
        private readonly Slider _offX = new Slider { Minimum = -4000, Maximum = 4000, TickFrequency = 10, IsSnapToTickEnabled = true };
        private readonly Slider _offY = new Slider { Minimum = -4000, Maximum = 4000, TickFrequency = 10, IsSnapToTickEnabled = true };
        private readonly Slider _barWidth = new Slider { Minimum = 30, Maximum = 600, TickFrequency = 2, IsSnapToTickEnabled = true };
        private readonly TextBlock _hint = new TextBlock { Opacity = 0.6, FontSize = 11, TextWrapping = TextWrapping.Wrap };

        private bool _loading = true;

        public HudSettingsWindow(AppSettings settings)
        {
            _s = settings;

            Title = "壁纸组件外观";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x1B, 0x1E));
            Foreground = Brushes.WhiteSmoke;
            FontFamily = new FontFamily("Microsoft YaHei UI");

            BuildUi();
            LoadFromSettings();

            _loading = false;
            WireEvents();
        }

        private void BuildUi()
        {
            var panel = new StackPanel { Margin = new Thickness(20) };

            panel.Children.Add(Label("字体"));
            _font.Height = 28;
            _font.Items.Add("（跟随界面字体）");
            foreach (var f in Fonts.SystemFontFamilies.OrderBy(x => x.Source)) _font.Items.Add(f.Source);
            panel.Children.Add(_font);

            panel.Children.Add(Label("颜色", 14));
            _custom.Margin = new Thickness(0, 4, 0, 8);
            _custom.Foreground = Brushes.WhiteSmoke;
            panel.Children.Add(_custom);

            _showCover.Margin = new Thickness(0, 0, 0, 8);
            _showCover.Foreground = Brushes.WhiteSmoke;
            panel.Children.Add(_showCover);
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
            panel.Children.Add(Row("目录", _lyricsDir, true, true));
            panel.Children.Add(new TextBlock { Text = "外部播放器（QQ音乐等）按「歌名 - 歌手.lrc」在这里找歌词", Opacity = 0.45, FontSize = 11, Margin = new Thickness(0, 0, 0, 8) });

            panel.Children.Add(Label("时间组件配色", 14));
            _clockCustom.Margin = new Thickness(0, 0, 0, 8);
            _clockCustom.Foreground = Brushes.WhiteSmoke;
            panel.Children.Add(_clockCustom);
            panel.Children.Add(Row("时间主色", _clockMain, true));
            panel.Children.Add(Row("日期/秒", _clockSub, true));

            panel.Children.Add(Label("字号", 14));
            panel.Children.Add(SliderRow("歌词字号", _lyricSize));
            panel.Children.Add(SliderRow("性能条字号", _perfSize));
            panel.Children.Add(SliderRow("性能条长度", _barWidth));

            panel.Children.Add(Label("位置偏移（相对左下角）", 14));
            panel.Children.Add(SliderRow("左右", _offX));
            panel.Children.Add(SliderRow("上下", _offY));

            _hint.Text = "改完即时生效并自动保存。想回到跟随壁纸明暗，把「自定义颜色」取消勾选即可。";
            _hint.Margin = new Thickness(0, 12, 0, 0);
            panel.Children.Add(_hint);

            var close = new Button
            {
                Content = "关闭",
                Height = 32,
                Margin = new Thickness(0, 16, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x3A, 0x3E)),
                Foreground = Brushes.WhiteSmoke,
                BorderThickness = new Thickness(0)
            };
            close.Click += (s, e) => Close();
            panel.Children.Add(close);

            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        private static TextBlock Label(string text, double top = 0)
            => new TextBlock { Text = text, Margin = new Thickness(0, top, 0, 4), FontWeight = FontWeights.SemiBold };

        private static FrameworkElement Row(string label, TextBox box, bool pickColor = false, bool pickFolder = false)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 };
            Grid.SetColumn(box, 1);
            g.Children.Add(t);
            g.Children.Add(box);

            if (pickColor)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

                // 颜色方块：既当预览，点一下弹系统调色盘
                var swatch = new Border
                {
                    Width = 26, Height = 26, CornerRadius = new CornerRadius(4),
                    Margin = new Thickness(8, 0, 0, 0),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255)),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = "点这里打开调色盘"
                };
                Grid.SetColumn(swatch, 2);
                g.Children.Add(swatch);

                Action syncSwatch = () =>
                {
                    var c = ParseColor(box.Text, Colors.White);
                    swatch.Background = new SolidColorBrush(c);
                };
                box.TextChanged += (s, e) => syncSwatch();
                syncSwatch();

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

        private FrameworkElement SliderRow(string label, Slider slider)
        {
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 };
            var v = new TextBlock { Text = "0", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            slider.ValueChanged += (s, e) => v.Text = Math.Round(slider.Value).ToString();
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(v, 2);
            g.Children.Add(t);
            g.Children.Add(slider);
            g.Children.Add(v);
            return g;
        }

        private void LoadFromSettings()
        {
            _font.SelectedItem = string.IsNullOrWhiteSpace(_s.HudFontFamilyName)
                ? "（跟随界面字体）" : _s.HudFontFamilyName;
            if (_font.SelectedIndex < 0) _font.SelectedIndex = 0;

            _custom.IsChecked = _s.HudCustomColors;
            _showCover.IsChecked = _s.HudShowCover;
            _clockCustom.IsChecked = _s.WidgetCustomColors;
            _clockMain.Text = _s.WidgetTextColor;
            _clockSub.Text = _s.WidgetSubColor;
            _lyricsDir.Text = _s.LyricsFolder;
            _text.Text = _s.HudTextColor;
            _sub.Text = _s.HudSubColor;
            _accent.Text = _s.HudAccentColor;
            _lyricSize.Value = _s.HudLyricFontSize;
            _perfSize.Value = _s.HudPerfFontSize;
            _barWidth.Value = _s.HudPerfBarWidth;
            _offX.Value = _s.HudOffsetX;
            _offY.Value = _s.HudOffsetY;
        }

        private void WireEvents()
        {
            _font.SelectionChanged += (s, e) =>
            {
                if (_loading) return;
                var sel = _font.SelectedIndex <= 0 ? "" : (_font.SelectedItem as string ?? "");
                _s.HudFontFamilyName = sel;
            };
            _custom.Click += (s, e) => { if (!_loading) _s.HudCustomColors = _custom.IsChecked == true; };
            _showCover.Click += (s, e) => { if (!_loading) _s.HudShowCover = _showCover.IsChecked == true; };
            _clockCustom.Click += (s, e) => { if (!_loading) _s.WidgetCustomColors = _clockCustom.IsChecked == true; };
            _clockMain.TextChanged += (s, e) => { if (!_loading) _s.WidgetTextColor = _clockMain.Text; };
            _clockSub.TextChanged += (s, e) => { if (!_loading) _s.WidgetSubColor = _clockSub.Text; };
            _lyricsDir.TextChanged += (s, e) => { if (!_loading) _s.LyricsFolder = _lyricsDir.Text; };
            _text.TextChanged += (s, e) => { if (!_loading) _s.HudTextColor = _text.Text; };
            _sub.TextChanged += (s, e) => { if (!_loading) _s.HudSubColor = _sub.Text; };
            _accent.TextChanged += (s, e) => { if (!_loading) _s.HudAccentColor = _accent.Text; };
            _lyricSize.ValueChanged += (s, e) => { if (!_loading) _s.HudLyricFontSize = _lyricSize.Value; };
            _perfSize.ValueChanged += (s, e) => { if (!_loading) _s.HudPerfFontSize = _perfSize.Value; };
            _barWidth.ValueChanged += (s, e) => { if (!_loading) _s.HudPerfBarWidth = _barWidth.Value; };
            _offX.ValueChanged += (s, e) => { if (!_loading) _s.HudOffsetX = Math.Round(_offX.Value); };
            _offY.ValueChanged += (s, e) => { if (!_loading) _s.HudOffsetY = Math.Round(_offY.Value); };
        }
    }
}
