using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DesktopDock.Services
{
    /// <summary>当前正在播放的信息（给壁纸左下角用）</summary>
    public sealed class NowPlaying
    {
        public bool HasSession;
        public string Title = "";
        public string Artist = "";
        public string Album = "";
        public bool IsPlaying;
        public TimeSpan Position;
        public TimeSpan Duration;
        public BitmapSource Cover;
        public string CoverKey = "";   // 这张封面属于哪首歌（title|artist|album）
    }

    /// <summary>
    /// 读系统「正在播放」的信息：GSMTC（GlobalSystemMediaTransportControlsSession）。
    /// 浏览器、Spotify、网易云、播放器只要支持系统媒体控制，这里都能读到。
    ///
    /// 播放状态和进度每秒 Poll 一次；歌曲/封面变化靠 MediaPropertiesChanged 事件，
    /// 避免每秒都去要一次 MediaProperties（那个是 async 且比较重）。
    /// </summary>
    public sealed class MediaWatcher
    {
        private readonly Dispatcher _dispatcher;

        private GlobalSystemMediaTransportControlsSessionManager _manager;
        private GlobalSystemMediaTransportControlsSession _session;

        private string _loadedKey = "";
        private int _lastCoverHash;
        private string _lastCoverAlbum = "";
        private DateTimeOffset _lastRepick = DateTimeOffset.MinValue;

        public NowPlaying Current { get; } = new NowPlaying();

        /// <summary>信息有变化（UI 线程上触发）</summary>
        public event Action Updated;

        public MediaWatcher()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
        }

        public void Start()
        {
            _ = InitAsync();
        }

        private async Task InitAsync()
        {
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _manager.CurrentSessionChanged += (s, e) => Rebind();
                Rebind();
                SettingsStore.Log("媒体会话已就绪");
            }
            catch (Exception ex)
            {
                SettingsStore.Log("媒体会话初始化失败: " + ex.Message);
            }
        }

        private void Rebind()
        {
            try
            {
                if (_session != null) _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            }
            catch
            {
                // 旧会话可能已经失效
            }

            _session = null;
            try { _session = PickSession(); }
            catch { _session = null; }

            _loadedKey = "";

            if (_session != null)
            {
                try { _session.MediaPropertiesChanged += OnMediaPropertiesChanged; }
                catch { }
                _ = LoadPropertiesAsync();
            }
            else
            {
                Current.HasSession = false;
                Current.Title = "";
                Current.Artist = "";
                Current.Album = "";
                Current.Cover = null;
                Current.CoverKey = "";
                Notify();
            }

            Poll();
        }

        private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender,
            MediaPropertiesChangedEventArgs args)
        {
            _ = LoadPropertiesAsync();
        }

        /// <summary>
        /// 挑会话：优先"真的在播放"的那个。
        /// 不能只用 GetCurrentSession() —— QQ音乐/部分播放器不抢 CurrentSession，
        /// 系统会把"当前"停留在上一个已经停掉的播放器上，结果明明在放却显示未在播放。
        /// </summary>
        /// <summary>
        /// 判断一个会话是否"真的在放"。
        /// 光看 PlaybackStatus 不够：浏览器、某些播放器会留下一直报 Playing 的幽灵会话，
        /// 位置却不再推进。真正在播的会话 timeline 会持续刷新，所以再加一条时间新鲜度判据。
        /// </summary>
        private static bool IsLivePlaying(GlobalSystemMediaTransportControlsSession s)
        {
            try
            {
                if (s.GetPlaybackInfo().PlaybackStatus !=
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    return false;

                var tl = s.GetTimelineProperties();
                var age = DateTimeOffset.Now - tl.LastUpdatedTime;
                return age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(15);
            }
            catch { return false; }
        }

        private static DateTimeOffset LastActive(GlobalSystemMediaTransportControlsSession s)
        {
            try { return s.GetTimelineProperties().LastUpdatedTime; }
            catch { return DateTimeOffset.MinValue; }
        }

        private GlobalSystemMediaTransportControlsSession PickSession()
        {
            if (_manager == null) return null;

            System.Collections.Generic.IReadOnlyList<GlobalSystemMediaTransportControlsSession> all = null;
            try { all = _manager.GetSessions(); } catch { }

            GlobalSystemMediaTransportControlsSession current = null;
            try { current = _manager.GetCurrentSession(); } catch { }

            if (all == null || all.Count == 0) return current;

            // 1) 当前会话真的在放 → 就用它（这是"用户最近操作的那个播放器"）
            if (current != null && IsLivePlaying(current)) return current;

            // 2) 否则在所有"真的在放"的会话里挑最近更新过的
            GlobalSystemMediaTransportControlsSession best = null;
            DateTimeOffset bestTime = DateTimeOffset.MinValue;

            foreach (var s in all)
            {
                if (!IsLivePlaying(s)) continue;
                var t = LastActive(s);
                if (t > bestTime) { bestTime = t; best = s; }
            }
            if (best != null) return best;

            // 3) 没人在放 → 保持当前会话（显示暂停/结束状态），否则取最近更新的
            if (current != null) return current;

            GlobalSystemMediaTransportControlsSession fallback = null;
            DateTimeOffset fallbackTime = DateTimeOffset.MinValue;
            foreach (var s in all)
            {
                var t = LastActive(s);
                if (t > fallbackTime) { fallbackTime = t; fallback = s; }
            }
            return fallback ?? all[all.Count - 1];
        }

        private async Task LoadPropertiesAsync()
        {
            var session = _session;
            if (session == null) return;

            try
            {
                GlobalSystemMediaTransportControlsSessionMediaProperties props =
                    await session.TryGetMediaPropertiesAsync();

                string key = (props.Title ?? "") + "|" + (props.Artist ?? "") + "|" + (props.AlbumTitle ?? "");
                if (key == _loadedKey) return;
                _loadedKey = key;

                Current.Title = props.Title ?? "";
                Current.Artist = props.Artist ?? "";
                Current.Album = props.AlbumTitle ?? "";
                Current.HasSession = true;

                // 换歌先清掉旧封面，否则新歌没有内嵌封面时，
                // 会一直挂着上一首的图（就是"封面和音乐不同步"）
                Current.Cover = null;
                Current.CoverKey = "";
                Notify();

                var (cover, coverHash) = await ReadCoverAsync(props.Thumbnail);

                // 播放器在切到"无封面"的歌时，往往继续吐上一张图。
                // 判定：字节指纹和上一首完全相同，但专辑换了 → 认定为残留，不采用
                bool albumChanged = !string.Equals(props.AlbumTitle ?? "", _lastCoverAlbum,
                                                   StringComparison.Ordinal);
                if (cover != null && albumChanged && coverHash != 0 && coverHash == _lastCoverHash)
                {
                    SettingsStore.Log("封面判定为播放器残留，丢弃: " + key);
                    cover = null;
                }
                if (cover != null)
                {
                    _lastCoverHash = coverHash;
                    _lastCoverAlbum = props.AlbumTitle ?? "";
                }

                // 等封面的这段时间里可能又换歌了，只在这个 key 仍是当前曲目时才应用
                if (cover != null && key == _loadedKey)
                SettingsStore.Log("封面读取 key=" + key + " 结果=" + (cover == null ? "null" : "ok") + " 当前key=" + _loadedKey);
                {
                    Current.Cover = cover;
                    Current.CoverKey = key;
                    SettingsStore.Log("封面采用 key=" + key + " 尺寸=" + cover.PixelWidth + "x" + cover.PixelHeight);
                    Notify();
                }
            }
            catch (Exception ex)
            {
                SettingsStore.Log("读取媒体信息失败: " + ex.Message);
            }
        }

        /// <summary>把封面缩略图读成 BitmapImage（后台线程建好后 Freeze，UI 直接用）</summary>
        /// <summary>把封面缩略图读成 BitmapImage；同时返回字节指纹，用于识别播放器残留的旧封面</summary>
        private static async Task<(BitmapSource bmp, int hash)> ReadCoverAsync(
            IRandomAccessStreamReference reference)
        {
            if (reference == null) return (null, 0);

            try
            {
                using (IRandomAccessStreamWithContentType stream = await reference.OpenReadAsync())
                {
                    uint size = (uint)stream.Size;
                    if (size == 0 || size > 8 * 1024 * 1024) return (null, 0);

                    var bytes = new byte[size];
                    using (var reader = new DataReader(stream.GetInputStreamAt(0)))
                    {
                        await reader.LoadAsync(size);
                        reader.ReadBytes(bytes);
                    }

                    int h = 17;
                    for (int i = 0; i < bytes.Length; i += 97) h = h * 31 + bytes[i];
                    h ^= bytes.Length;

                    using (var ms = new MemoryStream(bytes))
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = ms;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        return (bitmap, h);
                    }
                }
            }
            catch
            {
                return (null, 0);
            }
        }

        /// <summary>每秒调用：刷新播放状态与进度</summary>
        public void Poll()
        {
            var session = _session;

            // 当前会话没在放、但另有播放器在放 → 换过去（每 2 秒最多切一次）
            if (DateTimeOffset.Now - _lastRepick > TimeSpan.FromSeconds(2))
            {
                _lastRepick = DateTimeOffset.Now;
                bool currentPlaying = session != null && IsLivePlaying(session);

                if (!currentPlaying)
                {
                    var better = PickSession();
                    if (better != null && !ReferenceEquals(better, session))
                    {
                        try
                        {
                            if (session != null) session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                        }
                        catch { }

                        _session = session = better;
                        _loadedKey = "";
                        try { session.MediaPropertiesChanged += OnMediaPropertiesChanged; } catch { }
                        _ = LoadPropertiesAsync();
                    }
                }
            }

            if (session == null)
            {
                // 没有 GSMTC 会话（QQ音乐这类不注册的）→ 读窗口标题兜底
                if (WindowTitleReader.TryGetTrack(out string fbTitle, out string fbArtist))
                {
                    bool changed = !Current.HasSession
                                   || Current.Title != fbTitle
                                   || Current.Artist != fbArtist;

                    Current.HasSession = true;
                    Current.IsPlaying = true;
                    Current.Title = fbTitle;
                    Current.Artist = fbArtist;
                    Current.Position = TimeSpan.Zero;
                    Current.Duration = TimeSpan.Zero;
                    if (changed) Notify();
                }
                else if (Current.HasSession)
                {
                    Current.HasSession = false;
                    Current.Title = "";
                    Current.Artist = "";
                    Current.Cover = null;
                    Current.CoverKey = "";
                    Notify();
                }

                return;
            }

            try
            {
                GlobalSystemMediaTransportControlsSessionPlaybackInfo info = session.GetPlaybackInfo();
                GlobalSystemMediaTransportControlsSessionTimelineProperties timeline = session.GetTimelineProperties();

                Current.HasSession = true;
                Current.IsPlaying = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                TimeSpan duration = timeline.EndTime - timeline.StartTime;
                if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

                TimeSpan position = timeline.Position - timeline.StartTime;
                if (Current.IsPlaying)
                {
                    // 播放器不会每帧推进度，用 LastUpdatedTime 自己外推
                    TimeSpan drift = DateTimeOffset.Now - timeline.LastUpdatedTime;
                    if (drift > TimeSpan.Zero && drift < TimeSpan.FromMinutes(30)) position += drift;
                }

                if (position < TimeSpan.Zero) position = TimeSpan.Zero;
                if (duration > TimeSpan.Zero && position > duration) position = duration;

                Current.Duration = duration;
                Current.Position = position;
                Notify();
            }
            catch
            {
                // 会话可能刚结束，下次 Poll 会重新判断
            }
        }

        // ───────────────────────── 控制 ─────────────────────────

        /// <summary>播放 / 暂停（交给系统媒体会话去处理，播放器自己决定怎么响应）</summary>
        public async Task<bool> TogglePlayPauseAsync()
        {
            var session = _session;
            if (session == null) return false;

            try
            {
                bool ok = await session.TryTogglePlayPauseAsync();
                Poll();
                return ok;
            }
            catch (Exception ex)
            {
                SettingsStore.Log("播放/暂停失败: " + ex.Message);
                return false;
            }
        }

        public async Task<bool> NextAsync()
        {
            var session = _session;
            if (session == null) return false;

            try
            {
                bool ok = await session.TrySkipNextAsync();
                Poll();
                return ok;
            }
            catch (Exception ex)
            {
                SettingsStore.Log("下一首失败: " + ex.Message);
                return false;
            }
        }

        public async Task<bool> PreviousAsync()
        {
            var session = _session;
            if (session == null) return false;

            try
            {
                bool ok = await session.TrySkipPreviousAsync();
                Poll();
                return ok;
            }
            catch (Exception ex)
            {
                SettingsStore.Log("上一首失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>把进度条拖到指定位置（由播放器决定是否支持）</summary>
        public async Task<bool> SeekAsync(TimeSpan position)
        {
            var session = _session;
            if (session == null) return false;

            try
            {
                bool ok = await session.TryChangePlaybackPositionAsync(position.Ticks);
                Poll();
                return ok;
            }
            catch (Exception ex)
            {
                SettingsStore.Log("跳转播放位置失败: " + ex.Message);
                return false;
            }
        }

        private void Notify()
        {
            if (_dispatcher.CheckAccess())
            {
                Updated?.Invoke();
                return;
            }

            try
            {
                _dispatcher.BeginInvoke(new Action(() => Updated?.Invoke()));
            }
            catch
            {
                // 程序退出时调度器可能已关闭
            }
        }
    }
}
