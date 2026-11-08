using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopDock.Models;
using DesktopDock.Services;

namespace DesktopDock.Views
{
    /// <summary>
    /// 播放列表：列出音乐文件夹（含子目录）里的曲子，点一行就播。
    /// 用 ListBox + 虚拟化 —— 上千首也不会卡。
    /// 背景是毛玻璃（和抽屉同一套截图模糊），字体跟随设置里的界面字体。
    /// Topmost 的临时面板，点外面自动收起。
    /// </summary>
    public partial class PlaylistWindow : Window
    {
        private readonly AppSettings _s;
        private readonly LocalPlayer _player;

        public PlaylistWindow(AppSettings settings, LocalPlayer player)
        {
            InitializeComponent();
            _s = settings;
            _player = player;

            _player.Changed += SyncSelection;
            _s.PropertyChanged += OnSettingChanged;

            Deactivated += (s, e) => Hide();

            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    Hide();
                    e.Handled = true;
                }
            };

            ApplyFont();
            Rebuild();
        }

        private void OnSettingChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppSettings.FontFamilyName)) ApplyFont();
        }

        private void ApplyFont()
        {
            try
            {
                string name = string.IsNullOrWhiteSpace(_s.FontFamilyName) ? "Microsoft YaHei UI" : _s.FontFamilyName;
                TextElement.SetFontFamily(Root, new FontFamily(name));
            }
            catch
            {
                // 字体名字无效就保持默认
            }
        }

        /// <summary>贴着左下角信息条上方弹出（弹出前先抓一张背后的桌面做毛玻璃）</summary>
        public void ShowNear(Window hud)
        {
            Hide();
            if (TrackList.ItemsSource == null) Rebuild();

            ScreenInfo mon = ScreenInfo.Primary;
            double sc = mon.Scale <= 0 ? 1.0 : mon.Scale;

            double x = hud.Left;
            double y = hud.Top - Height - 10;
            if (y < mon.WorkTop / sc + 8) y = mon.WorkTop / sc + 8;

            Left = x;
            Top = y;

            CaptureGlass();

            Show();
            Activate();
        }

        /// <summary>抓一张窗口背后的桌面，缩+模糊后当底图（窗口此时必须是隐藏的）</summary>
        private void CaptureGlass()
        {
            try
            {
                ScreenInfo mon = ScreenInfo.Primary;
                double sc = mon.Scale <= 0 ? 1.0 : mon.Scale;

                int w = (int)Math.Ceiling(Width * sc);
                int h = (int)Math.Ceiling(Height * sc);
                int x = (int)Math.Round(Left * sc);
                int y = (int)Math.Round(Top * sc);

                BitmapSource shot = GlassCapture.Capture(x, y, w, h);
                BitmapSource blurred = shot == null ? null : GlassCapture.Blur(shot, 0.05, 2);

                if (blurred == null)
                {
                    Glass.Background = null;
                    return;
                }

                var brush = new ImageBrush(blurred)
                {
                    Stretch = Stretch.Fill,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top
                };
                brush.Freeze();
                Glass.Background = brush;

                // 蒙层浓度跟抽屉设置走，但有下限：列表里字小，太透会看不清
                double opacity = Math.Max(0.68, Math.Min(1.0, _s.PanelOpacity));
                var tint = new SolidColorBrush(Color.FromArgb((byte)Math.Round(opacity * 255), 0x0C, 0x0E, 0x13));
                tint.Freeze();
                Tint.Background = tint;
            }
            catch (Exception ex)
            {
                SettingsStore.Log("播放列表毛玻璃失败: " + ex.Message);
            }
        }

        public void Reload()
        {
            if (IsVisible) Rebuild();
        }

        private void Rebuild()
        {
            string root = string.IsNullOrWhiteSpace(_s.MusicFolder)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
                : _s.MusicFolder;

            FolderText.Text = root;

            int count = _player.LoadFolder(root);

            var rows = new List<string>(count);
            for (int i = 0; i < _player.Tracks.Count; i++)
                rows.Add((i + 1).ToString("000") + "  " + Path.GetFileNameWithoutExtension(_player.Tracks[i]));

            TrackList.ItemsSource = rows;
            TrackList.SelectedIndex = _player.HasTrack ? _player.Index : -1;
        }

        private void SyncSelection()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(SyncSelection));
                return;
            }

            if (!IsVisible) return;

            TrackList.SelectedIndex = _player.HasTrack ? _player.Index : -1;
        }

        private void TrackList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // 不依赖 ListBox 的选中事件（实测鼠标点在 TextBlock 上时它不触发），
            // 直接按坐标找行，稳健得多。
            DependencyObject element = TrackList.InputHitTest(e.GetPosition(TrackList)) as DependencyObject;

            while (element != null && !(element is ListBoxItem))
                element = VisualTreeHelper.GetParent(element);

            if (!(element is ListBoxItem item)) return;

            int index = TrackList.ItemContainerGenerator.IndexFromContainer(item);
            if (index < 0 || index >= _player.Tracks.Count) return;

            SettingsStore.Log("播放列表：点击第 " + index + " 行");
            _player.Play(index);
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Hide();

        private void RefreshBtn_Click(object sender, RoutedEventArgs e) => Rebuild();

        private void FolderBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string root = string.IsNullOrWhiteSpace(_s.MusicFolder)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
                    : _s.MusicFolder;

                if (Directory.Exists(root))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(root) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SettingsStore.Log("打开音乐文件夹失败: " + ex.Message);
            }
        }
    }
}
