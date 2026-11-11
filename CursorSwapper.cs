using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using static ShakeCursor.NativeMethods;

namespace ShakeCursor;

/// <summary>
/// Replaces the system arrow (OCR_NORMAL) with our own vector-rendered arrow,
/// re-rendered at the current shake size.
///
/// This is a REAL system cursor, not an overlay window, so Windows draws it on
/// top of everything — taskbar, Window Peek, Alt+Tab, UAC — with no z-order
/// fighting and no flicker. Other cursors (I-beam, resize, hand...) are left
/// alone and keep rendering natively.
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

    // Building a cursor costs ~1 ms; cap how often the animation rebuilds it.
    private const double MinRebuildSeconds = 1.0 / 60.0;

    private readonly object _gate = new();
    private readonly float _unitsToPx; // px per arrow unit at scale 1.0 (DPI aware)

    private float _restScale = 1f;
    private float _maxScale = 2.6f;
    private int _fillArgb = Color.Black.ToArgb();
    private int _outlineArgb = Color.White.ToArgb();
    private bool _shadow = true;

    private float _progress;
    private int _appliedHeightPx = -1;
    private bool _dirty = true;
    private bool _suspended;
    private bool _configured;
    private long _lastRebuildTicks;

    public CursorSwapper(float dpiScale)
    {
        _unitsToPx = ArrowGeometry.UnitsToPx * dpiScale;

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
            _dirty = true;
            Rebuild();
        }
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

            int heightPx = HeightPxFor(_progress);
            if (!_dirty && heightPx == _appliedHeightPx)
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            if ((now - _lastRebuildTicks) / (double)Stopwatch.Frequency < MinRebuildSeconds)
            {
                return; // the next tick will catch up
            }

            Rebuild();
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
            _dirty = true;
            if (_configured)
            {
                Rebuild();
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
        _dirty = true;
        _appliedHeightPx = -1;
    }

    public void Dispose() => Restore();

    private float ScaleFor(float progress) => _restScale + (_maxScale - _restScale) * progress;

    private int HeightPxFor(float progress) =>
        (int)MathF.Round(ArrowHeightUnits * _unitsToPx * ScaleFor(progress));

    private void Rebuild()
    {
        if (_suspended)
        {
            return;
        }

        _lastRebuildTicks = Stopwatch.GetTimestamp();
        float u = _unitsToPx * ScaleFor(_progress);

        IntPtr cursor = BuildCursor(u);
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

        _dirty = false;
        _appliedHeightPx = HeightPxFor(_progress);

        // Windows only re-reads the cursor image on the next mouse message, so a
        // stationary pointer would keep the old size while it shrinks back. A
        // zero-distance move makes it refresh.
        mouse_event(MOUSEEVENTF_MOVE, 0, 0, 0, UIntPtr.Zero);
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
