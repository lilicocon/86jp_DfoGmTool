using GmPvfLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace DfoGmTool.ServerCore.GameWorld
{
    /// <summary>
    /// Process-wide shared PVF archive. All readers (index build, skill tables, item grant)
    /// must go through this so we never hold multiple full Script.pvf copies in RAM.
    /// After idle use the archive is unloaded so operation bursts do not pin body pages forever.
    /// </summary>
    internal static class PvfArchiveAccessor
    {
        // Drop mapped archive shortly after last use so GM ops don't keep ~body RSS.
        private static readonly TimeSpan IdleUnloadDelay = TimeSpan.FromSeconds(8);

        private static readonly object Sync = new object();
        private static PvfArchive _archive;
        private static string _archivePath;
        private static int _leaseCount;
        private static Timer _idleUnloadTimer;

        internal static void Configure(string pvfPath)
        {
            if (string.IsNullOrWhiteSpace(pvfPath))
                throw new ArgumentException("PVF path cannot be null or empty.", nameof(pvfPath));

            var fullPath = Path.GetFullPath(pvfPath);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("PVF 文件不存在。", fullPath);

            lock (Sync)
            {
                if (_archive != null
                    && string.Equals(_archivePath, fullPath, StringComparison.OrdinalIgnoreCase))
                    return;

                DisposeArchiveUnlocked();
                _archivePath = fullPath;
            }
        }

        /// <summary>
        /// Borrow the shared archive under the process lock. Do not Dispose the archive;
        /// the accessor owns its lifetime. Prefer short critical sections.
        /// </summary>
        internal static T WithArchive<T>(Func<PvfArchive, T> action)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            lock (Sync)
            {
                BeginLeaseUnlocked();
                try
                {
                    return action(GetArchiveUnlocked());
                }
                finally
                {
                    EndLeaseUnlocked();
                }
            }
        }

        internal static void WithArchive(Action<PvfArchive> action)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            lock (Sync)
            {
                BeginLeaseUnlocked();
                try
                {
                    action(GetArchiveUnlocked());
                }
                finally
                {
                    EndLeaseUnlocked();
                }
            }
        }

        internal static void ClearChunkCache()
        {
            lock (Sync)
            {
                _archive?.ClearChunkCache();
            }
        }

        /// <summary>
        /// Drop the in-memory PVF archive after bulk index build or idle timeout.
        /// Subsequent reads reopen lazily (mmap).
        /// </summary>
        internal static void Unload()
        {
            lock (Sync)
            {
                if (_leaseCount > 0)
                    return;
                DisposeArchiveUnlocked();
            }
        }

        public static string ReadText(string relativePath)
        {
            var normalizedPath = NormalizeRelativePath(relativePath);
            lock (Sync)
            {
                BeginLeaseUnlocked();
                try
                {
                    var archive = GetArchiveUnlocked();
                    var content = archive.GetFileContent(normalizedPath);
                    // Single-file reads should not retain decompressed chunks.
                    archive.ClearChunkCache();
                    if (string.IsNullOrEmpty(content))
                        throw new FileNotFoundException($"PVF 归档中不存在文件: {normalizedPath}", normalizedPath);
                    return content;
                }
                finally
                {
                    EndLeaseUnlocked();
                }
            }
        }

        // GM适配: 服务端原版经进程级 Lazy Archive 访问, 此处改经 GetArchive()+Sync 以兼容运行时切换 PVF
        public static IReadOnlyList<string> ReadAllText(string relativePath)
        {
            var normalizedPath = NormalizeRelativePath(relativePath);
            var result = new List<string>();
            lock (Sync)
            {
                BeginLeaseUnlocked();
                try
                {
                    var archive = GetArchiveUnlocked();
                    foreach (var file in archive.Files)
                    {
                        if (!string.Equals(file.Path, normalizedPath, StringComparison.OrdinalIgnoreCase))
                            continue;
                        var content = archive.GetFileContent(file);
                        if (!string.IsNullOrEmpty(content))
                            result.Add(content);
                    }
                    archive.ClearChunkCache();
                }
                finally
                {
                    EndLeaseUnlocked();
                }
            }
            return result;
        }

        public static IReadOnlyList<string> FindPathsContaining(string fragment)
        {
            if (string.IsNullOrWhiteSpace(fragment))
                return Array.Empty<string>();
            lock (Sync)
            {
                BeginLeaseUnlocked();
                try
                {
                    return GetArchiveUnlocked().Files
                        .Select(file => string.IsNullOrEmpty(file.Path)
                            ? file.Name
                            : string.IsNullOrEmpty(file.Name)
                                ? file.Path
                                : file.Path.TrimEnd('/', '\\') + "/" + file.Name)
                        .Where(path => !string.IsNullOrEmpty(path)
                            && path.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                finally
                {
                    EndLeaseUnlocked();
                }
            }
        }

        private static void BeginLeaseUnlocked()
        {
            _leaseCount++;
            CancelIdleTimerUnlocked();
        }

        private static void EndLeaseUnlocked()
        {
            if (_leaseCount > 0)
                _leaseCount--;
            if (_leaseCount == 0)
                ScheduleIdleUnloadUnlocked();
        }

        private static void ScheduleIdleUnloadUnlocked()
        {
            CancelIdleTimerUnlocked();
            _idleUnloadTimer = new Timer(_ =>
            {
                lock (Sync)
                {
                    if (_leaseCount == 0)
                        DisposeArchiveUnlocked();
                }
            }, null, IdleUnloadDelay, Timeout.InfiniteTimeSpan);
        }

        private static void CancelIdleTimerUnlocked()
        {
            if (_idleUnloadTimer == null)
                return;
            try { _idleUnloadTimer.Dispose(); } catch { /* ignore */ }
            _idleUnloadTimer = null;
        }

        private static void DisposeArchiveUnlocked()
        {
            CancelIdleTimerUnlocked();
            if (_archive == null)
                return;
            try { _archive.ClearChunkCache(); } catch { /* ignore */ }
            try { _archive.Dispose(); } catch { /* ignore */ }
            _archive = null;
            // Mapped open still allocates large LOH tables; aggressive GC after unload
            // lets op-time peaks return closer to warm baseline.
            try
            {
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            }
            catch { /* ignore */ }
        }

        private static PvfArchive GetArchiveUnlocked()
        {
            var path = _archivePath ?? GameWorldConfig.PvfArchivePath;
            if (_archive != null && string.Equals(_archivePath, path, StringComparison.OrdinalIgnoreCase))
                return _archive;

            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("PVF 路径未配置。");

            DisposeArchiveUnlocked();
            // mmap open: body stays file-backed; metadata only on managed heap.
            _archive = PvfArchive.OpenMapped(path);
            _archivePath = path;
            return _archive;
        }

        private static string NormalizeRelativePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new ArgumentException("relativePath cannot be null or empty.", nameof(relativePath));

            return relativePath.Replace('\\', '/').TrimStart('.', '/');
        }
    }
}
