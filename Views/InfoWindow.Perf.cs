using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
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
        private double _uiScale = 1;   // ApplySettings 算出的 UI 缩放，配色时也要用

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
                fam = ResolveFont(_s.HudFontPerf);
            }
            catch { fam = new FontFamily("Microsoft YaHei UI"); }

            foreach (PerfRow r in _extraRows)
                System.Windows.Documents.TextElement.SetFontFamily(r.Panel, fam);
        }

        /// <summary>
        /// 性能块整体倾斜：每一行各自以左端为轴错切（y 随 x 变化），
        /// 于是「标签在左上、数值在右下」，行与行的左边缘仍然对齐。
        /// 行内文字再做一次反向错切，保持不歪。
        /// </summary>
        /// <summary>
        /// 性能块位置 + 旋转。旋转是刚体变换，不会被拉变形；
        /// 块内文字再反向旋转，保持字形正立。
        /// </summary>
        private void ApplyPerfSkew(double angleDeg, double offX, double offY)
        {
            double ang = Math.Max(-30, Math.Min(30, angleDeg));

            var g = new TransformGroup();
            g.Children.Add(new TranslateTransform(offX, offY));
            g.Children.Add(new RotateTransform(ang));
            PerfBlock.RenderTransformOrigin = new Point(0, 1);   // 绕左下角
            PerfBlock.RenderTransform = g;

            var rows = new List<FrameworkElement> { CpuBlock, RamBlock, GpuBlock };
            foreach (PerfRow r in _extraRows) rows.Add(r.Panel);

            foreach (FrameworkElement row in rows)
            {
                foreach (TextBlock tb in FindTextBlocks(row))
                {
                    if (Math.Abs(ang) < 0.01) { tb.RenderTransform = null; continue; }
                    tb.RenderTransformOrigin = new Point(0.5, 0.5);
                    tb.RenderTransform = new RotateTransform(-ang);
                }
            }
        }

        /// <summary>布局变化后重新应用（行显隐/字号变化时）</summary>
        internal void RefreshPerfQuad()
        {
            try { Dispatcher.BeginInvoke(new Action(() => ApplyPerfSkew(_s.HudTiltPerf, _s.HudPerfOffsetX * _uiScale, -_s.HudPerfOffsetY * _uiScale))); }
            catch { }
        }




        /// <summary>六行标签/数值统一样式，消除亮度差异</summary>
        internal void NormalizePerfText(Brush labelBrush, Brush valueBrush, double fontSize)
        {
            foreach (var lbl in new[] { CpuLabel, RamLabel, GpuLabel })
            {
                lbl.Foreground = labelBrush;
                lbl.Opacity = 1;
                lbl.FontSize = fontSize;
                lbl.FontWeight = FontWeights.Normal;
            }
            foreach (var val in new[] { CpuValue, RamValue, GpuValue })
            {
                val.Foreground = valueBrush;
                val.Opacity = 1;
                val.FontSize = fontSize;
                val.FontWeight = FontWeights.Normal;
            }
            foreach (PerfRow r in _extraRows)
            {
                r.Label.Foreground = labelBrush; r.Label.Opacity = 1;
                r.Label.FontSize = fontSize; r.Label.FontWeight = FontWeights.Normal;
                r.Value.Foreground = valueBrush; r.Value.Opacity = 1;
                r.Value.FontSize = fontSize; r.Value.FontWeight = FontWeights.Normal;
            }
        }

        /// <summary>把 HUD 贴回当前屏幕大小（全屏应用退出后分辨率可能变过）</summary>
        internal void FitToScreen()
        {
            try
            {
                double w = SystemParameters.PrimaryScreenWidth;
                double h = SystemParameters.PrimaryScreenHeight;
                if (Math.Abs(Width - w) > 1 || Math.Abs(Height - h) > 1)
                {
                    Left = 0; Top = 0; Width = w; Height = h;
                    ApplySettings();
                    SettingsStore.Log("HUD 尺寸已重设 " + w + "x" + h);
                }
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        /// <summary>把 PotPlayer 窗口呼到最前；找不到返回 false</summary>
        private static bool BringPotPlayerToFront()
        {
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcesses())
                {
                    string name = "";
                    try { name = p.ProcessName; } catch { continue; }
                    if (name.IndexOf("PotPlayer", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    IntPtr h = p.MainWindowHandle;
                    if (h == IntPtr.Zero) continue;

                    if (IsIconic(h)) Native.ShowWindow(h, 9);   // SW_RESTORE
                    Native.ShowWindow(h, 5);                    // SW_SHOW
                    SetForegroundWindow(h);
                    SettingsStore.Log("已唤醒 PotPlayer 窗口: " + name);
                    return true;
                }
            }
            catch (Exception ex) { SettingsStore.Log("唤出 PotPlayer 失败: " + ex.Message); }
            return false;
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
        private void ApplyExtraPerfPalette(Brush main, Brush dim, Brush faint, Brush fill)
        {
            foreach (PerfRow r in _extraRows)
            {
                // 直接复用 CPU 那一行正在用的 Brush 实例：
                // 同一个画刷对象，颜色/不透明度必然完全一致（之前新建画刷会有细微差别）
                r.Label.Foreground = CpuLabel.Foreground;
                r.Value.Foreground = CpuValue.Foreground;
                r.Label.Opacity = 1;
                r.Value.Opacity = 1;
                r.Track.Background = faint;  // 轨道底
                r.Fill.Background = fill;    // 填充 = 强调色
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
