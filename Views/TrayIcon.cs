using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DesktopDock.Views
{
    /// <summary>托盘图标 + 右键菜单</summary>
    public sealed class TrayIcon : IDisposable
    {
        private readonly NotifyIcon _notify;
        private readonly ToolStripMenuItem _openItem;
        private readonly ToolStripMenuItem _hideIconsItem;
        private readonly ToolStripMenuItem _autoStartItem;

        public event Action ToggleRequested;
        public event Action RefreshRequested;
        public event Action HideIconsToggled;
        public event Action AutoStartToggled;
        public event Action ExitRequested;
        public event Action HudSettingsRequested;

        public TrayIcon()
        {
            var menu = new ContextMenuStrip { ShowImageMargin = false };

            _openItem = new ToolStripMenuItem("打开收纳盒", null, (s, e) => ToggleRequested?.Invoke());
            menu.Items.Add(_openItem);
            menu.Items.Add(new ToolStripMenuItem("重新扫描桌面", null, (s, e) => RefreshRequested?.Invoke()));
            menu.Items.Add(new ToolStripMenuItem("壁纸组件外观…", null, (s, e) => HudSettingsRequested?.Invoke()));
            menu.Items.Add(new ToolStripSeparator());

            _hideIconsItem = new ToolStripMenuItem("隐藏桌面图标", null, (s, e) => HideIconsToggled?.Invoke());
            _hideIconsItem.CheckOnClick = false;
            menu.Items.Add(_hideIconsItem);

            _autoStartItem = new ToolStripMenuItem("开机自动启动", null, (s, e) => AutoStartToggled?.Invoke());
            _autoStartItem.CheckOnClick = false;
            menu.Items.Add(_autoStartItem);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("退出", null, (s, e) => ExitRequested?.Invoke()));

            _notify = new NotifyIcon
            {
                Text = "桌面收纳盒",
                ContextMenuStrip = menu
            };

            try
            {
                _notify.Icon = LoadIcon();
            }
            catch (Exception ex)
            {
                Services.SettingsStore.Log("托盘图标加载失败，改用系统图标: " + ex.Message);
                _notify.Icon = SystemIcons.Application;
            }

            _notify.Visible = true;

            _notify.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) ToggleRequested?.Invoke();
            };
            _notify.DoubleClick += (s, e) => ToggleRequested?.Invoke();
        }

        /// <summary>优先用内嵌的 app.ico（不依赖外部文件），失败再退回 exe 自带图标</summary>
        private static Icon LoadIcon()
        {
            try
            {
                var uri = new Uri("pack://application:,,,/assets/app.ico", UriKind.Absolute);
                System.Windows.Resources.StreamResourceInfo info =
                    System.Windows.Application.GetResourceStream(uri);
                if (info != null)
                {
                    using (System.IO.Stream stream = info.Stream)
                    {
                        return new Icon(stream, 32, 32);
                    }
                }
            }
            catch (Exception ex)
            {
                Services.SettingsStore.Log("读取内嵌图标失败: " + ex.Message);
            }

            try
            {
                string exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    Icon icon = Icon.ExtractAssociatedIcon(exe);
                    if (icon != null) return icon;
                }
            }
            catch
            {
                // 走最后的兜底
            }

            return SystemIcons.Application;
        }

        public void SetOpenState(bool open)
        {
            _openItem.Text = open ? "收起收纳盒" : "打开收纳盒";
        }

        public void SetHideIconsChecked(bool value)
        {
            _hideIconsItem.Checked = value;
        }

        public void SetAutoStartChecked(bool value)
        {
            _autoStartItem.Checked = value;
        }

        public void ShowBalloon(string title, string text)
        {
            try
            {
                _notify.BalloonTipTitle = title;
                _notify.BalloonTipText = text;
                _notify.ShowBalloonTip(2500);
            }
            catch
            {
                // 忽略
            }
        }

        private static Icon BuildIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    using (GraphicsPath path = RoundedRect(new RectangleF(2, 2, 28, 28), 9))
                    {
                        using (var brush = new LinearGradientBrush(new PointF(2, 2), new PointF(30, 30),
                                   Color.FromArgb(255, 62, 74, 96), Color.FromArgb(255, 22, 27, 36)))
                        {
                            g.FillPath(brush, path);
                        }

                        using (var pen = new Pen(Color.FromArgb(110, 232, 199, 155), 1.4f))
                        {
                            g.DrawPath(pen, path);
                        }
                    }

                    using (var dot = new SolidBrush(Color.FromArgb(240, 232, 199, 155)))
                    {
                        g.FillEllipse(dot, 10f, 9f, 5.5f, 5.5f);
                        g.FillEllipse(dot, 17.5f, 9f, 5.5f, 5.5f);
                        g.FillEllipse(dot, 10f, 16.5f, 5.5f, 5.5f);
                        g.FillEllipse(dot, 17.5f, 16.5f, 5.5f, 5.5f);
                    }
                }

                IntPtr handle = bmp.GetHicon();
                return Icon.FromHandle(handle);
            }
        }

        private static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public void Dispose()
        {
            try
            {
                _notify.Visible = false;
                _notify.Dispose();
            }
            catch
            {
                // 忽略
            }
        }
    }
}
