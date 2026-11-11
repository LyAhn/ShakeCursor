using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using static ShakeCursor.NativeMethods;

namespace ShakeCursor;

/// <summary>
/// A click-through, topmost layered window that draws the replacement cursor.
/// All rendering happens via UpdateLayeredWindow from the pointer-poll thread,
/// so the WinForms paint pipeline is never involved — no flicker, no activation.
///
/// The arrow is drawn as an anti-aliased vector polygon at any scale, which is
/// why the enlarged cursor stays crisp instead of blowing up a 32x32 bitmap.
/// </summary>
internal sealed class OverlayForm : Form
{
    internal static readonly PointF[] ArrowUnits =
        ArrowGeometry.Points.Select(p => new PointF(p.X, p.Y)).ToArray();

    private const int Pad = 32; // margin around the arrow, also absorbs the shadow

    // The window/bitmap are sized once for this absolute upper bound so the
    // user can raise "max size" in settings without any reallocation.
    private const float HardMaxScale = 4.0f;

    // Cursor styling — swap these for other pointer schemes.
    private volatile int _fillArgb = Color.Black.ToArgb();
    private volatile int _outlineArgb = Color.White.ToArgb();
    private volatile bool _shadow = true;

    private float _restScale = 1.0f;
    private float _maxScale = 2.6f;

    private readonly Bitmap _surface;
    private readonly Graphics _graphics;
    private readonly float _unitsToPx; // base px per arrow unit at scale 1.0 (DPI aware)

    // A DIB section we own: GDI+ pixels are copied byte-for-byte into it each
    // frame, preserving the premultiplied alpha that UpdateLayeredWindow
    // expects. (GetHbitmap proved unreliable here — it dropped the alpha and
    // rendered the whole window opaque black.)
    private IntPtr _dibBitmap;
    private IntPtr _dibBits;
    private readonly byte[] _frameBuffer;

    private BLENDFUNCTION _blend = new()
    {
        BlendOp = AC_SRC_OVER,
        BlendFlags = 0,
        SourceConstantAlpha = 255,
        AlphaFormat = AC_SRC_ALPHA,
    };

    private bool _visibleState;
    private double _timeSinceTopmostAssert = 999;

    /// <summary>
    /// The taskbar/Start menu live in the same TOPMOST band and re-assert
    /// themselves above us when clicked, so we re-claim topmost periodically
    /// (fast fallback) and instantly via <see cref="PokeTopmost"/> when the
    /// system reports a foreground change. Call every tick from the pointer
    /// thread.
    /// </summary>
    private const double TopmostInterval = 0.1;

    public void EnsureTopmost(double dt)
    {
        _timeSinceTopmostAssert += dt;
        if (_visibleState && _timeSinceTopmostAssert > TopmostInterval)
        {
            PokeTopmost();
        }
    }

    /// <summary>Re-claims topmost immediately (any thread).</summary>
    public void PokeTopmost()
    {
        _timeSinceTopmostAssert = 0;
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
            0x0001 /*NOSIZE*/ | 0x0002 /*NOMOVE*/ | 0x0010 /*NOACTIVATE*/);
    }

    public OverlayForm(float dpiScale)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(-32000, -32000, 1, 1);
        TopMost = true;
        Enabled = false;

        _unitsToPx = ArrowGeometry.UnitsToPx * dpiScale;

        // Window is sized for the absolute maximum scale once, so per-frame
        // work never needs to resize the window or reallocate the bitmap, and
        // settings changes never trigger reallocation.
        int maxW = 2 * Pad + (int)Math.Ceiling(14 * _unitsToPx * HardMaxScale) + 8;
        int maxH = 2 * Pad + (int)Math.Ceiling(24 * _unitsToPx * HardMaxScale) + 8;
        Size = new Size(maxW, maxH);

        _surface = new Bitmap(maxW, maxH, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        _graphics = Graphics.FromImage(_surface);
        _graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = maxW,
                biHeight = -maxH, // negative = top-down, matches GDI+ row order
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            },
        };
        _dibBitmap = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out _dibBits, IntPtr.Zero, 0);
        if (_dibBitmap == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateDIBSection failed (error {Marshal.GetLastWin32Error()}).");
        }
        _frameBuffer = new byte[maxW * maxH * 4];
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style = WS_POPUP;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST
                        | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    /// <summary>Sets the arrow colours and shadow (live from settings).</summary>
    public void SetStyle(Color fill, Color outline, bool shadow)
    {
        _fillArgb = fill.ToArgb();
        _outlineArgb = outline.ToArgb();
        _shadow = shadow;
    }

    /// <summary>Sets the user-configurable cursor sizes (live from settings).</summary>
    public void SetSizes(float restScale, float maxScale)
    {
        _restScale = restScale;
        _maxScale = maxScale;
    }

    /// <summary>
    /// Called every poll tick from the pointer thread (safe to call from any
    /// thread — ShowWindow/BeginInvoke are only touched on state changes).
    /// </summary>
    public void SetFrame(bool show, Point cursorPos, float progress)
    {
        if (show != _visibleState)
        {
            _visibleState = show;
            if (show)
            {
                // Re-assert topmost immediately on show (also resets the timer).
                _timeSinceTopmostAssert = 0;
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                    0x0001 /*NOSIZE*/ | 0x0002 /*NOMOVE*/ |
                    0x0010 /*NOACTIVATE*/ | 0x0040 /*SHOWWINDOW*/);
            }
            else
            {
                BeginInvoke(() => ShowWindow(Handle, SW_HIDE));
            }
        }

        if (!show)
        {
            return;
        }

        RenderAndComposite(cursorPos, progress);
    }

    private void RenderAndComposite(Point cursorPos, float progress)
    {
        _graphics.Clear(Color.Transparent);

        // Shake progress maps between the user's rest size and max size.
        float drawScale = _restScale + (_maxScale - _restScale) * Math.Clamp(progress, 0f, 1f);
        float u = _unitsToPx * drawScale;

        // Build the scaled arrow path with the tip at (Pad, Pad); the window is
        // positioned so that point lands exactly on the pointer location.
        var pts = new PointF[ArrowUnits.Length];
        for (int i = 0; i < ArrowUnits.Length; i++)
        {
            pts[i] = new PointF(Pad + ArrowUnits[i].X * u, Pad + ArrowUnits[i].Y * u);
        }

        // Soft drop shadow: translucent dark copy, offset down-right.
        if (_shadow)
        {
            using var shadowPath = new GraphicsPath();
            shadowPath.AddPolygon(Translate(pts, 2.5f, 3.5f));
            using var shadowBrush = new SolidBrush(Color.FromArgb(70, 0, 0, 0));
            _graphics.FillPolygon(shadowBrush, shadowPath.PathPoints);
        }

        // White arrow body with a black outline, Windows-style.
        using (var body = new GraphicsPath())
        {
            body.AddPolygon(pts);

            using var bodyBrush = new SolidBrush(Color.FromArgb(_fillArgb));
            using var outline = new Pen(Color.FromArgb(_outlineArgb), MathF.Max(2f, 1.6f * u))
            {
                LineJoin = LineJoin.Round,
            };

            // Draw the outline slightly wider underneath the fill so the fill
            // eats into it from the inside — gives a clean even border.
            _graphics.DrawPath(outline, body);
            _graphics.FillPolygon(bodyBrush, pts);
        }

        // --- Push the pixels: GDI+ surface → DIB section → ULW ---
        var rect = new Rectangle(0, 0, _surface.Width, _surface.Height);
        var bits = _surface.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, _surface.PixelFormat);
        try
        {
            Marshal.Copy(bits.Scan0, _frameBuffer, 0, _frameBuffer.Length);
        }
        finally
        {
            _surface.UnlockBits(bits);
        }
        Marshal.Copy(_frameBuffer, 0, _dibBits, _frameBuffer.Length);

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        try
        {
            IntPtr oldBitmap = SelectObject(memDc, _dibBitmap);

            // (Pad, Pad) inside the window must be at the pointer position, and
            // the window is resized to the surface dimensions here — this keeps
            // the ULW geometry independent of any WinForms DPI scaling.
            var dest = new POINT { X = cursorPos.X - Pad, Y = cursorPos.Y - Pad };
            var size = new SIZE { Width = _surface.Width, Height = _surface.Height };
            var src = new POINT { X = 0, Y = 0 };

            if (!UpdateLayeredWindow(
                Handle, screenDc, ref dest, ref size, memDc, ref src,
                0, ref _blend, ULW_ALPHA))
            {
                Diagnostics.LogULWFailure(Marshal.GetLastWin32Error());
            }

            SelectObject(memDc, oldBitmap);
        }
        finally
        {
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static PointF[] Translate(PointF[] pts, float dx, float dy)
    {
        var copy = new PointF[pts.Length];
        for (int i = 0; i < pts.Length; i++)
        {
            copy[i] = new PointF(pts[i].X + dx, pts[i].Y + dy);
        }
        return copy;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _graphics.Dispose();
            _surface.Dispose();
            if (_dibBitmap != IntPtr.Zero)
            {
                DeleteObject(_dibBitmap);
                _dibBitmap = IntPtr.Zero;
            }
        }
        base.Dispose(disposing);
    }
}