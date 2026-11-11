using System.Diagnostics;
using static ShakeCursor.NativeMethods;

namespace ShakeCursor;

/// <summary>
/// High-frequency polling loop: samples the real pointer, feeds shake
/// detection, and tells the <see cref="CursorSwapper"/> what size the arrow
/// should currently be. Runs on a dedicated thread that sleeps 1 ms per tick
/// (~150-250 Hz depending on timer resolution).
/// </summary>
internal sealed class PointerWatcher
{
    private readonly CursorSwapper _swapper;
    private readonly ShakeDetector _shaker;
    private Thread? _thread;
    private volatile bool _running;

    /// <summary>True while an excluded app is in front: the arrow is left alone.</summary>
    public volatile bool Paused;

    // Telemetry (written to %TEMP%\ShakeCursor.log once per second while active).
    private double _telemetryTimer;
    private float _maxSpeed;
    private int _lastReversalCount;

    public PointerWatcher(CursorSwapper swapper, ShakeDetector shaker)
    {
        _swapper = swapper;
        _shaker = shaker;
    }

    public void Start()
    {
        _running = true;
        _thread = new Thread(Loop)
        {
            Name = "PointerWatcher",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(500);
    }

    private void Loop()
    {
        // Ask Windows for 1 ms timer resolution so Thread.Sleep(1) is ~1 ms.
        timeBeginPeriod(1);

        // Windows 11 ignores that request for a process with no visible window
        // (this one is tray-only), which drops the loop to ~64 Hz and makes both
        // shake detection and the growth animation coarse. Opt out of that
        // throttling (and of EcoQoS) so the request is honoured.
        var throttling = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
            StateMask = 0,
        };
        if (!SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref throttling,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>()))
        {
            Diagnostics.Log("SetProcessInformation failed: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        }

        long lastTimestamp = Stopwatch.GetTimestamp();
        long lastFrequency = Stopwatch.Frequency;

        try
        {
            while (_running)
            {
                var ci = new CURSORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<CURSORINFO>() };
                if (!GetCursorInfo(ref ci))
                {
                    // Secure desktop (UAC) or locked session: nothing to sample.
                    Thread.Sleep(10);
                    continue;
                }

                long now = Stopwatch.GetTimestamp();
                double dt = (now - lastTimestamp) / (double)lastFrequency;
                lastTimestamp = now;
                dt = Math.Clamp(dt, 0.0001, 0.1); // guard against resume/hiccups

                var pt = new System.Drawing.Point(ci.pt.X, ci.pt.Y);

                // ALWAYS feed the detector with the real pointer position, even
                // while the I-beam/hand cursors are showing.
                float progress = _shaker.Update(pt, dt);
                if (!Paused)
                {
                    _swapper.SetProgress(progress);
                }

                _telemetryTimer += dt;
                if (_telemetryTimer >= 1.0)
                {
                    _telemetryTimer = 0;
                    Diagnostics.Log(
                        $"telemetry: maxSpeed={Interlocked.Exchange(ref _maxSpeed, 0):F0} " +
                        $"reversals={_shaker.ReversalCount - _lastReversalCount} " +
                        $"intensity={_shaker.CurrentIntensity:F0} " +
                        $"progress={_shaker.CurrentProgress:F2} dtMs={dt * 1000:F1} " +
                        _swapper.TakeRebuildStats());
                    _lastReversalCount = _shaker.ReversalCount;
                }
                else if (_shaker.LastSpeed > _maxSpeed)
                {
                    _maxSpeed = _shaker.LastSpeed;
                }

                Thread.Sleep(1);
            }
        }
        catch (Exception ex)
        {
            // Never leave the user with a stuck cursor — and never crash silently.
            Diagnostics.Log("PointerWatcher loop crashed: " + ex);
            Console.Error.WriteLine(ex);
            _swapper.Restore();
        }
    }
}
