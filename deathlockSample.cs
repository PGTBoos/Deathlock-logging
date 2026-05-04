using System;
using System.Threading;
using System.Threading.Tasks;
 
/// <summary>
/// Sample: a classic A→B / B→A deadlock, instrumented with LockHelper.
///
/// Run this program. Open another terminal and run:
///   Linux/macOS: watch -n 1 'ls -la locks-active/'
///   Windows:     while (1) { cls; dir locks-active; sleep 1 }
///
/// Within a second or two of the deadlock occurring, you'll see two .lock files
/// stuck in the directory and not going away. Their filenames tell you exactly
/// which threads and which locks are involved. Open them — the contents show
/// timestamps and (on the second one to deadlock) which thread blocked it.
///
/// To verify the no-op mode: set LockHelper.EnableLockLogging = false at the top
/// of Main and re-run. You'll still get the deadlock (this code IS broken on
/// purpose) but no marker files or history log — proving the wrapper is truly
/// transparent when disabled.
/// </summary>
public class DeadlockSample
{
    private static readonly object _lockA = new object();
    private static readonly object _lockB = new object();
 
    public static void Main()
    {
        // Optional: customize where files go. Defaults are fine.
        // LockHelper.LockDir = "/tmp/my-app-locks";
        // LockHelper.MarkerFileMinWaitMs = 50;
 
        Console.WriteLine("Starting deadlock demo.");
        Console.WriteLine($"Watch the '{LockHelper.LockDir}' directory.");
        Console.WriteLine($"History log: {LockHelper.HistoryLog}");
        Console.WriteLine();
 
        // First, do some normal lock work to show the healthy case.
        // These are fast and uncontended, so NO marker files will appear
        // (MarkerFileMinWaitMs filter skips them) — the directory stays clean.
        Console.WriteLine("Phase 1: normal lock activity (directory should stay empty)...");
        for (int i = 0; i < 100; i++)
        {
            _lockA.WithLockDebug("Counter.Increment", () =>
            {
                Thread.Sleep(1);
            });
        }
        Console.WriteLine("Done. Directory should still be empty.");
        Console.WriteLine();
 
        // Now demonstrate a long-held lock — this WILL show up as a marker file
        // because the slow-hold watchdog kicks in after MarkerFileMinHeldMs.
        Console.WriteLine("Phase 2: a slow critical section (marker file will appear briefly)...");
        _lockA.WithLockDebug("SlowOperation", () =>
        {
            Thread.Sleep(500);
        });
        Console.WriteLine("Done. Marker file deleted on release.");
        Console.WriteLine();
 
        // Now the actual deadlock.
        Console.WriteLine("Phase 3: provoking a deadlock. Watch the directory now!");
        Console.WriteLine("Two .lock files will appear and STAY THERE — that's the bug.");
        Console.WriteLine("Press Ctrl+C to exit when you've seen enough.");
        Console.WriteLine();
 
        var t1 = new Thread(Worker1) { Name = "Worker-AB", IsBackground = true };
        var t2 = new Thread(Worker2) { Name = "Worker-BA", IsBackground = true };
        t1.Start();
        t2.Start();
 
        // Keep the main thread alive so the deadlock is observable.
        // In a real app you'd hit Ctrl+C; here we just block forever.
        Thread.Sleep(Timeout.Infinite);
    }
 
    // Acquires A, then B
    private static void Worker1()
    {
        _lockA.WithLockDebug("ResourceA", () =>
        {
            Thread.Sleep(100); // give Worker2 time to grab B
            _lockB.WithLockDebug("ResourceB", () =>
            {
                // Never reached.
                Console.WriteLine("Worker1 got both locks (impossible in this demo).");
            });
        });
    }
 
    // Acquires B, then A — opposite order, classic deadlock
    private static void Worker2()
    {
        _lockB.WithLockDebug("ResourceB", () =>
        {
            Thread.Sleep(100); // give Worker1 time to grab A
            _lockA.WithLockDebug("ResourceA", () =>
            {
                // Never reached.
                Console.WriteLine("Worker2 got both locks (impossible in this demo).");
            });
        });
    }
}
