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

        /// <summary>按显示适配器拆开的 GPU 占用（0 = 第一个适配器，通常是核显）</summary>
        public static double Gpu0Percent { get; private set; }
        public static double Gpu1Percent { get; private set; }

        /// <summary>磁盘活动时间（%）</summary>
        public static double DiskPercent { get; private set; }

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
            SampleDisk();
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

                // 实例名形如 pid_1234_luid_0x00000000_0x0000F6AC_phys_0_eng_0_engtype_3D
                // 同一个 LUID = 同一块显卡，按 LUID 汇总就能拆成 GPU0 / GPU1
                var byLuid = new Dictionary<string, double>(StringComparer.Ordinal);
                for (int i = 0; i < _gpuCounters.Length; i++)
                {
                    double v = 0;
                    try { v = _gpuCounters[i].NextValue(); }
                    catch { continue; }

                    string key = LuidKey(_gpuInstances[i]);
                    if (!byLuid.ContainsKey(key)) byLuid[key] = 0;
                    byLuid[key] += v;
                }

                double sum = 0;
                foreach (var kv in byLuid) sum += kv.Value;
                GpuPercent = Math.Max(0, Math.Min(100, sum));

                var keys = new List<string>(byLuid.Keys);
                keys.Sort(StringComparer.Ordinal);
                Gpu0Percent = keys.Count > 0 ? Math.Max(0, Math.Min(100, byLuid[keys[0]])) : 0;
                Gpu1Percent = keys.Count > 1 ? Math.Max(0, Math.Min(100, byLuid[keys[1]])) : 0;
            }
            catch (Exception ex)
            {
                SettingsStore.Log("GPU 采集失败: " + ex.Message);
                GpuPercent = 0;
            }
        }

        private static readonly System.Text.RegularExpressions.Regex LuidRe =
            new System.Text.RegularExpressions.Regex(@"luid_0x([0-9A-Fa-f]+)_0x([0-9A-Fa-f]+)");

        private static string LuidKey(string instance)
        {
            var m = LuidRe.Match(instance ?? "");
            return m.Success ? (m.Groups[1].Value + "_" + m.Groups[2].Value).ToUpperInvariant() : "?";
        }

        private static PerformanceCounter _diskCounter;

        private static void SampleDisk()
        {
            try
            {
                if (_diskCounter == null)
                    _diskCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total", true);
                double v = _diskCounter.NextValue();
                DiskPercent = Math.Max(0, Math.Min(100, v));
            }
            catch
            {
                DiskPercent = 0;
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
