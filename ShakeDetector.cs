namespace ShakeCursor;

/// <summary>
/// Detects "shake to enlarge" pointer motion (the macOS behaviour).
///
/// A direction reversal is detected as a SIGN FLIP of the dominant axis of a
/// low-pass-filtered velocity, gated by the peak speed of the swing that just
/// ended. This is essential: a real hand shake is sinusoidal, so velocity
/// passes through zero at each turnaround — per-tick direction-flip detection
/// at high sampling rates never sees a fast reversal (the flip tick is always
/// near zero speed). Smoothed, windowed detection works on real hand motion.
///
/// Reversal events accumulate intensity (decaying exponentially), which maps
/// to a shake progress value (0..1); the returned progress is exponentially
/// smoothed so the animation feels fluid rather than binary. The cursor renderer maps
/// progress to actual cursor size (rest size → max size).
/// </summary>
internal sealed class ShakeDetector
{
    // --- Tuning constants ----------------------------------------------------
    // Speeds are in physical pixels per second.

    private const float VelocityTau = 0.02f;      // s; smoothing of velocity (kills per-tick noise)
    private const float SignDeadzone = 150f;      // px/s; below this the dominant axis has no sign
    private const float MinShakeSpeed = 1500f;    // px/s at sensitivity 1; swing peak must exceed this (÷ sensitivity)
    private const int MinChain = 3;               // reversals in quick succession before any count (rejects flick + correction)
    private const double ChainGap = 0.4;          // s; max gap between reversals of one shake
    private const float SpeedCap = 3500f;         // px/s; clamps one reversal's contribution
    private const float ReversalGain = 0.8f;      // per reversal event (× user sensitivity)

    private const float IntensityHoldTau = 0.55f; // s; decay while the pointer keeps moving fast
    private const float IntensityIdleTau = 0.20f; // s; decay once lingering has expired
    private const float HoldSpeed = 600f;         // px/s; smoothed speed treated as "still moving"
    private const float MaxIntensity = 10000f;

    private const float IntensityStart = 1800f;   // intensity where enlargement begins
    private const float IntensityFull = 5000f;    // intensity at full enlargement

    private const float GrowTau = 0.06f;   // s; how fast the cursor grows
    private const float ShrinkTau = 0.25f; // s; how fast it settles back

    // --- State ----------------------------------------------------------------

    private System.Drawing.Point _prevPos;
    private float _smoothVx;
    private float _smoothVy;
    private float _peakRecent;
    private int _prevSign;
    private int _chain;
    private double _sinceReversal = 999;
    private float _intensity;
    private float _progress;
    private double _timeSinceMovement;
    private bool _hasPrevious;

    // --- Live stats (for telemetry / tuning) ---------------------------------
    public float CurrentIntensity => _intensity;
    public float CurrentProgress => _progress;
    public float LastSpeed { get; private set; }
    public int ReversalCount { get; private set; }

    /// <summary>User sensitivity multiplier applied to each reversal event.</summary>
    public float Sensitivity { get; set; } = 1f;

    /// <summary>
    /// Seconds the cursor stays enlarged after the pointer stops moving,
    /// before it starts shrinking back.
    /// </summary>
    public float LingerSeconds { get; set; } = 1.75f;

    /// <summary>
    /// Feed one pointer sample. Returns the current smoothed shake progress
    /// (0 = rest size, 1 = fully enlarged).
    /// </summary>
    public float Update(System.Drawing.Point pos, double dt)
    {
        if (!_hasPrevious || dt <= 0)
        {
            _hasPrevious = true;
            _prevPos = pos;
            return _progress;
        }

        float vx = (pos.X - _prevPos.X) / (float)dt;
        float vy = (pos.Y - _prevPos.Y) / (float)dt;
        _prevPos = pos;
        LastSpeed = MathF.Sqrt(vx * vx + vy * vy);

        // Low-pass the velocity: per-tick jitter can't flip its sign, but a
        // genuine back-and-forth turnaround crosses zero cleanly.
        float velBlend = 1f - MathF.Exp((float)(-dt / VelocityTau));
        _smoothVx += (vx - _smoothVx) * velBlend;
        _smoothVy += (vy - _smoothVy) * velBlend;
        float smoothSpeed = MathF.Sqrt(_smoothVx * _smoothVx + _smoothVy * _smoothVy);

        // Swing peak: the max smoothed speed since the last reversal. No time
        // decay — a reversal "spends" it and resets it, so each event sees the
        // true peak of the swing that just ended (the flip itself always
        // happens near zero speed, well after the swing's fastest moment).
        if (smoothSpeed > _peakRecent)
        {
            _peakRecent = smoothSpeed;
        }

        // Dominant-axis sign flip while the swing peak is high = reversal.
        float dominant = MathF.Abs(_smoothVx) >= MathF.Abs(_smoothVy) ? _smoothVx : _smoothVy;
        int signNow = MathF.Abs(dominant) > SignDeadzone ? Math.Sign(dominant) : 0;
        _sinceReversal += dt;
        float minSpeed = MinShakeSpeed / MathF.Max(Sensitivity, 0.1f);
        if (signNow != 0 && _prevSign != 0 && signNow != _prevSign && _peakRecent >= minSpeed)
        {
            _chain = _sinceReversal <= ChainGap ? _chain + 1 : 1;
            _sinceReversal = 0;
            if (_chain >= MinChain)
            {
                _intensity += Math.Min(_peakRecent, SpeedCap) * ReversalGain * Sensitivity;
                ReversalCount++;
            }
            _peakRecent = smoothSpeed; // start the next swing's peak from here
        }
        if (signNow != 0)
        {
            _prevSign = signNow;
        }

        // Exponential decay of intensity:
        //  - while the pointer keeps moving: slow decay (stays enlarged),
        //  - after it stops: no decay at all until the linger period the user
        //    configured has passed (the "stays big for a moment" behaviour),
        //  - then: fast decay so it settles back smoothly.
        if (smoothSpeed > HoldSpeed)
        {
            _timeSinceMovement = 0;
            _intensity *= MathF.Exp((float)(-dt / IntensityHoldTau));
        }
        else
        {
            _timeSinceMovement += dt;
            bool lingering = _timeSinceMovement < LingerSeconds && _intensity > IntensityStart;
            if (!lingering)
            {
                _intensity *= MathF.Exp((float)(-dt / IntensityIdleTau));
            }
        }
        _intensity = Math.Clamp(_intensity, 0f, MaxIntensity);

        // Map intensity to target progress with a smooth ramp.
        float t = Math.Clamp(
            (_intensity - IntensityStart) / (IntensityFull - IntensityStart),
            0f, 1f);

        // Exponentially approach the target — faster growing, slower shrinking.
        float tau = t > _progress ? GrowTau : ShrinkTau;
        float blend = 1f - MathF.Exp((float)(-dt / tau));
        _progress += (t - _progress) * blend;

        return _progress;
    }

    public void Reset()
    {
        _hasPrevious = false;
        _smoothVx = _smoothVy = 0f;
        _peakRecent = 0f;
        _prevSign = 0;
        _chain = 0;
        _sinceReversal = 999;
        _intensity = 0f;
        _progress = 0f;
        _timeSinceMovement = 0;
    }
}
