using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DesktopDock.Models;
using DesktopDock.Services;

namespace DesktopDock.Views
{
    /// <summary>
    /// 性能块里「后加」的三行：磁盘、GPU0、GPU1。
    /// 单独放一个 partial 文件，避免往 XAML 里堆重复结构。
    /// </summary>
    public partial class InfoWindow
    {
        private sealed class PerfRow
        {
            public string Id;
            public StackPanel Panel;
            public TextBlock Label;
            public Border Track;
            public Border Fill;
            public TextBlock Value;
        }

        private readonly List<PerfRow> _extraRows = new List<PerfRow>();

        private void BuildExtraPerfRows()
        {
            // 幂等：重复调用只会把行越堆越多（之前每改一次设置就多三行）
            if (_extraRows.Count > 0) return;

            AddPerfRow("disk", "DISK");
            AddPerfRow("gpu0", "GPU0");
            AddPerfRow("gpu1", "GPU1");
        }

        private void AddPerfRow(string id, string label)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8),
                Visibility = Visibility.Collapsed
            };

            var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };

            var track = new Border
            {
                Width = 96,
                Height = 3,
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(1)
            };

            var fill = new Border { HorizontalAlignment = HorizontalAlignment.Left, Width = 0, CornerRadius = new CornerRadius(1) };
            track.Child = fill;

            var val = new TextBlock { Text = "--", VerticalAlignment = VerticalAlignment.Center };

            row.Children.Add(lbl);
            row.Children.Add(track);
            row.Children.Add(val);
            PerfBlock.Children.Add(row);

            _extraRows.Add(new PerfRow { Id = id, Panel = row, Label = lbl, Track = track, Fill = fill, Value = val });
        }

        /// <summary>让新增行继承 HUD 字体（否则会和 CPU/RAM/GPU 不是一个字体）</summary>
        private void ApplyExtraPerfFont(double s)
        {
            FontFamily fam;
            try
            {
                fam = string.IsNullOrWhiteSpace(_s.HudFontFamilyName)
                    ? new FontFamily("Microsoft YaHei UI")
                    : new FontFamily(_s.HudFontFamilyName);
            }
            catch { fam = new FontFamily("Microsoft YaHei UI"); }

            foreach (PerfRow r in _extraRows)
                System.Windows.Documents.TextElement.SetFontFamily(r.Panel, fam);
        }

        /// <summary>字号 / 条宽 / 显隐</summary>
        private void ApplyExtraPerfLayout(double s)
        {
            foreach (PerfRow r in _extraRows)
            {
                r.Label.FontSize = _s.HudPerfFontSize * s;
                r.Value.FontSize = _s.HudPerfFontSize * s;
                r.Track.Width = Math.Max(30, _s.HudPerfBarWidth) * s;
                r.Track.Height = Math.Max(2, 3 * s);
                r.Panel.Margin = new Thickness(0, 0, 0, 8);

                bool show;
                switch (r.Id)
                {
                    case "disk": show = _s.HudShowDisk; break;
                    case "gpu0": show = _s.HudShowGpu0; break;
                    default: show = _s.HudShowGpu1; break;
                }
                r.Panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>配色跟随主/次文字</summary>
        private void ApplyExtraPerfPalette(Brush main, Brush dim, Brush faint)
        {
            foreach (PerfRow r in _extraRows)
            {
                r.Label.Foreground = dim;
                r.Value.Foreground = main;
                r.Track.Background = faint;
                r.Fill.Background = dim;
            }
        }

        private void UpdateExtraPerf()
        {
            foreach (PerfRow r in _extraRows)
            {
                double v;
                switch (r.Id)
                {
                    case "disk": v = SystemMonitor.DiskPercent; break;
                    case "gpu0": v = SystemMonitor.Gpu0Percent; break;
                    default: v = SystemMonitor.Gpu1Percent; break;
                }
                SetBar(r.Value, r.Fill, r.Track, v);
            }
        }
    }
}
