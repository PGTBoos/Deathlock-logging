using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

/// <summary>
/// Drop-in lock instrumentation for hunting deadlocks and contention.
///
/// Replace `lock (obj) { ... }` with `obj.WithLockDebug("Name", () => { ... });`
/// and watch the `locks-active/` directory while the app runs. Each currently-held
/// lock appears as a file there; it disappears on release. A file that won't go
/// away IS your deadlock — the filename tells you which thread, which lock, and when.
///
/// Set EnableLockLogging = false to make the wrapper a no-op (plain `lock` semantics).
/// </summary>
public static class LockHelper
{
    public static bool EnableLockLogging = true;

    /// <summary>Directory where one file is created per held lock. Defaults to ./locks-active</summary>
    public static string LockDir = "locks-active";

    /// <summary>Append-only log of completed lock events (only the interesting ones).</summary>
    public static string HistoryLog = "locks-history.log";

    /// <summary>Skip creating a marker file for locks acquired faster than this. Avoids disk I/O on hot, uncontended locks.</summary>
    public static int MarkerFileMinWaitMs = 10;

    /// <summary>Also create a marker file if the lock has been held longer than this, even if it was acquired quickly. Catches "fast acquire, slow critical section" cases.</summary>
    public static int MarkerFileMinHeldMs = 100;

    /// <summary>Threshold for writing a line to the history log.</summary>
    public static int LogIfWaitedMsAtLeast = 50;
    public static int LogIfHeldMsAtLeast = 100;

    private static readonly ConcurrentDictionary<object, HolderInfo> _holders =
        new ConcurrentDictionary<object, HolderInfo>(new ReferenceEqualityComparer());

    private static readonly BlockingCollection<string> _historyQueue =
        new BlockingCollection<string>(boundedCapacity: 100_000);
    private static readonly Thread _historyWriter;

    private sealed class HolderInfo
    {
        public int ThreadId;
        public string Name;
        public string ThreadName;
        public long AcquiredTicks;
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }

    static LockHelper()
    {
        try
        {
            Directory.CreateDirectory(LockDir);
            // Clean up stale marker files from a previous run (e.g. after a crash).
            // Otherwise leftover files would look like permanent deadlocks.
            foreach (var stale in Directory.EnumerateFiles(LockDir, "*.lock"))
            {
                try { File.Delete(stale); } catch { /* best-effort */ }
            }
        }
        catch { /* if we can't create the dir, marker files are skipped silently */ }

        _historyWriter = new Thread(HistoryWriterLoop)
        {
            IsBackground = true,
            Name = "LockHelper.HistoryWriter"
        };
        _historyWriter.Start();
    }

    /// <summary>Wraps `lock (obj) { action(); }` with diagnostics.</summary>
    public static void WithLockDebug(this object obj, string name, Action action)
    {
        if (obj == null) throw new ArgumentNullException(nameof(obj));
        if (action == null) throw new ArgumentNullException(nameof(action));
        if (obj is string || obj.GetType().IsValueType)
            throw new ArgumentException(
                "Lock target must be a non-string reference type.", nameof(obj));

        if (!EnableLockLogging)
        {
            lock (obj) { action(); }
            return;
        }

        var tid = Thread.CurrentThread.ManagedThreadId;
        var tname = Thread.CurrentThread.Name ?? "-";
        var enterTs = DateTime.UtcNow;
        var waitTimer = Stopwatch.StartNew();

        // Snapshot whoever currently holds it (racy but useful for diagnosis).
        _holders.TryGetValue(obj, out var blockedBy);

        lock (obj)
        {
            var waitedMs = waitTimer.ElapsedMilliseconds;
            var holdTimer = Stopwatch.StartNew();

            var info = new HolderInfo
            {
                ThreadId = tid,
                Name = name,
                ThreadName = tname,
                AcquiredTicks = Stopwatch.GetTimestamp()
            };
            _holders[obj] = info;

            // Decide whether to drop a marker file. For fast, uncontended locks we skip it
            // entirely so this wrapper is safe even on hot locks taken thousands of times/sec.
            string markerPath = null;
            bool shouldMark = waitedMs >= MarkerFileMinWaitMs || blockedBy != null;
            if (shouldMark)
            {
                markerPath = TryCreateMarker(name, tid, tname, enterTs, waitedMs, blockedBy);
            }

            // Watchdog: if the critical section runs long, create the marker even if we
            // didn't initially. We check after MarkerFileMinHeldMs of holding.
            // Simple approach: register a Timer that fires once.
            Timer slowHoldWatchdog = null;
            if (markerPath == null)
            {
                slowHoldWatchdog = new Timer(_ =>
                {
                    if (markerPath == null)
                        markerPath = TryCreateMarker(name, tid, tname, enterTs, waitedMs, blockedBy);
                }, null, MarkerFileMinHeldMs, Timeout.Infinite);
            }

            try
            {
                action();
            }
            finally
            {
                slowHoldWatchdog?.Dispose();
                _holders.TryRemove(obj, out _);

                // Delete the marker if we created one. THIS is the "remove on release".
                if (markerPath != null)
                {
                    try { File.Delete(markerPath); } catch { /* ignore */ }
                }

                var heldMs = holdTimer.ElapsedMilliseconds;

                bool interesting =
                    waitedMs >= LogIfWaitedMsAtLeast ||
                    heldMs   >= LogIfHeldMsAtLeast   ||
                    blockedBy != null;

                if (interesting)
                {
                    var sb = new StringBuilder(256);
                    sb.Append(enterTs.ToString("O"))
                      .Append(' ').Append(name)
                      .Append(" T").Append(tid).Append('(').Append(tname).Append(')')
                      .Append(" waited=").Append(waitedMs).Append("ms")
                      .Append(" held=").Append(heldMs).Append("ms");
                    if (blockedBy != null)
                    {
                        sb.Append(" blockedBy=T").Append(blockedBy.ThreadId)
                          .Append('/').Append(blockedBy.Name);
                    }
                    _historyQueue.TryAdd(sb.ToString());
                }
            }
        }
    }

    /// <summary>Func-returning overload, for `return myLock.WithLockDebug("Name", () => ...);`</summary>
    public static T WithLockDebug<T>(this object obj, string name, Func<T> func)
    {
        if (func == null) throw new ArgumentNullException(nameof(func));
        T result = default;
        obj.WithLockDebug(name, () => { result = func(); });
        return result;
    }

    private static string TryCreateMarker(string name, int tid, string tname,
        DateTime enterTs, long waitedMs, HolderInfo blockedBy)
    {
        try
        {
            var safeName = SanitizeForFilename(name);
            var filename = $"{enterTs:yyyyMMddTHHmmss.fff}_T{tid:D4}_{safeName}.lock";
            var path = Path.Combine(LockDir, filename);

            var contents = new StringBuilder();
            contents.Append("acquired=").Append(enterTs.ToString("O")).Append('\n');
            contents.Append("thread=").Append(tid).Append(" (").Append(tname).Append(")\n");
            contents.Append("name=").Append(name).Append('\n');
            contents.Append("waitedMs=").Append(waitedMs).Append('\n');
            if (blockedBy != null)
            {
                contents.Append("blockedBy=T").Append(blockedBy.ThreadId)
                        .Append(" holding=").Append(blockedBy.Name).Append('\n');
            }

            File.WriteAllText(path, contents.ToString());
            return path;
        }
        catch
        {
            return null; // never let logging break the lock
        }
    }

    private static string SanitizeForFilename(string s)
    {
        if (string.IsNullOrEmpty(s)) return "_";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
        return new string(chars);
    }

    private static void HistoryWriterLoop()
    {
        try
        {
            using var fs = new FileStream(HistoryLog, FileMode.Append,
                FileAccess.Write, FileShare.Read, 4096, FileOptions.SequentialScan);
            using var sw = new StreamWriter(fs) { AutoFlush = false };
            foreach (var line in _historyQueue.GetConsumingEnumerable())
            {
                sw.WriteLine(line);
                if (_historyQueue.Count == 0) sw.Flush();
            }
            sw.Flush();
        }
        catch { /* writer thread should never crash the app */ }
    }

    /// <summary>Call before process exit if you want guaranteed flush of the history log.</summary>
    public static void Shutdown(TimeSpan? timeout = null)
    {
        _historyQueue.CompleteAdding();
        _historyWriter.Join(timeout ?? TimeSpan.FromSeconds(2));
    }
}
