using System;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using DesktopDock.Models;

namespace DesktopDock.Services
{
    public static class SettingsStore
    {
        private static string _dataDir;

        /// <summary>配置目录：优先 %APPDATA%\DesktopDock，取不到就落在程序目录下</summary>
        public static string DataDir
        {
            get
            {
                if (_dataDir == null) _dataDir = ResolveDataDir();
                return _dataDir;
            }
        }

        private static string ResolveDataDir()
        {
            string appData = null;
            try
            {
                appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }
            catch
            {
                appData = null;
            }

            if (!string.IsNullOrWhiteSpace(appData))
            {
                string primary = Path.Combine(appData, "DesktopDock");
                if (IsWritable(primary)) return primary;
            }

            // 配置目录不可写时退回程序目录（便携模式）
            try
            {
                string portable = Path.Combine(AppContext.BaseDirectory, "data");
                if (IsWritable(portable)) return portable;
                return portable;
            }
            catch
            {
                return ".";
            }
        }

        private static bool IsWritable(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".write-probe");
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string SettingsPath => Path.Combine(DataDir, "settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private static DispatcherTimer _debounce;
        private static AppSettings _pending;

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);

                    bool hasVersion;
                    using (var probe = JsonDocument.Parse(json))
                    {
                        hasVersion = probe.RootElement.ValueKind == JsonValueKind.Object
                                     && probe.RootElement.TryGetProperty("SettingsVersion", out _);
                    }

                    var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                    if (loaded != null)
                    {
                        // 老配置（没有版本号）→ 启用毛玻璃并给出适合的蒙层浓度
                        if (!hasVersion)
                        {
                            loaded.GlassEffect = true;
                            loaded.PanelOpacity = 0.82;
                            loaded.SettingsVersion = 2;
                        }

                        Migrate(loaded);
                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("读取设置失败: " + ex.Message);
            }

            return new AppSettings();
        }

        /// <summary>老版本配置迁移：v2 起默认开启毛玻璃，蒙层更适合 0.82</summary>
        private static void Migrate(AppSettings settings)
        {
            if (settings.SettingsVersion < 2)
            {
                settings.GlassEffect = true;
                settings.PanelOpacity = 0.82;
                settings.SettingsVersion = 2;
            }
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
                return;
            }
            catch (Exception ex)
            {
                Log("保存设置失败: " + ex.Message);
            }

            // 主目录不可写就退回程序目录，保证设置不丢
            try
            {
                _dataDir = Path.Combine(AppContext.BaseDirectory, "data");
                Directory.CreateDirectory(_dataDir);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
            }
            catch (Exception ex)
            {
                Log("备用目录保存也失败: " + ex.Message);
            }
        }

        /// <summary>拖动滑块时高频调用，合并成一次落盘</summary>
        public static void SaveDebounced(AppSettings settings)
        {
            _pending = settings;
            if (_debounce == null)
            {
                _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _debounce.Tick += (s, e) =>
                {
                    _debounce.Stop();
                    if (_pending != null) Save(_pending);
                };
            }

            _debounce.Stop();
            _debounce.Start();
        }

        /// <summary>同时写配置目录与程序目录，任一处可写就能留下痕迹</summary>
        public static void Log(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine;
            string failure = null;
            bool written = false;

            try
            {
                Directory.CreateDirectory(DataDir);
                File.AppendAllText(Path.Combine(DataDir, "log.txt"), line);
                written = true;
            }
            catch (Exception ex)
            {
                failure = ex.GetType().Name + ": " + ex.Message;
            }

            try
            {
                string dir = AppContext.BaseDirectory;
                if (!string.IsNullOrEmpty(dir))
                {
                    File.AppendAllText(Path.Combine(dir, "debug.log"),
                        line + (failure != null ? "    [配置目录写入失败: " + failure + "]" + Environment.NewLine : ""));
                    written = true;
                }
            }
            catch
            {
                // 忽略
            }

            if (written) return;

            // Low IL 时上面两处都是 Medium 对象（只允许往下写），LocalLow 是唯一还能写的地方。
            // 没有这一步，"低权限模式"下连日志都不会留下，没法排查。
            try
            {
                string low = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData", "LocalLow", "DesktopDock");
                Directory.CreateDirectory(low);
                File.AppendAllText(Path.Combine(low, "log.txt"), line + "    [低权限回退日志]" + Environment.NewLine);
            }
            catch
            {
                // 真的无处可写就算了
            }
        }
    }
}
