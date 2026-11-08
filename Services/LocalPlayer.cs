using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopDock.Services
{
    /// <summary>
    /// 本地播放列表 + 播放引擎（WPF MediaPlayer，够用且不引第三方库）。
    ///
    /// 它和 <see cref="MediaWatcher"/> 是两条平行的音源：
    ///  - 列表里点的歌走这里（本地文件，能精确 seek）
    ///  - 其它软件在放的歌走 GSMTC（只能跟随、粗粒度 seek）
    /// 壁纸左下角优先显示"本地正在播"，没有本地曲目时再显示系统会话。
    /// </summary>
    public sealed class LocalPlayer
    {
        private static readonly string[] SupportedExtensions =
        {
            ".mp3", ".wav", ".wma", ".m4a", ".aac", ".flac", ".ogg", ".opus"
        };

        private readonly MediaPlayer _player = new MediaPlayer();
        private readonly DispatcherTimer _timer;
        private readonly Dispatcher _dispatcher;

        private bool _durationKnown;

        public List<string> Tracks { get; private set; } = new List<string>();
        public int Index { get; private set; } = -1;

        public bool HasTrack => Index >= 0 && Index < Tracks.Count;
        public bool IsPlaying { get; private set; }

        /// <summary>上一条播放失败（多半是系统缺这个格式的解码器）</summary>
        public bool Failed { get; private set; }
        public TimeSpan Duration { get; private set; }

        public string CurrentPath => HasTrack ? Tracks[Index] : "";

        /// <summary>文件名里带「歌手 - 歌名」就拆开，否则整段当歌名</summary>
        public string Title
        {
            get
            {
                if (!HasTrack) return "";
                var (_, title) = SplitName(Path.GetFileNameWithoutExtension(Tracks[Index]));
                return title;
            }
        }

        public string Artist
        {
            get
            {
                if (!HasTrack) return "";
                var (artist, _) = SplitName(Path.GetFileNameWithoutExtension(Tracks[Index]));
                return artist;
            }
        }

        private static (string Artist, string Title) SplitName(string name)
        {
            int split = name.IndexOf(" - ", StringComparison.Ordinal);
            if (split > 0 && split + 3 < name.Length)
                return (name.Substring(0, split).Trim(), name.Substring(split + 3).Trim());

            return ("", name);
        }

        /// <summary>状态/进度有变化（UI 线程）</summary>
        public event Action Changed;

        public LocalPlayer()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;

            _player.MediaOpened += (s, e) =>
            {
                _durationKnown = _player.NaturalDuration.HasTimeSpan;
                Duration = _durationKnown ? _player.NaturalDuration.TimeSpan : TimeSpan.Zero;
                IsPlaying = true;
                Failed = false;
                Notify();
            };

            _player.MediaEnded += (s, e) => Next();

            _player.MediaFailed += (s, e) =>
            {
                SettingsStore.Log("本地播放失败(" + Path.GetFileName(CurrentPath) + "): "
                                  + (e.ErrorException?.Message ?? "格式不支持"));
                IsPlaying = false;
                Failed = true;
                Notify();
            };

            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _timer.Tick += (s, e) =>
            {
                if (HasTrack) Notify();
            };
            _timer.Start();
        }

        public TimeSpan Position => _player.Position;

        /// <summary>扫描文件夹里的音频文件（含子目录；按相对路径排序）</summary>
        public int LoadFolder(string folder)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                {
                    Tracks = new List<string>();
                    Index = -1;
                    Notify();
                    return 0;
                }

                var files = new List<string>();
                Scan(folder, files, 0);

                Tracks = files
                    .OrderBy(f => Path.GetFileName(f), StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                if (Index >= Tracks.Count) Index = -1;
                Notify();
                return Tracks.Count;
            }
            catch (Exception ex)
            {
                SettingsStore.Log("扫描音乐文件夹失败: " + ex.Message);
                Tracks = new List<string>();
                return 0;
            }
        }

        /// <summary>手写递归：遇到没权限的子目录跳过，不影响其它目录</summary>
        private static void Scan(string folder, List<string> files, int depth)
        {
            if (depth > 6 || files.Count > 3000) return;

            try
            {
                foreach (string file in Directory.GetFiles(folder))
                {
                    if (SupportedExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                        files.Add(file);

                    if (files.Count > 3000) return;
                }
            }
            catch
            {
                // 这个目录读不了就算了
            }

            try
            {
                foreach (string dir in Directory.GetDirectories(folder))
                    Scan(dir, files, depth + 1);
            }
            catch
            {
                // 忽略
            }
        }

        public void Play(int index)
        {
            if (index < 0 || index >= Tracks.Count) return;

            try
            {
                Index = index;
                _durationKnown = false;
                Duration = TimeSpan.Zero;
                IsPlaying = true;
                Failed = false;

                _player.Open(new Uri(Tracks[index]));
                _player.Play();
                SettingsStore.Log("本地播放: " + Path.GetFileName(Tracks[index]));
                Notify();
            }
            catch (Exception ex)
            {
                SettingsStore.Log("播放失败(" + Path.GetFileName(Tracks[index]) + "): " + ex.Message);
                IsPlaying = false;
                Notify();
            }
        }

        public void TogglePlayPause()
        {
            if (!HasTrack) return;

            try
            {
                if (IsPlaying)
                {
                    _player.Pause();
                    IsPlaying = false;
                }
                else
                {
                    _player.Play();
                    IsPlaying = true;
                }
            }
            catch (Exception ex)
            {
                SettingsStore.Log("本地播放切换失败: " + ex.Message);
            }

            Notify();
        }

        public void Next()
        {
            if (Tracks.Count == 0) return;
            Play((Index + 1) % Tracks.Count);
        }

        public void Previous()
        {
            if (Tracks.Count == 0) return;
            Play(Index <= 0 ? Tracks.Count - 1 : Index - 1);
        }

        /// <summary>拖动进度条：本地播放可以直接跳到任意位置</summary>
        public void Seek(TimeSpan position)
        {
            if (!HasTrack) return;

            try
            {
                if (position < TimeSpan.Zero) position = TimeSpan.Zero;
                if (_durationKnown && position > Duration) position = Duration;
                _player.Position = position;
                Notify();
            }
            catch
            {
                // 有些格式不支持 seek，忽略
            }
        }

        public void Stop()
        {
            try { _player.Stop(); } catch { }
            IsPlaying = false;
            Index = -1;
            Notify();
        }

        private void Notify()
        {
            if (_dispatcher.CheckAccess())
            {
                Changed?.Invoke();
                return;
            }

            try { _dispatcher.BeginInvoke(new Action(() => Changed?.Invoke())); }
            catch { }
        }
    }
}
