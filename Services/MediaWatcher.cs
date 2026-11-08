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
        private GlobalSystemMediaTransportControlsSession PickSession()
        {
            if (_manager == null) return null;

            System.Collections.Generic.IReadOnlyList<GlobalSystemMediaTransportControlsSession> all = null;
            try { all = _manager.GetSessions(); } catch { }

            if (all != null && all.Count > 0)
            {
                // 1) 正在播放的优先
                foreach (var s in all)
                {
                    try
                    {
                        if (s.GetPlaybackInfo().PlaybackStatus ==
                            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                            return s;
                    }
                    catch { }
                }

                // 2) 没人在放，但当前会话有内容就用它（保持显示暂停状态）
                try
                {
                    var cur = _manager.GetCurrentSession();
                    if (cur != null) return cur;
                }
                catch { }

                // 3) 兜底：最近一个
                return all[all.Count - 1];
            }

            try { return _manager.GetCurrentSession(); }
            catch { return null; }
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

                BitmapSource cover = await ReadCoverAsync(props.Thumbnail);
                if (cover != null) Current.Cover = cover;
                else if (key != _loadedKey) Current.Cover = null;

                Notify();
            }
            catch (Exception ex)
            {
                SettingsStore.Log("读取媒体信息失败: " + ex.Message);
            }
        }

        /// <summary>把封面缩略图读成 BitmapImage（后台线程建好后 Freeze，UI 直接用）</summary>
        private static async Task<BitmapSource> ReadCoverAsync(IRandomAccessStreamReference reference)
        {
            if (reference == null) return null;

            try
            {
                using (IRandomAccessStreamWithContentType stream = await reference.OpenReadAsync())
                {
                    uint size = (uint)stream.Size;
                    if (size == 0 || size > 8 * 1024 * 1024) return null;

                    var bytes = new byte[size];
                    using (var reader = new DataReader(stream.GetInputStreamAt(0)))
                    {
                        await reader.LoadAsync(size);
                        reader.ReadBytes(bytes);
                    }

                    using (var ms = new MemoryStream(bytes))
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = ms;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        return bitmap;
                    }
                }
            }
            catch
            {
                return null;
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
                bool currentPlaying = false;
                if (session != null)
                {
                    try
                    {
                        currentPlaying = session.GetPlaybackInfo().PlaybackStatus ==
                            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    }
                    catch { currentPlaying = false; }
                }

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
