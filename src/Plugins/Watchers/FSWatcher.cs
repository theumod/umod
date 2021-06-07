using Oxide.Core.Libraries;
using System;
using System.Collections.Generic;
using System.IO;
#if !NETSTANDARD
using System.Security.Permissions;
#endif
using System.Text.RegularExpressions;

namespace Oxide.Core.Plugins.Watchers
{
    /// <summary>
    /// Represents a file system watcher
    /// </summary>
    public sealed class FSWatcher : PluginChangeWatcher
    {
        private class QueuedChange
        {
            internal WatcherChangeTypes type;
            internal Timer.TimerInstance timer;
        }

        // The filesystem watcher
        private FileSystemWatcher watcher;

        // The plugin list
        private ICollection<string> watchedPlugins;

        // Changes are buffered briefly to avoid duplicate events
        private Dictionary<string, QueuedChange> changeQueue;

        private Timer timers;

        private Dictionary<string, FileSystemWatcher> m_symlinkWatchers = new Dictionary<string, FileSystemWatcher>();

        /// <summary>
        /// Initializes a new instance of the FSWatcher class
        /// </summary>
        /// <param name="directory"></param>
        /// <param name="filter"></param>
        public FSWatcher(string directory, string filter)
        {
            watchedPlugins = new HashSet<string>();
            changeQueue = new Dictionary<string, QueuedChange>();
            timers = Interface.Oxide.GetLibrary<Timer>();

            if (Interface.Oxide.Config.Options.PluginWatchers)
            {
                LoadWatcher(directory, filter);

                // watch symlinked files
                if (Environment.OSVersion.Platform == PlatformID.Unix)
                {
                    var files = new DirectoryInfo(directory).GetFiles(filter);
                    foreach (var fileInfo in files)
                    {
                        if(IsFileSymlink(fileInfo.FullName))
                        {
                            LoadWatcherSymlink(fileInfo.FullName);
                        }
                    }
                }
            }
            else
            {
                Interface.Oxide.LogWarning("Automatic plugin reloading and unloading has been disabled");
            }
        }

        bool IsFileSymlink(string path)
        {
            var fileAttributes = File.GetAttributes(path);
            var isSymlink = (fileAttributes & FileAttributes.ReparsePoint) > 0;
            return isSymlink;
        }

        // TODO: replace with syscall. But this adds the least code and does not require a Linux and Windows Oxide.Core to be seperated
        private string GetSymlinkPath(string path)
        {
            var p = new System.Diagnostics.Process();
            p.StartInfo.FileName = "readlink";
            p.StartInfo.Arguments = string.Format("-f {0}", path);
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardOutput = true;
            p.Start();
            return p.StandardOutput.ReadToEnd().Trim();
        }

        [PermissionSet(SecurityAction.Demand, Name = "FullTrust")]
        private void LoadWatcherSymlink(string symlinkPath)
        {
            var realPath = GetSymlinkPath(symlinkPath);
            var realDirName = Path.GetDirectoryName(realPath);
            var realFileName = Path.GetFileName(realPath);

            void symlinkTarget_Changed(object sender, FileSystemEventArgs e)
            {
                watcher_Changed(sender, e);
            }

            var w = new FileSystemWatcher(realDirName, realFileName);
            m_symlinkWatchers[symlinkPath] = w;
            w.Changed += symlinkTarget_Changed;
            w.Created += symlinkTarget_Changed;
            w.Deleted += symlinkTarget_Changed;
            w.Error += watcher_Error;
            w.NotifyFilter = NotifyFilters.LastWrite;
            w.IncludeSubdirectories = false;
            w.EnableRaisingEvents = true;
        }

        /// <summary>
        /// Loads the filesystem watcher
        /// </summary>
        /// <param name="directory"></param>
        /// <param name="filter"></param>
#if !NETSTANDARD
        [PermissionSet(SecurityAction.Demand, Name = "FullTrust")]
#endif
        private void LoadWatcher(string directory, string filter)
        {
            // Create the watcher
            watcher = new FileSystemWatcher(directory, filter);
            watcher.Changed += watcher_Changed;
            watcher.Created += watcher_Changed;
            watcher.Deleted += watcher_Changed;
            watcher.Error += watcher_Error;
            watcher.NotifyFilter = NotifyFilters.LastWrite;
            watcher.IncludeSubdirectories = true;
            watcher.EnableRaisingEvents = true;
            GC.KeepAlive(watcher);
        }

        /// <summary>
        /// Adds a filename-plugin mapping to this watcher
        /// </summary>
        /// <param name="name"></param>
        public void AddMapping(string name) => watchedPlugins.Add(name);

        /// <summary>
        /// Removes the specified mapping from this watcher
        /// </summary>
        /// <param name="name"></param>
        public void RemoveMapping(string name) => watchedPlugins.Remove(name);

        /// <summary>
        /// Called when the watcher has registered a filesystem change
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void watcher_Changed(object sender, FileSystemEventArgs e)
        {
            FileSystemWatcher watcher = (FileSystemWatcher)sender;
            int length = e.FullPath.Length - watcher.Path.Length - Path.GetExtension(e.Name).Length - 1;
            string sub_path = e.FullPath.Substring(watcher.Path.Length + 1, length);
            if (!changeQueue.TryGetValue(sub_path, out QueuedChange change))
            {
                change = new QueuedChange();
                changeQueue[sub_path] = change;
            }
            change.timer?.Destroy();
            change.timer = null;
            switch (e.ChangeType)
            {
                case WatcherChangeTypes.Changed:
                    if (change.type != WatcherChangeTypes.Created)
                    {
                        change.type = WatcherChangeTypes.Changed;
                    }

                    break;

                case WatcherChangeTypes.Created:
                    if (change.type == WatcherChangeTypes.Deleted)
                    {
                        change.type = WatcherChangeTypes.Changed;
                    }
                    else
                    {
                        change.type = WatcherChangeTypes.Created;
                    }

                    break;

                case WatcherChangeTypes.Deleted:
                    if (change.type == WatcherChangeTypes.Created)
                    {
                        changeQueue.Remove(sub_path);
                        return;
                    }
                    change.type = WatcherChangeTypes.Deleted;

                    break;
            }
            Interface.Oxide.NextTick(() =>
            {
                if (Environment.OSVersion.Platform == PlatformID.Unix)
                {
                    switch (e.ChangeType)
                    {
                        case WatcherChangeTypes.Created:
                            if (IsFileSymlink(e.FullPath))
                            {
                                LoadWatcherSymlink(e.FullPath);
                            }
                            break;
                        case WatcherChangeTypes.Deleted:
                            if (m_symlinkWatchers.ContainsKey(e.FullPath))
                            {
                                m_symlinkWatchers.TryGetValue(e.FullPath, out FileSystemWatcher symlinkWatcher);
                                symlinkWatcher?.Dispose();
                                m_symlinkWatchers.Remove(e.FullPath);
                            }
                            break;
                    }
                }

                change.timer?.Destroy();
                change.timer = timers.Once(.2f, () =>
                {
                    change.timer = null;
                    changeQueue.Remove(sub_path);
                    if (Regex.Match(sub_path, @"include\\", RegexOptions.IgnoreCase).Success)
                    {
                        if (change.type == WatcherChangeTypes.Created || change.type == WatcherChangeTypes.Changed)
                        {
                            FirePluginSourceChanged(sub_path);
                        }

                        return;
                    }
                    switch (change.type)
                    {
                        case WatcherChangeTypes.Changed:
                            if (watchedPlugins.Contains(sub_path))
                            {
                                FirePluginSourceChanged(sub_path);
                            }
                            else
                            {
                                FirePluginAdded(sub_path);
                            }

                            break;

                        case WatcherChangeTypes.Created:
                            FirePluginAdded(sub_path);
                            break;

                        case WatcherChangeTypes.Deleted:
                            if (watchedPlugins.Contains(sub_path))
                            {
                                FirePluginRemoved(sub_path);
                            }

                            break;
                    }
                });
            });
        }

        private void watcher_Error(object sender, ErrorEventArgs e)
        {
            Interface.Oxide.NextTick(() =>
            {
                Interface.Oxide.LogError("FSWatcher error: {0}", e.GetException());
                RemoteLogger.Exception("FSWatcher error", e.GetException());
            });
        }
    }
}
