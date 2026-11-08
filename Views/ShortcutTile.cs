using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopDock.Models;

namespace DesktopDock.Views
{
    /// <summary>一个桌面项目的磁贴：图标 + 名称，单击启动。</summary>
    public sealed class ShortcutTile : Border
    {
        private static readonly Brush HoverBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush ClearBrush = Brushes.Transparent;
        private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)));

        private readonly Image _image;
        private readonly TextBlock _text;

        public ShortcutItem Item { get; }

        public event EventHandler Activated;

        public ShortcutTile(ShortcutItem item, double width, double iconSize, double nameSize, FontFamily font)
        {
            Item = item;

            Width = width;
            Margin = new Thickness(2);
            Padding = new Thickness(2, 8, 2, 8);
            CornerRadius = new CornerRadius(10);
            Background = ClearBrush;
            Cursor = System.Windows.Input.Cursors.Hand;

            _image = new Image
            {
                Width = iconSize,
                Height = iconSize,
                Stretch = Stretch.Uniform,
                Source = item.Icon
            };

            _text = new TextBlock
            {
                Text = item.DisplayName,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = nameSize,
                FontFamily = font,
                Foreground = TextBrush,
                Margin = new Thickness(0, 7, 0, 0),
                MaxHeight = Math.Ceiling(nameSize * 1.36 * 2)
            };

            var stack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            stack.Children.Add(new Border { Child = _image, Width = iconSize, Height = iconSize });
            stack.Children.Add(_text);
            Child = stack;

            if (item.IsFolder)
                ToolTip = item.Path + "（文件夹）";
            else
                ToolTip = item.Path;

            MouseEnter += (s, e) => Background = HoverBrush;
            MouseLeave += (s, e) => Background = ClearBrush;
            MouseLeftButtonUp += (s, e) => Activated?.Invoke(this, EventArgs.Empty);
        }

        public void SetIcon(BitmapSource bmp)
        {
            if (bmp != null) _image.Source = bmp;
        }

        public void SetNameFont(double size)
        {
            _text.FontSize = size;
            _text.MaxHeight = Math.Ceiling(size * 1.36 * 2);
        }

        private static Brush Freeze(Brush brush)
        {
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }
    }
}
