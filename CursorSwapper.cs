using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using static ShakeCursor.NativeMethods;

namespace ShakeCursor;

/// <summary>
/// Replaces the system arrow (OCR_NORMAL) with our own vector-rendered arrow,
/// re-rendered at the current shake size. The other pointer shapes (I-beam,
/// hand, resize...) are the user's own cursor-scheme files, loaded at the
/// matching size (the files carry crisp images from 32 to 128 px).
///
/// This is a REAL system cursor, not an overlay window, so Windows draws it on
/// top of everything — taskbar, Window Peek, Alt+Tab, UAC — with no z-order
/// fighting and no flicker. Animated cursors (busy/wait) are left alone.
///
/// The swap can be undone and redone at runtime (<see cref="Suspend"/> /
/// <see cref="Resume"/>) so excluded apps get the real system arrow.
/// </summary>
internal sealed class CursorSwapper : IDisposable
{
    private const float ArrowWidthUnits = 14f;
    private const float ArrowHeightUnits = 24f;
    private const float ShadowDx = 2.5f;
    private const float ShadowDy = 3.5f;

    // Building the arrow costs ~1.5 ms; cap how often the animation rebuilds it.
    private const double MinRebuildSeconds = 1.0 / 120.0;
    // The eleven other cursors cost about 3 ms to swap, so they update less often.
    private const double MinExtrasSeconds = 1.0 / 40.0;

    // The standard (100% DPI) cursor size, and the step the other cursors snap to.
    private const float BaseCursorPx = 32f;
    private const int ExtraSizeStep = 4;

    // Registry value in HKCU\Control Panel\Cursors -> OCR id of the slot it fills.
    private static readonly (string Value, uint Id)[] ExtraSlots =
    {
        ("IBeam", 32513), ("Crosshair", 32515), ("UpArrow", 32516),
        ("SizeNWSE", 32642), ("SizeNESW", 32643), ("SizeWE", 32644), ("SizeNS", 32645),
        ("SizeAll", 32646), ("No", 32648), ("Hand", 32649), ("Help", 32651),
    };

    private readonly float _dpiScale;
    private readonly List<(uint Id, string Path)> _extras = new();
    private readonly Dictionary<(uint Id, int Px), IntPtr> _extraCache = new();
    private int _appliedExtraPx = -1;

    // Rebuild cost, logged with the telemetry so smoothness can be tuned on data.
    private int _rebuildCount;
    private double _rebuildTotalMs;
    private double _rebuildMaxMs;
    private double _loadMs;
    private double _extraMs;

    private readonly object _gate = new();
    private readonly float _unitsToPx; // px per arrow unit at scale 1.0 (DPI aware)

    private float _restScale = 1f;
    private float _maxScale = 2.6f;
    private int _fillArgb = Color.Black.ToArgb();
    private int _outlineArgb = Color.White.ToArgb();
    private bool _shadow = true;

    private float _progress;
    private int _appliedHeightPx = -1;
    private bool _suspended;
    private bool _configured;
    private long _lastRebuildTicks;
    private long _lastExtrasTicks;

    public CursorSwapper(float dpiScale)
    {
        _dpiScale = dpiScale;
        _unitsToPx = ArrowGeometry.UnitsToPx * dpiScale;
        ResolveExtraCursors();

        // A previous instance may have been hard-killed with an enlarged arrow
        // still installed; start from the real system cursors.
        Restore();
    }

    /// <summary>Applies size and style (live from settings) and re-renders.</summary>
    public void Configure(float restScale, float maxScale, Color fill, Color outline, bool shadow)
    {
        lock (_gate)
        {
            _restScale = restScale;
            _maxScale = maxScale;
            _fillArgb = fill.ToArgb();
            _outlineArgb = outline.ToArgb();
            _shadow = shadow;
            _configured = true;
            _appliedHeightPx = -1;
            _appliedExtraPx = -1;
            Apply(arrow: true, extras: true);
        }
        PreloadExtras();
    }

    /// <summary>
    /// Called every poll tick with the smoothed shake progress (0..1). Only
    /// rebuilds the cursor when its pixel size actually changes.
    /// </summary>
    public void SetProgress(float progress)
    {
        lock (_gate)
        {
            _progress = Math.Clamp(progress, 0f, 1f);
            if (_suspended || !_configured)
            {
                return;
            }

            bool arrowStale = HeightPxFor(_progress) != _appliedHeightPx;
            bool extrasStale = ExtraPxFor(_progress) != _appliedExtraPx;
            if (!arrowStale && !extrasStale)
            {
                return;
            }

            // Throttled parts are simply caught up by a later tick.
            long now = Stopwatch.GetTimestamp();
            bool doArrow = arrowStale && Seconds(now - _lastRebuildTicks) >= MinRebuildSeconds;
            bool doExtras = extrasStale && Seconds(now - _lastExtrasTicks) >= MinExtrasSeconds;
            if (doArrow || doExtras)
            {
                Apply(doArrow, doExtras);
            }
        }
    }

    /// <summary>Hands the real system arrow back (an excluded app is in front).</summary>
    public void Suspend()
    {
        lock (_gate)
        {
            _suspended = true;
            Restore();
        }
    }

    /// <summary>Installs our arrow again after <see cref="Suspend"/>.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            _suspended = false;
            _appliedHeightPx = -1;
            _appliedExtraPx = -1;
            if (_configured)
            {
                Apply(arrow: true, extras: true);
            }
        }
    }

    /// <summary>
    /// Restores the default system cursors from the registry. Safe to call even
    /// after a partial failure. Also runs on ProcessExit so a clean exit always
    /// undoes the custom cursor.
    /// </summary>
    public void Restore()
    {
        SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
        _appliedHeightPx = -1;
        _appliedExtraPx = -1;
    }

    public void Dispose()
    {
        Restore();
        foreach (IntPtr cursor in _extraCache.Values)
        {
            DestroyIcon(cursor);
        }
        _extraCache.Clear();
    }

    /// <summary>Rebuild count and cost since the last call, for the telemetry log.</summary>
    public string TakeRebuildStats()
    {
        lock (_gate)
        {
            string text = _rebuildCount == 0
                ? "rebuilds=0"
                : $"rebuilds={_rebuildCount} avgMs={_rebuildTotalMs / _rebuildCount:F2} maxMs={_rebuildMaxMs:F2} extraMs={_extraMs:F1} loadMs={_loadMs:F1}";
            _rebuildCount = 0;
            _rebuildTotalMs = 0;
            _rebuildMaxMs = 0;
            _loadMs = 0;
            _extraMs = 0;
            return text;
        }
    }

    private void ResolveExtraCursors()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors");
            foreach (var (value, id) in ExtraSlots)
            {
                string? raw = key?.GetValue(value) as string;
                string path = string.IsNullOrWhiteSpace(raw) ? "" : Environment.ExpandEnvironmentVariables(raw);
                if (File.Exists(path))
                {
                    _extras.Add((id, path));
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // No readable scheme: those cursors simply stay at their normal size.
        }
        Diagnostics.Log($"extra cursors resolved: {_extras.Count}/{ExtraSlots.Length}");
    }

    private int ExtraPxFor(float progress)
    {
        float px = BaseCursorPx * _dpiScale * ScaleFor(progress);
        // The cursor files carry crisp images up to 128 px; beyond that they'd only be upscaled.
        return Math.Clamp((int)MathF.Round(px / ExtraSizeStep) * ExtraSizeStep, 16, 128);
    }

    private float ScaleFor(float progress) => _restScale + (_maxScale - _restScale) * progress;

    private int HeightPxFor(float progress) =>
        (int)MathF.Round(ArrowHeightUnits * _unitsToPx * ScaleFor(progress));

    private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;

    /// <summary>Installs the arrow and/or the other cursors at the current size.</summary>
    private void Apply(bool arrow, bool extras)
    {
        if (_suspended)
        {
            return;
        }

        long started = Stopwatch.GetTimestamp();
        if (arrow)
        {
            _lastRebuildTicks = started;
            IntPtr cursor = BuildCursor(_unitsToPx * ScaleFor(_progress));
            if (cursor == IntPtr.Zero)
            {
                Diagnostics.Log($"BuildCursor failed (error {Marshal.GetLastWin32Error()})");
                return;
            }

            // SetSystemCursor takes ownership of (and destroys) the handle on success.
            if (!SetSystemCursor(cursor, OCR_NORMAL))
            {
                Diagnostics.Log($"SetSystemCursor failed (error {Marshal.GetLastWin32Error()})");
                DestroyIcon(cursor);
                return;
            }
            _appliedHeightPx = HeightPxFor(_progress);
        }

        if (extras)
        {
            _lastExtrasTicks = started;
            RebuildExtras();
        }

        // Windows only re-reads the cursor image on the next mouse message, so a
        // stationary pointer would keep the old size while it shrinks back. A
        // zero-distance move makes it refresh.
        mouse_event(MOUSEEVENTF_MOVE, 0, 0, 0, UIntPtr.Zero);

        double ms = Seconds(Stopwatch.GetTimestamp() - started) * 1000.0;
        _rebuildCount++;
        _rebuildTotalMs += ms;
        _rebuildMaxMs = Math.Max(_rebuildMaxMs, ms);
    }

    /// <summary>
    /// Loads every cursor at every size it can be shown at, off the poll thread,
    /// so the animation only ever copies a cached handle (a cold LoadImage from
    /// disk costs about 1 ms per cursor).
    /// </summary>
    private void PreloadExtras()
    {
        (uint Id, string Path)[] extras;
        int from, to;
        lock (_gate)
        {
            extras = _extras.ToArray();
            from = ExtraPxFor(0f);
            to = ExtraPxFor(1f);
        }

        new Thread(() =>
        {
            for (int px = from; px <= to; px += ExtraSizeStep)
            {
                foreach (var (id, path) in extras)
                {
                    lock (_gate)
                    {
                        if (_extraCache.ContainsKey((id, px)))
                        {
                            continue;
                        }
                    }

                    IntPtr loaded = LoadImage(IntPtr.Zero, path, IMAGE_CURSOR, px, px, LR_LOADFROMFILE);
                    if (loaded == IntPtr.Zero)
                    {
                        continue;
                    }

                    lock (_gate)
                    {
                        if (!_extraCache.TryAdd((id, px), loaded))
                        {
                            DestroyIcon(loaded);
                        }
                    }
                }
            }
        })
        {
            Name = "CursorPreload",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        }.Start();
    }

    /// <summary>Loads the user's scheme cursors at the current size and installs them.</summary>
    private void RebuildExtras()
    {
        int px = ExtraPxFor(_progress);
        if (px == _appliedExtraPx)
        {
            return;
        }
        _appliedExtraPx = px;
        long t0 = Stopwatch.GetTimestamp();

        for (int i = _extras.Count - 1; i >= 0; i--)
        {
            var (id, path) = _extras[i];
            if (!_extraCache.TryGetValue((id, px), out IntPtr cached))
            {
                long l0 = Stopwatch.GetTimestamp();
                cached = LoadImage(IntPtr.Zero, path, IMAGE_CURSOR, px, px, LR_LOADFROMFILE);
                _loadMs += (Stopwatch.GetTimestamp() - l0) * 1000.0 / Stopwatch.Frequency;
                if (cached == IntPtr.Zero)
                {
                    Diagnostics.Log($"could not load {path} at {px}px (error {Marshal.GetLastWin32Error()})");
                    _extras.RemoveAt(i); // leave this one native from now on
                    continue;
                }
                _extraCache.TryAdd((id, px), cached);
            }

            // SetSystemCursor consumes the handle it is given, so hand it a copy.
            IntPtr copy = CopyIcon(cached);
            if (copy != IntPtr.Zero && !SetSystemCursor(copy, id))
            {
                DestroyIcon(copy);
            }
        }
        _extraMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
    }

    /// <summary>Renders the arrow at <paramref name="u"/> px per unit into a cursor handle.</summary>
    private IntPtr BuildCursor(float u)
    {
        float penWidth = MathF.Max(2f, 1.6f * u);
        int pad = (int)MathF.Ceiling(penWidth) + 2;
        int w = 2 * pad + (int)MathF.Ceiling(ArrowWidthUnits * u + ShadowDx);
        int h = 2 * pad + (int)MathF.Ceiling(ArrowHeightUnits * u + ShadowDy);

        var pts = new PointF[ArrowGeometry.Points.Length];
        for (int i = 0; i < pts.Length; i++)
        {
            pts[i] = new PointF(pad + ArrowGeometry.Points[i].X * u, pad + ArrowGeometry.Points[i].Y * u);
        }

        // Premultiplied ARGB: exactly what an alpha cursor's colour bitmap holds.
        using var surface = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(surface))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            if (_shadow)
            {
                var shifted = new PointF[pts.Length];
                for (int i = 0; i < pts.Length; i++)
                {
                    shifted[i] = new PointF(pts[i].X + ShadowDx, pts[i].Y + ShadowDy);
                }
                using var shadowBrush = new SolidBrush(Color.FromArgb(70, 0, 0, 0));
                g.FillPolygon(shadowBrush, shifted);
            }

            using var bodyBrush = new SolidBrush(Color.FromArgb(_fillArgb));
            using var outline = new Pen(Color.FromArgb(_outlineArgb), penWidth) { LineJoin = LineJoin.Round };
            g.DrawPolygon(outline, pts);
            g.FillPolygon(bodyBrush, pts);
        }

        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // top-down, matches GDI+ row order
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            },
        };
        IntPtr colorBitmap = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (colorBitmap == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr maskBitmap = IntPtr.Zero;
        try
        {
            var data = surface.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, surface.PixelFormat);
            try
            {
                var buffer = new byte[w * h * 4];
                Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
                Marshal.Copy(buffer, 0, bits, buffer.Length);
            }
            finally
            {
                surface.UnlockBits(data);
            }

            // Ignored for a 32-bit alpha colour bitmap, but CreateIconIndirect requires one.
            maskBitmap = CreateBitmap(w, h, 1, 1, IntPtr.Zero);
            if (maskBitmap == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var info = new ICONINFO
            {
                fIcon = false, // a cursor: the hotspot below is honoured
                xHotspot = pad,
                yHotspot = pad,
                hbmMask = maskBitmap,
                hbmColor = colorBitmap,
            };
            return CreateIconIndirect(ref info);
        }
        finally
        {
            if (maskBitmap != IntPtr.Zero)
            {
                DeleteObject(maskBitmap);
            }
            DeleteObject(colorBitmap);
        }
    }
}
