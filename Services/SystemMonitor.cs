using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DesktopDock.Services
{
    /// <summary>
    /// 壁纸左下角那排 CPU / RAM / GPU 的数据源。
    ///
    /// - CPU：GetSystemTimes 前后差值（不依赖性能计数器，零权限）
    /// - RAM：GlobalMemoryStatusEx 的 dwMemoryLoad
    /// - GPU：性能计数器 "GPU Engine / Utilization Percentage"，把所有引擎加起来（封顶 100）
    ///   GPU 计数器实例会随进程增删变化，所以定期对齐实例名单，避免读到已消失的实例。
    /// </summary>
    public static class SystemMonitor
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct FILEIDTIME
        {
            public uint Low;
            public uint High;
            public ulong Value => ((ulong)High << 32) | Low;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out FILEIDTIME idleTime, out FILEIDTIME kernelTime, out FILEIDTIME userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        public static double CpuPercent { get; private set; }
        public static double RamPercent { get; private set; }
        public static double GpuPercent { get; private set; }

        private static ulong _lastIdle;
        private static ulong _lastKernel;
        private static ulong _lastUser;
        private static bool _hasCpuBaseline;

        private static PerformanceCounter[] _gpuCounters = Array.Empty<PerformanceCounter>();
        private static string[] _gpuInstances = Array.Empty<string>();
        private static DateTime _gpuSyncAt = DateTime.MinValue;

        public static void Sample()
        {
            SampleCpu();
            SampleRam();
            SampleGpu();
        }

        private static void SampleCpu()
        {
            try
            {
                if (!GetSystemTimes(out FILEIDTIME idle, out FILEIDTIME kernel, out FILEIDTIME user)) return;

                ulong idleNow = idle.Value;
                ulong kernelNow = kernel.Value;
                ulong userNow = user.Value;

                if (!_hasCpuBaseline)
                {
                    _lastIdle = idleNow;
                    _lastKernel = kernelNow;
                    _lastUser = userNow;
                    _hasCpuBaseline = true;
                    return;
                }

                ulong dIdle = idleNow - _lastIdle;
                ulong dKernel = kernelNow - _lastKernel;
                ulong dUser = userNow - _lastUser;

                _lastIdle = idleNow;
                _lastKernel = kernelNow;
                _lastUser = userNow;

                // kernel 时间里包含 idle，所以总时间就是 kernel + user
                ulong total = dKernel + dUser;
                if (total == 0) return;

                double busy = 1.0 - (double)dIdle / total;
                CpuPercent = Math.Max(0, Math.Min(100, busy * 100));
            }
            catch
            {
                // 采集失败保持上一次的值
            }
        }

        private static void SampleRam()
        {
            try
            {
                var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
                if (!GlobalMemoryStatusEx(ref status)) return;
                RamPercent = Math.Max(0, Math.Min(100, status.dwMemoryLoad));
            }
            catch
            {
                // 忽略
            }
        }

        private static void SampleGpu()
        {
            try
            {
                if (DateTime.Now - _gpuSyncAt > TimeSpan.FromSeconds(5))
                    SyncGpuCounters();

                if (_gpuCounters.Length == 0)
                {
                    GpuPercent = 0;
                    return;
                }

                double sum = 0;
                foreach (PerformanceCounter counter in _gpuCounters)
                {
                    try { sum += counter.NextValue(); }
                    catch { /* 实例消失，下一轮重建 */ }
                }

                GpuPercent = Math.Max(0, Math.Min(100, sum));
            }
            catch (Exception ex)
            {
                SettingsStore.Log("GPU 采集失败: " + ex.Message);
                GpuPercent = 0;
            }
        }

        /// <summary>实例名单变了才重建计数器（创建 PerformanceCounter 比较贵）</summary>
        private static void SyncGpuCounters()
        {
            _gpuSyncAt = DateTime.Now;

            try
            {
                string[] names = new PerformanceCounterCategory("GPU Engine").GetInstanceNames();
                if (names.Length == 0) return;

                if (names.Length == _gpuInstances.Length && SameNames(names, _gpuInstances)) return;

                var counters = new List<PerformanceCounter>();
                foreach (string name in names)
                {
                    try
                    {
                        counters.Add(new PerformanceCounter("GPU Engine", "Utilization Percentage", name, true));
                    }
                    catch
                    {
                        // 个别实例读不到就跳过
                    }
                }

                _gpuCounters = counters.ToArray();
                _gpuInstances = names;
            }
            catch (Exception ex)
            {
                SettingsStore.Log("GPU 计数器初始化失败: " + ex.Message);
                _gpuCounters = Array.Empty<PerformanceCounter>();
                _gpuInstances = Array.Empty<string>();
            }
        }

        private static bool SameNames(string[] a, string[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }
    }
}
