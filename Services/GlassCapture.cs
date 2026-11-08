using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace DesktopDock.Services
{
    /// <summary>
    /// 毛玻璃底图：抓取面板背后的桌面 → 大幅缩小 → 盒式模糊 → 交给 WPF 双线性放大。
    /// 比实时 Effect 便宜得多（每帧零成本），放大后的柔化效果和真实背景模糊很接近。
    /// </summary>
    public static class GlassCapture
    {
        /// <summary>抓取屏幕物理像素区域；失败返回 null</summary>
        public static BitmapSource Capture(int x, int y, int width, int height)
        {
            if (width <= 0 || height <= 0) return null;

            try
            {
                using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb))
                {
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
                    }

                    BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height),
                        ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                    try
                    {
                        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null,
                            data.Scan0, data.Stride * height, data.Stride);
                        source.Freeze();
                        return source;
                    }
                    finally
                    {
                        bitmap.UnlockBits(data);
                    }
                }
            }
            catch (Exception ex)
            {
                SettingsStore.Log("抓取毛玻璃底图失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>缩小 + 模糊 + 轻微去饱和；scale 越小越糊</summary>
        public static BitmapSource Blur(BitmapSource source, double scale = 0.06, int passes = 2)
        {
            if (source == null) return null;

            try
            {
                var small = new TransformedBitmap(source, new ScaleTransform(scale, scale));
                BitmapSource current = new WriteableBitmap(small);

                for (int i = 0; i < passes; i++)
                    current = BoxBlur(current, 1);

                // 去一点饱和度：背后的图标变成柔和的色块，而不是认得出的图形
                current = Desaturate(current, 0.45);

                var frozen = current;
                if (frozen.CanFreeze) frozen.Freeze();
                return frozen;
            }
            catch (Exception ex)
            {
                SettingsStore.Log("模糊毛玻璃底图失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>向灰度靠拢，amount 0~1</summary>
        private static BitmapSource Desaturate(BitmapSource source, double amount)
        {
            int width = source.PixelWidth;
            int height = source.PixelHeight;
            int stride = width * 4;

            var pixels = new byte[stride * height];
            source.CopyPixels(pixels, stride, 0);

            for (int i = 0; i < pixels.Length; i += 4)
            {
                double b = pixels[i];
                double g = pixels[i + 1];
                double r = pixels[i + 2];
                double gray = 0.114 * b + 0.587 * g + 0.299 * r;

                pixels[i] = (byte)Math.Round(b + (gray - b) * amount);
                pixels[i + 1] = (byte)Math.Round(g + (gray - g) * amount);
                pixels[i + 2] = (byte)Math.Round(r + (gray - r) * amount);
                pixels[i + 3] = 255;
            }

            var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            result.Freeze();
            return result;
        }

        /// <summary>3×3 盒式模糊（在缩略图上跑，开销可忽略）</summary>
        private static BitmapSource BoxBlur(BitmapSource source, int radius)
        {
            int width = source.PixelWidth;
            int height = source.PixelHeight;
            if (width < 3 || height < 3) return source;

            const int bytesPerPixel = 4;
            int stride = width * bytesPerPixel;
            var input = new byte[stride * height];
            source.CopyPixels(input, stride, 0);

            var horizontal = new byte[input.Length];
            for (int y = 0; y < height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < width; x++)
                {
                    int sumB = 0, sumG = 0, sumR = 0, count = 0;
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int sx = x + dx;
                        if (sx < 0 || sx >= width) continue;
                        int offset = row + sx * bytesPerPixel;
                        sumB += input[offset];
                        sumG += input[offset + 1];
                        sumR += input[offset + 2];
                        count++;
                    }

                    int target = row + x * bytesPerPixel;
                    horizontal[target] = (byte)(sumB / count);
                    horizontal[target + 1] = (byte)(sumG / count);
                    horizontal[target + 2] = (byte)(sumR / count);
                    horizontal[target + 3] = 255;
                }
            }

            var output = new byte[input.Length];
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    int sumB = 0, sumG = 0, sumR = 0, count = 0;
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        int sy = y + dy;
                        if (sy < 0 || sy >= height) continue;
                        int offset = sy * stride + x * bytesPerPixel;
                        sumB += horizontal[offset];
                        sumG += horizontal[offset + 1];
                        sumR += horizontal[offset + 2];
                        count++;
                    }

                    int target = y * stride + x * bytesPerPixel;
                    output[target] = (byte)(sumB / count);
                    output[target + 1] = (byte)(sumG / count);
                    output[target + 2] = (byte)(sumR / count);
                    output[target + 3] = 255;
                }
            }

            var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
                output, stride);
            result.Freeze();
            return result;
        }
    }
}
