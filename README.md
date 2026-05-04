# LockHelper

A drop-in `lock` instrumentation tool for C# / .NET. Designed for one specific job:   

> **finding deadlocks and lock contention in a running application without adding a heavyweight profiler.**

The trick: each currently-held lock is represented as a tiny file in a directory.  
The file is created when the lock is acquired and deleted when it's released.  
So at any moment, the directory is a live picture of which locks are held.  
**A file that won't go away is your deadlock** the filename tells you which thread, which lock, and when it was acquired.

## How it works

Replace this:

```csharp
lock (myLock)
{
    // critical section
}
```

With this:

```csharp
myLock.WithLockDebug("Cache.Add", () =>
{
    // critical section
});
```

While the app runs, watch the `locks-active/` directory.  
In a healthy system it stays empty (or has files that flicker in and out).  
When something deadlocks, the offending files freeze in place.

For value-returning critical sections:

```csharp
return myLock.WithLockDebug("Cache.Get", () => _dict.TryGetValue(k, out var v) ? v : null);
```

## What you watch

**During normal operation:** the `locks-active/` directory should be near-empty.  
Fast uncontended locks don't create marker files at all (controlled by `MarkerFileMinWaitMs`, default 10ms) so there's no disk I/O hit on hot locks.  

**When a lock is contended or slow:** a file appears, named like `20260504T143022.123_T0007_Cache.Add.lock`.  
That's timestamp + thread ID + lock name. The file's contents add detail — who acquired it, how long they waited, who was blocking them.

**When something deadlocks:** the file stays. Forever. 
You'll see it sitting there minutes later, hours later. Open it, read the contents, you have your culprit.

A useful watch command while debugging:

```bash
# Linux/macOS — show locks held longer than 30 seconds, refresh every 2s
watch -n 2 'find locks-active -name "*.lock" -mmin +0.5 -exec cat {} \;'
```

```powershell
# Windows PowerShell — same idea
while ($true) {
    Clear-Host
    Get-ChildItem locks-active -Filter *.lock |
        Where-Object { $_.LastWriteTime -lt (Get-Date).AddSeconds(-30) } |
        ForEach-Object { Get-Content $_.FullName; Write-Host '---' }
    Start-Sleep -Seconds 2
}
```

If that command ever prints anything, you've caught a stuck lock as it's happening.

## Secondary output: history log

In addition to the live marker files, completed lock events are written to `locks-history.log` 
> but only the *interesting* ones (waits over `LogIfWaitedMsAtLeast`, holds over `LogIfHeldMsAtLeast`, or contended acquisitions).
> For multi-day runs this stays small and grep-friendly. Sort by `waited=` descending and the worst offenders pop out:

```
2026-05-04T14:30:22.123Z Cache.Add T7(worker-1) waited=4231ms held=2ms blockedBy=T12/Cache.Flush
```

## Configuration

All tunable via static fields on `LockHelper`:

| Field | Default | What it does |
|---|---|---|
| `EnableLockLogging` | `true` | Set to `false` for a true no-op (plain `lock` semantics, zero overhead beyond a method call) |
| `LockDir` | `"locks-active"` | Where marker files go |
| `HistoryLog` | `"locks-history.log"` | Where the history log goes |
| `MarkerFileMinWaitMs` | `10` | Skip marker files for locks acquired faster than this — keeps hot locks from flooding the disk |
| `MarkerFileMinHeldMs` | `100` | Even if acquire was fast, create a marker if the critical section runs longer than this |
| `LogIfWaitedMsAtLeast` | `50` | Threshold for writing to history log |
| `LogIfHeldMsAtLeast` | `100` | Same |

## Design notes

- **The history log writer is a single background thread**, fed by a bounded `BlockingCollection`.  
  Producers (your lock-wrapped code) call `TryAdd` which never blocks.  
  if the writer can't keep up, log lines are dropped rather than stalling your critical section.  
  Here the whole point is to not let diagnostics make the problem worse.
- **Lock target validation:** value types and strings are rejected, since `lock(string)` is a well-known footgun.
- **Holder tracking** uses reference identity (`ReferenceEqualityComparer`), since `lock` itself uses reference identity.
  This matters if you have lock objects with custom `Equals`.
- **Stale file cleanup at startup:** if the previous process crashed mid-lock, leftover `.lock` files from that  
  run are deleted in the static constructor so they don't look like permanent deadlocks.
- **No external dependencies.** Single source file, drops into any .NET project.

## Limitations

- This finds deadlocks and contention. It does not find race conditions, data corruption, or logic bugs.  
- Marker files use synchronous file I/O. On extremely hot locks (tens of thousands of acquisitions per second per lock)  
  the threshold filters keep this from being a problem, but if you tune the thresholds to zero you'll measure your disk, not your locks.  
- The "currently blocked by" field on a marker file is captured when the wait *starts*, not continuously.  
  It tells you who held the lock at the moment your thread arrived; the actual blocker may have changed by the time you read the file.
- Calls inside the wrapped action that use `return`, `break`, or `continue` referring to the *outer* method won't work — you're inside a lambda.  
  Use the `Func<T>` overload to return values.
- `await` inside the lambda won't compile (the delegate is synchronous).  
  The original `lock` block didn't allow `await` either, so this only matters if someone tries to "improve" the code mid-conversion.

## Sample

See `sample/DeadlockSample.cs` for a runnable demo that intentionally creates a classic A→B / B→A deadlock between two threads.  
Run it, watch the `locks-active/` directory, and see the bug appear as files that won't go away.

## License

MIT.
