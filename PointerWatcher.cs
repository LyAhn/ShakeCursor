using System.Diagnostics;
using static ShakeCursor.NativeMethods;

namespace ShakeCursor;

/// <summary>
/// High-frequency polling loop: samples the real pointer, feeds shake
/// detection, and drives the overlay window. Runs on a dedicated thread that
/// sleeps 1 ms per tick (~150-250 Hz depending on timer resolution).
///
/// Only shows the overlay when the *active* system cursor is our blank arrow —
/// when an app sets an I-beam or resize cursor, the real cursor renders
/// natively and the overlay stays hidden.
/// </summary>
internal sealed class PointerWatcher
{
    private readonly CursorSwapper _swapper;
    private readonly OverlayForm _overlay;
    private readonly ShakeDetector _shaker = new();
    private Thread? _thread;
    private volatile bool _running;

    /// <summary>True while an excluded app is in front: the overlay stays hidden.</summary>
    public volatile bool Paused;
    private bool _lastActive;

    // Telemetry (written to %TEMP%\ShakeCursor.log once per second while active).
    private double _telemetryTimer;
    private float _maxSpeed;
    private int _lastReversalCount;

    // How long a non-arrow cursor blip may last before the overlay hides.
    private const double GracePeriod = 0.04;
    private double _timeSinceActive = 999;

    public PointerWatcher(CursorSwapper swapper, OverlayForm overlay, ShakeDetector shaker)
    {
        _swapper = swapper;
        _overlay = overlay;
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

        long lastTimestamp = Stopwatch.GetTimestamp();
        long lastFrequency = Stopwatch.Frequency;

        try
        {
            while (_running)
            {
                var ci = new CURSORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<CURSORINFO>() };
                if (!GetCursorInfo(ref ci))
                {
                    Thread.Sleep(1);
                    continue;
                }

                long now = Stopwatch.GetTimestamp();
                double dt = (now - lastTimestamp) / (double)lastFrequency;
                lastTimestamp = now;
                dt = Math.Clamp(dt, 0.0001, 0.1); // guard against resume/hiccups

                var pt = new System.Drawing.Point(ci.pt.X, ci.pt.Y);
                bool isActive =
                    !Paused &&
                    (ci.flags & CURSOR_SHOWING) != 0 &&
                    ci.hCursor == _swapper.ArrowHandle;

                // ALWAYS feed the detector with the real pointer position.
                // Brief cursor-shape changes while shaking (I-beam and hand
                // zones) previously reset the accumulation mid-shake, which
                // killed the effect entirely.
                float progress = _shaker.Update(pt, dt);

                if (isActive)
                {
                    _timeSinceActive = 0;
                }
                else
                {
                    _timeSinceActive += dt;
                }

                // Very brief non-arrow blips (< GracePeriod) keep showing the
                // overlay so the animation doesn't flicker while crossing
                // I-beam/link zones; longer ones hand control back to the
                // real cursor.
                bool show = !Paused && (isActive || _timeSinceActive < GracePeriod);

                if (show != _lastActive)
                {
                    _lastActive = show;
                    Diagnostics.Log(
                        $"overlay {(show ? "shown" : "hidden")}: " +
                        $"hCursor=0x{ci.hCursor.ToInt64():X} " +
                        $"flags=0x{ci.flags:X} pos=({pt.X},{pt.Y})");
                }

                _overlay.SetFrame(show, pt, progress);
                _overlay.EnsureTopmost(dt);

                _telemetryTimer += dt;
                if (_telemetryTimer >= 1.0)
                {
                    _telemetryTimer = 0;
                    Diagnostics.Log(
                        $"telemetry: maxSpeed={Interlocked.Exchange(ref _maxSpeed, 0):F0} " +
                        $"reversals={_shaker.ReversalCount - _lastReversalCount} " +
                        $"intensity={_shaker.CurrentIntensity:F0} " +
                        $"progress={_shaker.CurrentProgress:F2} dtMs={dt * 1000:F1}");
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
            // Never leave the user with a blank cursor — and never crash silently.
            Diagnostics.Log("PointerWatcher loop crashed: " + ex);
            Console.Error.WriteLine(ex);
            _swapper.Restore();
        }
    }
}
