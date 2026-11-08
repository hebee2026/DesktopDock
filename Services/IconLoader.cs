using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace DesktopDock.Services
{
    /// <summary>
    /// 取文件/快捷方式的真实大图标（走 IShellItemImageFactory，带 alpha、清晰；
    /// 失败时退回 ExtractAssociatedIcon）。
    /// </summary>
    public static class IconLoader
    {
        private const int SIIGBF_RESIZETOFIT = 0x00;
        private const int SIIGBF_BIGGERSIZEOK = 0x01;
        private const int SIIGBF_ICONONLY = 0x04;

        private static readonly Dictionary<string, BitmapSource> Cache =
            new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);

        private static readonly object Gate = new object();

        private static readonly System.Collections.Concurrent.BlockingCollection<Request> Queue =
            new System.Collections.Concurrent.BlockingCollection<Request>();

        private static readonly object StartGate = new object();
        private static Thread _worker;

        private sealed class Request
        {
            public string Path;
            public int Size;
            public Action<BitmapSource> Done;
        }

        /// <summary>
        /// 异步取图标。Shell 的图标扩展对线程套间很敏感，并发跑在 MTA 线程池上
        /// 会偶发返回空图标 / 张冠李戴，所以统一交给一条 STA 线程串行处理。
        /// </summary>
        public static void GetAsync(string path, int size, Action<BitmapSource> done)
        {
            if (string.IsNullOrEmpty(path)) return;

            EnsureWorker();
            Queue.Add(new Request { Path = path, Size = size, Done = done });
        }

        private static void EnsureWorker()
        {
            if (_worker != null) return;

            lock (StartGate)
            {
                if (_worker != null) return;

                var thread = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = "DesktopDock.IconWorker"
                };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                _worker = thread;
            }
        }

        private static void WorkerLoop()
        {
            foreach (Request request in Queue.GetConsumingEnumerable())
            {
                BitmapSource bitmap = null;
                try
                {
                    bitmap = Get(request.Path, request.Size);
                }
                catch
                {
                    bitmap = null;
                }

                Action<BitmapSource> callback = request.Done;
                if (bitmap == null || callback == null) continue;

                var app = System.Windows.Application.Current;
                if (app == null) continue;

                app.Dispatcher.BeginInvoke(new Action(() => callback(bitmap)));
            }
        }

        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(Native.SIZE size, int flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        public static BitmapSource Get(string path, int size = 48)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string key = path + "|" + size;

            lock (Gate)
            {
                if (Cache.TryGetValue(key, out var cached)) return cached;
            }

            BitmapSource bmp = null;
            try
            {
                bmp = FromShell(path, size);
            }
            catch
            {
                bmp = null;
            }

            if (bmp == null)
            {
                try
                {
                    bmp = FromAssociatedIcon(path);
                }
                catch
                {
                    bmp = null;
                }
            }

            lock (Gate)
            {
                Cache[key] = bmp;
            }

            return bmp;
        }

        private static BitmapSource FromShell(string path, int size)
        {
            IShellItemImageFactory factory;
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out factory);

            IntPtr hbm;
            int hr = factory.GetImage(new Native.SIZE(size, size),
                SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK | SIIGBF_RESIZETOFIT, out hbm);
            if (hr != 0 || hbm == IntPtr.Zero) return null;

            try
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());

                // 必须真正复制像素：直接返回 src 时它仍引用 hbm 的内存，
                // hbm 一释放，位图就可能显示成「下一个」图标的样子。
                var copy = new WriteableBitmap(src);
                copy.Freeze();
                return copy;
            }
            finally
            {
                Native.DeleteObject(hbm);
            }
        }

        private static BitmapSource FromAssociatedIcon(string path)
        {
            using (var icon = Icon.ExtractAssociatedIcon(path))
            {
                if (icon == null) return null;
                var bmp = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                return bmp;
            }
        }
    }
}
