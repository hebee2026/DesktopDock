using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DesktopDock.Models
{
    /// <summary>所有用户可调项，持久化到 %APPDATA%\DesktopDock\settings.json</summary>
    public sealed class AppSettings : INotifyPropertyChanged
    {
        private string _fontFamilyName = "Microsoft YaHei UI";
        private double _clockFontSize = 36;
        private double _weatherFontSize = 15;
        private double _shortcutNameFontSize = 12;
        private double _shortcutIconSize = 40;
        private double _panelWidth = 420;
        private double _panelOpacity = 0.82;
        private bool _glassEffect = true;
        private bool _transparentEffect = false;
        private int _settingsVersion = 2;
        private int _lod = 2;
        private bool _showSeconds = true;
        private bool _use24Hour = true;
        private bool _showWeather = true;
        private string _weatherCity = "上海";
        private double _weatherLat = 31.2222;
        private double _weatherLon = 121.4581;
        private bool _useCelsius = true;
        private int _edgeTriggerWidth = 4;
        private int _autoHideDelayMs = 700;
        private bool _hideDesktopIcons = false;
        private bool _infoHudEnabled = true;
        private bool _infoHudShowPerf = true;
        private bool _infoHudShowMusic = true;
        private string _musicFolder = "";
        private double _infoHudFontSize = 13;
        private double _infoHudScale = 1.0;
        private bool _onlyOnDesktop = true;
        private bool _showEdgeTab = true;
        private bool _startWithWindows = false;
        private string _autoStartPreferred = "run";
        private bool _widgetEnabled = true;
        private int _widgetLod = 2;
        private string _widgetFontFamilyName = "";
        private double _widgetFontSize = 80;
        private double _widgetMarginRight = 52;
        private double _widgetMarginTop = 52;

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>界面字体（任意已安装字体名）</summary>
        public string FontFamilyName { get => _fontFamilyName; set => Set(ref _fontFamilyName, value); }

        /// <summary>时钟字号</summary>
        public double ClockFontSize { get => _clockFontSize; set => Set(ref _clockFontSize, value); }

        /// <summary>天气字号</summary>
        public double WeatherFontSize { get => _weatherFontSize; set => Set(ref _weatherFontSize, value); }

        /// <summary>快捷方式名称字号</summary>
        public double ShortcutNameFontSize { get => _shortcutNameFontSize; set => Set(ref _shortcutNameFontSize, value); }

        /// <summary>快捷方式图标大小</summary>
        public double ShortcutIconSize { get => _shortcutIconSize; set => Set(ref _shortcutIconSize, value); }

        /// <summary>面板宽度</summary>
        public double PanelWidth { get => _panelWidth; set => Set(ref _panelWidth, value); }

        /// <summary>蒙层浓度 0 ~ 1：0 = 完全透明（只剩毛玻璃/壁纸），1 = 接近不透明</summary>
        public double PanelOpacity
        {
            get => _panelOpacity;
            set => Set(ref _panelOpacity, Math.Max(0, Math.Min(1, value)));
        }

        /// <summary>毛玻璃背景：真模糊桌面，而不是单纯半透明</summary>
        public bool GlassEffect { get => _glassEffect; set => Set(ref _glassEffect, value); }

        /// <summary>
        /// 透明效果：面板不再截屏做毛玻璃，直接把桌面/窗口实时透出来（只剩蒙层压暗）。
        /// 和 GlassEffect 互斥，开启时忽略毛玻璃。
        /// </summary>
        public bool TransparentEffect { get => _transparentEffect; set => Set(ref _transparentEffect, value); }

        /// <summary>配置版本，用于老配置迁移</summary>
        public int SettingsVersion { get => _settingsVersion; set => Set(ref _settingsVersion, value); }

        /// <summary>信息层级：1 简洁 / 2 标准 / 3 丰富</summary>
        public int Lod { get => _lod; set => Set(ref _lod, Math.Max(1, Math.Min(3, value))); }

        public bool ShowSeconds { get => _showSeconds; set => Set(ref _showSeconds, value); }

        public bool Use24Hour { get => _use24Hour; set => Set(ref _use24Hour, value); }

        public bool ShowWeather { get => _showWeather; set => Set(ref _showWeather, value); }

        public string WeatherCity { get => _weatherCity; set => Set(ref _weatherCity, value); }

        public double WeatherLat { get => _weatherLat; set => Set(ref _weatherLat, value); }

        public double WeatherLon { get => _weatherLon; set => Set(ref _weatherLon, value); }

        public bool UseCelsius { get => _useCelsius; set => Set(ref _useCelsius, value); }

        /// <summary>屏幕最左侧多少像素算触发区</summary>
        public int EdgeTriggerWidth { get => _edgeTriggerWidth; set => Set(ref _edgeTriggerWidth, Math.Max(1, Math.Min(24, value))); }

        /// <summary>鼠标离开后多久自动收起</summary>
        public int AutoHideDelayMs { get => _autoHideDelayMs; set => Set(ref _autoHideDelayMs, Math.Max(150, Math.Min(5000, value))); }

        public bool HideDesktopIcons { get => _hideDesktopIcons; set => Set(ref _hideDesktopIcons, value); }

        // ───────────── 壁纸左下角：性能 + 正在播放 ─────────────

        /// <summary>显示左下角信息条</summary>
        public bool InfoHudEnabled { get => _infoHudEnabled; set => Set(ref _infoHudEnabled, value); }

        /// <summary>显示 CPU / RAM / GPU</summary>
        public bool InfoHudShowPerf { get => _infoHudShowPerf; set => Set(ref _infoHudShowPerf, value); }

        /// <summary>显示正在播放的音乐</summary>
        public bool InfoHudShowMusic { get => _infoHudShowMusic; set => Set(ref _infoHudShowMusic, value); }

        /// <summary>本地播放列表扫描的音乐文件夹（空 = 系统「我的音乐」）</summary>
        public string MusicFolder
        {
            get => _musicFolder;
            set => Set(ref _musicFolder, value ?? "");
        }

        /// <summary>信息条字号</summary>
        public double InfoHudFontSize
        {
            get => _infoHudFontSize;
            set => Set(ref _infoHudFontSize, Math.Max(9, Math.Min(28, value)));
        }

        /// <summary>
        /// 信息条整体缩放：字号、封面、细线长度、间距一起按比例放大/缩小。
        /// 1.0 = 默认大小。
        /// </summary>
        public double InfoHudScale
        {
            get => _infoHudScale;
            set => Set(ref _infoHudScale, Math.Max(0.6, Math.Min(2.2, value)));
        }

        /// <summary>仅在桌面为前台窗口时才响应左边缘悬停（避免在工作软件上误弹出）</summary>
        public bool OnlyOnDesktop { get => _onlyOnDesktop; set => Set(ref _onlyOnDesktop, value); }

        public bool ShowEdgeTab { get => _showEdgeTab; set => Set(ref _showEdgeTab, value); }
        // ───────── 壁纸 HUD 外观自定义 ─────────
        private bool _hudCustomColors = false;
        private string _hudTextColor = "#F2FEFF";
        private string _hudSubColor = "#BFFCFF";
        private string _hudAccentColor = "#9CF4FF";
        private string _hudFontFamilyName = "";
        private double _hudPerfFontSize = 11;
        private double _hudLyricFontSize = 15;
        private double _hudOffsetX = 0;
        private double _hudOffsetY = 0;

        /// <summary>关掉就跟随壁纸明暗自动配色</summary>
        public bool HudCustomColors { get => _hudCustomColors; set => Set(ref _hudCustomColors, value); }

        /// <summary>主文字色 #RRGGBB</summary>
        public string HudTextColor { get => _hudTextColor; set => Set(ref _hudTextColor, value ?? "#F2FEFF"); }

        /// <summary>次文字色</summary>
        public string HudSubColor { get => _hudSubColor; set => Set(ref _hudSubColor, value ?? "#BFFCFF"); }

        /// <summary>强调色（条 / 节奏柱）</summary>
        public string HudAccentColor { get => _hudAccentColor; set => Set(ref _hudAccentColor, value ?? "#9CF4FF"); }

        /// <summary>字体名；空 = 跟随界面字体</summary>
        public string HudFontFamilyName { get => _hudFontFamilyName; set => Set(ref _hudFontFamilyName, value ?? ""); }

        public double HudPerfFontSize
        {
            get => _hudPerfFontSize;
            set => Set(ref _hudPerfFontSize, Math.Max(8, Math.Min(30, value)));
        }

        public double HudLyricFontSize
        {
            get => _hudLyricFontSize;
            set => Set(ref _hudLyricFontSize, Math.Max(9, Math.Min(48, value)));
        }

        public double HudOffsetX
        {
            get => _hudOffsetX;
            set => Set(ref _hudOffsetX, Math.Max(-4000, Math.Min(4000, value)));
        }

        public double HudOffsetY
        {
            get => _hudOffsetY;
            set => Set(ref _hudOffsetY, Math.Max(-4000, Math.Min(4000, value)));
        }
        private double _hudTilt = 0;
        private double _hudPerfOffsetX = 0, _hudPerfOffsetY = 0;
        private double _hudMusicOffsetX = 0, _hudMusicOffsetY = 0;
        private double _hudLyricOffsetX = 0, _hudLyricOffsetY = 0;

        /// <summary>整体倾角（度），负值向左倒</summary>
        public double HudTilt
        {
            get => _hudTilt;
            set => Set(ref _hudTilt, Math.Max(-45, Math.Min(45, value)));
        }

        public double HudPerfOffsetX { get => _hudPerfOffsetX; set => Set(ref _hudPerfOffsetX, Math.Max(-2000, Math.Min(2000, value))); }
        public double HudPerfOffsetY { get => _hudPerfOffsetY; set => Set(ref _hudPerfOffsetY, Math.Max(-2000, Math.Min(2000, value))); }
        public double HudMusicOffsetX { get => _hudMusicOffsetX; set => Set(ref _hudMusicOffsetX, Math.Max(-2000, Math.Min(2000, value))); }
        public double HudMusicOffsetY { get => _hudMusicOffsetY; set => Set(ref _hudMusicOffsetY, Math.Max(-2000, Math.Min(2000, value))); }
        public double HudLyricOffsetX { get => _hudLyricOffsetX; set => Set(ref _hudLyricOffsetX, Math.Max(-2000, Math.Min(2000, value))); }
        public double HudLyricOffsetY { get => _hudLyricOffsetY; set => Set(ref _hudLyricOffsetY, Math.Max(-2000, Math.Min(2000, value))); }

        private double _hudPerfBarWidth = 96;

        private bool _hudShowCover = true;

        /// <summary>是否显示专辑封面</summary>
        public bool HudShowCover { get => _hudShowCover; set => Set(ref _hudShowCover, value); }
        private bool _hudShowTranslation = true;

        /// <summary>双语歌词时，在当前句下面显示译文</summary>
        public bool HudShowTranslation { get => _hudShowTranslation; set => Set(ref _hudShowTranslation, value); }
        private bool _hudShowDisk = false;
        private bool _hudShowGpu0 = false;
        private bool _hudShowGpu1 = false;
        private bool _hudShowLyrics = true;
        private bool _hudShowBeat = true;

        public bool HudShowDisk { get => _hudShowDisk; set => Set(ref _hudShowDisk, value); }
        private bool _hudShowCpu = true;
        private bool _hudShowRam = true;
        private bool _hudShowGpu = true;

        public bool HudShowCpu { get => _hudShowCpu; set => Set(ref _hudShowCpu, value); }
        public bool HudShowRam { get => _hudShowRam; set => Set(ref _hudShowRam, value); }
        public bool HudShowGpu { get => _hudShowGpu; set => Set(ref _hudShowGpu, value); }
        public bool HudShowGpu0 { get => _hudShowGpu0; set => Set(ref _hudShowGpu0, value); }
        public bool HudShowGpu1 { get => _hudShowGpu1; set => Set(ref _hudShowGpu1, value); }

        /// <summary>歌词块开关</summary>
        public bool HudShowLyrics { get => _hudShowLyrics; set => Set(ref _hudShowLyrics, value); }

        /// <summary>节奏条开关</summary>
        public bool HudShowBeat { get => _hudShowBeat; set => Set(ref _hudShowBeat, value); }
        private double _hudBeatOffsetX = 0, _hudBeatOffsetY = 0;
        private double _hudBeatBars = 32;
        private double _hudTiltPerf = 0, _hudTiltMusic = 0, _hudTiltLyric = 0, _hudTiltBeat = 0;

        public double HudBeatOffsetX { get => _hudBeatOffsetX; set => Set(ref _hudBeatOffsetX, Math.Max(-2000, Math.Min(2000, value))); }
        public double HudBeatOffsetY { get => _hudBeatOffsetY; set => Set(ref _hudBeatOffsetY, Math.Max(-2000, Math.Min(2000, value))); }

        /// <summary>节奏条柱子数量</summary>
        public double HudBeatBars
        {
            get => _hudBeatBars;
            set => Set(ref _hudBeatBars, Math.Max(8, Math.Min(160, value)));
        }
        private double _hudBeatHeight = 26;

        /// <summary>节奏条高度（像素）</summary>
        public double HudBeatHeight
        {
            get => _hudBeatHeight;
            set => Set(ref _hudBeatHeight, Math.Max(8, Math.Min(90, value)));
        }

        /// <summary>各组件独立倾角（俯仰）</summary>
        public double HudTiltPerf { get => _hudTiltPerf; set => Set(ref _hudTiltPerf, Math.Max(-30, Math.Min(30, value))); }
        public double HudTiltMusic { get => _hudTiltMusic; set => Set(ref _hudTiltMusic, Math.Max(-30, Math.Min(30, value))); }
        public double HudTiltLyric { get => _hudTiltLyric; set => Set(ref _hudTiltLyric, Math.Max(-30, Math.Min(30, value))); }
        public double HudTiltBeat { get => _hudTiltBeat; set => Set(ref _hudTiltBeat, Math.Max(-30, Math.Min(30, value))); }
        private double _hudLyricWidth = 0;
        private double _hudPlayerWidth = 340;
        private double _hudBeatBarWidth = 4;

        /// <summary>歌词块最大宽度（dip）；0 = 跟着内容自适应</summary>
        public double HudLyricWidth
        {
            get => _hudLyricWidth;
            set => Set(ref _hudLyricWidth, Math.Max(0, Math.Min(2000, value)));
        }

        /// <summary>播放器信息区宽度（曲名/进度条）</summary>
        public double HudPlayerWidth
        {
            get => _hudPlayerWidth;
            set => Set(ref _hudPlayerWidth, Math.Max(160, Math.Min(1600, value)));
        }

        /// <summary>节奏柱粗细（1~12 px）</summary>
        public double HudBeatBarWidth
        {
            get => _hudBeatBarWidth;
            set => Set(ref _hudBeatBarWidth, Math.Max(1, Math.Min(14, value)));
        }

        private double _hudTitleFontSize = 14;
        private double _hudArtistFontSize = 11;
        private double _hudTimeFontSize = 10;

        /// <summary>曲名字号</summary>
        public double HudTitleFontSize
        {
            get => _hudTitleFontSize;
            set => Set(ref _hudTitleFontSize, Math.Max(8, Math.Min(40, value)));
        }

        /// <summary>歌手字号</summary>
        public double HudArtistFontSize
        {
            get => _hudArtistFontSize;
            set => Set(ref _hudArtistFontSize, Math.Max(8, Math.Min(40, value)));
        }

        /// <summary>进度时间 / 状态字号</summary>
        public double HudTimeFontSize
        {
            get => _hudTimeFontSize;
            set => Set(ref _hudTimeFontSize, Math.Max(8, Math.Min(30, value)));
        }        private bool _widgetCustomColors = false;
        private string _widgetTextColor = "#FFFFFF";
        private string _widgetSubColor = "#FFFFFF";

        /// <summary>时间组件是否用自定义颜色（关掉 = 跟随壁纸明暗自动）</summary>
        public bool WidgetCustomColors { get => _widgetCustomColors; set => Set(ref _widgetCustomColors, value); }

        /// <summary>时间主色 #RRGGBB</summary>
        public string WidgetTextColor { get => _widgetTextColor; set => Set(ref _widgetTextColor, value ?? "#FFFFFF"); }

        /// <summary>秒/日期/星期色</summary>
        public string WidgetSubColor { get => _widgetSubColor; set => Set(ref _widgetSubColor, value ?? "#FFFFFF"); }
        private string _lyricsFolder = "";

        /// <summary>外部播放器的歌词文件夹（放「歌名 - 歌手.lrc」）</summary>
        public string LyricsFolder
        {
            get => _lyricsFolder;
            set => Set(ref _lyricsFolder, value ?? "");
        }

        /// <summary>性能条单根的长度（px，随整体缩放）</summary>
        public double HudPerfBarWidth
        {
            get => _hudPerfBarWidth;
            set => Set(ref _hudPerfBarWidth, Math.Max(30, Math.Min(600, value)));
        }

        public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }

        /// <summary>自启方式偏好：run = HKCU Run（普通权限，默认）；task = 计划任务（最高权限，需要一次 UAC）</summary>
        public string AutoStartPreferred
        {
            get => _autoStartPreferred;
            set => Set(ref _autoStartPreferred, string.IsNullOrWhiteSpace(value) ? "run" : value.Trim().ToLowerInvariant());
        }

        // ───────────── 壁纸右上角的时间/天气组件 ─────────────

        /// <summary>是否显示壁纸组件</summary>
        public bool WidgetEnabled { get => _widgetEnabled; set => Set(ref _widgetEnabled, value); }

        /// <summary>组件信息层级：1 简洁 / 2 标准 / 3 丰富</summary>
        public int WidgetLod { get => _widgetLod; set => Set(ref _widgetLod, Math.Max(1, Math.Min(3, value))); }

        /// <summary>组件字体（空 = 跟随主界面字体）</summary>
        public string WidgetFontFamilyName
        {
            get => _widgetFontFamilyName;
            set => Set(ref _widgetFontFamilyName, value ?? "");
        }

        /// <summary>组件字号（时钟字号，其余按比例缩放）</summary>
        public double WidgetFontSize
        {
            get => _widgetFontSize;
            set => Set(ref _widgetFontSize, Math.Max(18, Math.Min(140, value)));
        }
        private int _widgetStyle = 0;

        /// <summary>时钟样式：0 横排 / 1 竖排 / 2 极简 / 3 大字</summary>
        public int WidgetStyle
        {
            get => _widgetStyle;
            set => Set(ref _widgetStyle, Math.Max(0, Math.Min(3, value)));
        }

        /// <summary>壁纸时间距工作区右侧的距离（DIP）</summary>
        public double WidgetMarginRight
        {
            get => _widgetMarginRight;
            set => Set(ref _widgetMarginRight, Math.Max(0, Math.Min(4000, value)));
        }

        /// <summary>壁纸时间距工作区顶部的距离（DIP）</summary>
        public double WidgetMarginTop
        {
            get => _widgetMarginTop;
            set => Set(ref _widgetMarginTop, Math.Max(0, Math.Min(4000, value)));
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
