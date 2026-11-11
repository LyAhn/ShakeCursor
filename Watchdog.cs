using System.Diagnostics;

namespace ShakeCursor;

/// <summary>
/// Companion process that guarantees the real system cursors come back even
/// when the main process is hard-killed (Task Manager, crash, power-off of the
/// session). The main process spawns this with <c>--watchdog &lt;pid&gt; &lt;event&gt;</c>;
/// it waits for that process to die and restores the cursors unless the main
/// process signalled a clean exit first.
/// </summary>
internal static class Watchdog
{
    public const string MutexName = @"Local\ShakeCursor.SingleInstance";

    public static string CleanExitEventName(int pid) => $@"Local\ShakeCursor.CleanExit.{pid}";

    /// <summary>Starts the watchdog for the current process. Returns the clean-exit signal.</summary>
    public static EventWaitHandle? Spawn()
    {
        try
        {
            int pid = Environment.ProcessId;
            var cleanExit = new EventWaitHandle(false, EventResetMode.ManualReset, CleanExitEventName(pid));

            string? exe = Environment.ProcessPath;
            if (exe is null)
            {
                return cleanExit;
            }

            Process.Start(new ProcessStartInfo(exe)
            {
                ArgumentList = { "--watchdog", pid.ToString() },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return cleanExit;
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Watchdog spawn failed: " + ex.Message);
            return null;
        }
    }

    public static int Run(int parentPid)
    {
        try
        {
            using var cleanExit = new EventWaitHandle(false, EventResetMode.ManualReset, CleanExitEventName(parentPid));
            using var parent = Process.GetProcessById(parentPid);

            while (!parent.WaitForExit(500))
            {
                if (cleanExit.WaitOne(0))
                {
                    return 0;
                }
            }

            if (cleanExit.WaitOne(0))
            {
                return 0;
            }

            // A fresh instance may already own the cursor; don't pull it out
            // from under it.
            if (Mutex.TryOpenExisting(MutexName, out Mutex? existing))
            {
                existing.Dispose();
                return 0;
            }

            NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETCURSORS, 0, IntPtr.Zero, 0);
        }
        catch (ArgumentException)
        {
            // Parent already gone before we attached: restore.
            NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETCURSORS, 0, IntPtr.Zero, 0);
        }
        return 0;
    }
}
