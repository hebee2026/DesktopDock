using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using DesktopDock.Models;

namespace DesktopDock.Services
{
    /// <summary>扫描「用户桌面 + 公共桌面」里可启动的项目，并监听变化。</summary>
    public sealed class ShortcutScanner : IDisposable
    {
        private static readonly HashSet<string> LaunchableExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".lnk", ".url", ".appref-ms", ".exe", ".bat", ".cmd", ".ps1", ".msc"
            };

        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private DispatcherTimer _debounce;

        public event Action Changed;

        public static IReadOnlyList<string> Folders => new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        };

        public List<ShortcutItem> Scan()
        {
            var result = new List<ShortcutItem>();
            var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

            foreach (var dir in Folders)
            {
                if (!Directory.Exists(dir)) continue;

                IEnumerable<FileSystemInfo> entries;
                try
                {
                    entries = new DirectoryInfo(dir).EnumerateFileSystemInfos();
                }
                catch
                {
                    continue;
                }

                foreach (var info in entries)
                {
                    try
                    {
                        if (info.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                        if (info.Attributes.HasFlag(FileAttributes.Hidden)) continue;
                        if (info.Attributes.HasFlag(FileAttributes.System)) continue;

                        bool isFolder = info is DirectoryInfo;
                        string name = info.Name;

                        if (!isFolder)
                        {
                            string ext = Path.GetExtension(name);
                            if (!LaunchableExtensions.Contains(ext)) continue;
                            if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                                && !ext.Equals(".url", StringComparison.OrdinalIgnoreCase)
                                && !ext.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase))
                            {
                                // .exe/.bat 之类保留扩展名，便于区分
                            }
                            else
                            {
                                name = Path.GetFileNameWithoutExtension(name);
                            }
                        }

                        if (name.StartsWith("~$", StringComparison.Ordinal)) continue;
                        if (!seen.Add(name)) continue;

                        result.Add(new ShortcutItem
                        {
                            DisplayName = name,
                            Path = info.FullName,
                            IsFolder = isFolder,
                            ModifiedUtc = info.LastWriteTimeUtc
                        });
                    }
                    catch
                    {
                        // 单个项目读不到不影响整体
                    }
                }
            }

            return result
                .OrderByDescending(i => i.IsFolder)
                .ThenBy(i => i.DisplayName, StringComparer.CurrentCulture)
                .ToList();
        }

        public void StartWatching()
        {
            foreach (var dir in Folders)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    var watcher = new FileSystemWatcher(dir)
                    {
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                                       | NotifyFilters.LastWrite | NotifyFilters.Attributes,
                        EnableRaisingEvents = true
                    };

                    watcher.Created += OnFsEvent;
                    watcher.Deleted += OnFsEvent;
                    watcher.Renamed += OnFsEvent;
                    watcher.Changed += OnFsEvent;
                    _watchers.Add(watcher);
                }
                catch (Exception ex)
                {
                    SettingsStore.Log("监听桌面失败(" + dir + "): " + ex.Message);
                }
            }
        }

        private void OnFsEvent(object sender, FileSystemEventArgs e)
        {
            if (e.FullPath.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)) return;
            ScheduleReload();
        }

        /// <summary>多次文件事件合并成一次重扫</summary>
        public void ScheduleReload()
        {
            if (_debounce == null)
            {
                _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                _debounce.Tick += (s, e) =>
                {
                    _debounce.Stop();
                    Changed?.Invoke();
                };
            }

            _debounce.Stop();
            _debounce.Start();
        }

        public void Dispose()
        {
            foreach (var w in _watchers)
            {
                try
                {
                    w.EnableRaisingEvents = false;
                    w.Dispose();
                }
                catch
                {
                    // 忽略
                }
            }

            _watchers.Clear();
        }
    }
}
