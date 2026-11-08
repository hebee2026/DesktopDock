using System;
using NAudio.Dsp;
using NAudio.Wave;

namespace DesktopDock.Services
{
    /// <summary>
    /// 真频谱：WASAPI Loopback 抓系统正在播的声音，做 FFT 分成 N 段。
    ///
    /// 为什么要抓系统声音而不是抓自己的 MediaPlayer：
    ///   大多数人是用 PotPlayer / 网易云 / 浏览器放歌，收纳盒自己并不出声；
    ///   只有 Loopback 才能拿到"整个系统在放什么"。
    ///
    /// 拿不到（无声卡 loopback / 独占模式 / 无输出设备）时 Bands 全 0，调用方退回内置律动。
    /// </summary>
    public sealed class AudioSpectrum : IDisposable
    {
        public const int BandCount = 32;

        /// <summary>0~1 的 32 段能量，按频率从低到高</summary>
        public float[] Bands { get; } = new float[BandCount];

        /// <summary>是否真的抓到了非静音数据</summary>
        public bool HasSignal { get; private set; }

        public string LastError { get; private set; } = "";

        private const int FftLength = 2048;
        private const int FftLog = 11;          // 2^11 = 2048

        private WasapiLoopbackCapture _capture;
        private readonly float[] _ring = new float[FftLength];
        private int _ringPos;
        private int _ringFill;

        private readonly double[] _window = new double[FftLength];
        private readonly Complex[] _fft = new Complex[FftLength];
        private readonly float[] _smoothed = new float[BandCount];
        private readonly object _lock = new object();
        private readonly int[] _bandLo = new int[BandCount];
        private readonly int[] _bandHi = new int[BandCount];
        private int _sampleRate = 48000;

        public AudioSpectrum()
        {
            for (int i = 0; i < FftLength; i++)
                _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftLength - 1));
        }

        public void Start()
        {
            try
            {
                _capture = new WasapiLoopbackCapture();
                _sampleRate = _capture.WaveFormat.SampleRate;
                BuildBands();

                _capture.DataAvailable += OnData;
                _capture.RecordingStopped += (s, e) =>
                {
                    LastError = e.Exception?.Message ?? "";
                    SettingsStore.Log("音频频谱停止: " + LastError);
                };
                _capture.StartRecording();
                SettingsStore.Log("音频频谱已启动: " + _capture.WaveFormat);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                SettingsStore.Log("音频频谱启动失败: " + ex.Message);
            }
        }

        private void BuildBands()
        {
            // 对数分频：低频段窄、高频段宽，和听感一致
            double minHz = 40, maxHz = Math.Min(16000, _sampleRate / 2.0 - 100);
            double binHz = (double)_sampleRate / FftLength;
            for (int b = 0; b < BandCount; b++)
            {
                double f0 = minHz * Math.Pow(maxHz / minHz, (double)b / BandCount);
                double f1 = minHz * Math.Pow(maxHz / minHz, (double)(b + 1) / BandCount);
                _bandLo[b] = Math.Max(1, (int)(f0 / binHz));
                _bandHi[b] = Math.Min(FftLength / 2 - 1, Math.Max(_bandLo[b] + 1, (int)(f1 / binHz)));
            }
        }

        private void OnData(object sender, WaveInEventArgs e)
        {
            var fmt = _capture.WaveFormat;
            int ch = fmt.Channels;
            int bytesPerSample = fmt.BitsPerSample / 8;
            int frame = bytesPerSample * ch;
            if (frame <= 0) return;
            int frames = e.BytesRecorded / frame;

            lock (_lock)
            {
                for (int i = 0; i < frames; i++)
                {
                    float v = 0;
                    for (int c = 0; c < ch; c++)
                    {
                        int off = i * frame + c * bytesPerSample;
                        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat)
                            v += BitConverter.ToSingle(e.Buffer, off);
                        else if (bytesPerSample == 2)
                            v += BitConverter.ToInt16(e.Buffer, off) / 32768f;
                    }
                    v /= ch;
                    _ring[_ringPos] = v;
                    _ringPos = (_ringPos + 1) % FftLength;
                    if (_ringFill < FftLength) _ringFill++;
                }
            }
        }

        /// <summary>由 UI 定时调用：算一次 FFT，更新 Bands（带平滑）</summary>
        public void Update()
        {
            if (_capture == null) return;

            try
            {
                lock (_lock)
                {
                    if (_ringFill < FftLength) return;

                    for (int i = 0; i < FftLength; i++)
                    {
                        int idx = (_ringPos + i) % FftLength;   // 按时间顺序展开
                        _fft[i].X = (float)(_ring[idx] * _window[i]);
                        _fft[i].Y = 0;
                    }
                }

                FastFourierTransform.FFT(true, FftLog, _fft);

                float max = 0.0001f;
                var raw = new float[BandCount];
                for (int b = 0; b < BandCount; b++)
                {
                    float sum = 0;
                    for (int k = _bandLo[b]; k <= _bandHi[b]; k++)
                    {
                        float mag = (float)Math.Sqrt(_fft[k].X * _fft[k].X + _fft[k].Y * _fft[k].Y);
                        sum += mag;
                    }
                    raw[b] = sum / Math.Max(1, _bandHi[b] - _bandLo[b] + 1);
                    if (raw[b] > max) max = raw[b];
                }

                bool any = false;
                for (int b = 0; b < BandCount; b++)
                {
                    // 归一化 + 开方压一下动态，再和上一帧做平滑（上升快、回落慢）
                    float v = (float)Math.Sqrt(Math.Min(1f, raw[b] / max * 1.6f));
                    if (v > 0.02f) any = true;
                    float prev = _smoothed[b];
                    _smoothed[b] = v > prev ? prev + (v - prev) * 0.65f : prev + (v - prev) * 0.18f;
                    Bands[b] = _smoothed[b];
                }
                HasSignal = any;
            }
            catch { }
        }

        public void Dispose()
        {
            try
            {
                if (_capture != null)
                {
                    _capture.DataAvailable -= OnData;
                    _capture.StopRecording();
                    _capture.Dispose();
                    _capture = null;
                }
            }
            catch { }
        }
    }
}
