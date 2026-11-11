using System.Runtime.InteropServices;

namespace ShakeCursor;

/// <summary>
/// Replaces the standard Windows arrow cursor with a fully transparent cursor.
/// The overlay window then draws its own high-resolution arrow on top, which is
/// what makes smooth scaling and shake-to-enlarge possible.
///
/// Only OCR_NORMAL (the arrow) is blanked in v1 — other cursors (I-beam, resize,
/// hand...) keep rendering natively, and the overlay hides itself whenever the
/// active cursor is not our blank arrow.
///
/// The swap can be undone and redone at runtime (<see cref="Suspend"/> /
/// <see cref="Resume"/>) so excluded apps get the real system arrow.
/// </summary>
internal sealed class CursorSwapper : IDisposable
{
    private volatile IntPtr _arrowHandle;

    public CursorSwapper()
    {
        // A previous instance may have been hard-killed with the blank arrow
        // still installed; start from the real system cursors.
        Restore();
        Swap();
    }

    /// <summary>
    /// The system's handle for the arrow slot after the swap. Compare this
    /// against GetCursorInfo's hCursor to decide when the replacement overlay
    /// should be visible. It changes on every <see cref="Resume"/>.
    /// </summary>
    public IntPtr ArrowHandle => _arrowHandle;

    private void Swap()
    {
        // Cursor mask truth table: AND=1 means "leave the screen unchanged",
        // AND=0 means "paint the XOR color". So AND all-ones + XOR all-zeros
        // is a fully transparent cursor. (All-zeros/AND would be opaque black!)
        var andPlane = System.Linq.Enumerable.Repeat((byte)0xFF, 32 * 32 / 8).ToArray();
        var xorPlane = new byte[32 * 32 / 8];

        IntPtr blank = NativeMethods.CreateCursor(
            NativeMethods.GetModuleHandle(null),
            xHotSpot: 0, yHotSpot: 0,
            nWidth: 32, nHeight: 32,
            andPlane, xorPlane);

        if (blank == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateCursor failed (error {Marshal.GetLastWin32Error()}).");
        }

        // SetSystemCursor copies the cursor contents into the OCR_NORMAL slot
        // and then DESTROYS the handle we passed — `blank` is dangling from
        // here on. So ask the system what the arrow slot maps to now; that is
        // the handle GetCursorInfo reports while the (blank) arrow is shown.
        if (!NativeMethods.SetSystemCursor(blank, NativeMethods.OCR_NORMAL))
        {
            throw new InvalidOperationException(
                $"SetSystemCursor failed (error {Marshal.GetLastWin32Error()}).");
        }

        IntPtr handle = NativeMethods.LoadCursor(IntPtr.Zero, NativeMethods.IDC_ARROW);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"LoadCursor(IDC_ARROW) failed after swap (error {Marshal.GetLastWin32Error()}).");
        }

        _arrowHandle = handle;
        Diagnostics.Log($"cursor swapped: arrowHandle=0x{handle.ToInt64():X}");
    }

    /// <summary>Hands the real system arrow back (an excluded app is in front).</summary>
    public void Suspend()
    {
        Restore();
        _arrowHandle = IntPtr.Zero;
    }

    /// <summary>Blanks the system arrow again after <see cref="Suspend"/>.</summary>
    public void Resume() => Swap();

    /// <summary>
    /// Restores the default system cursors from the registry. Safe to call even
    /// after a partial failure. Also runs on ProcessExit so a clean exit always
    /// undoes the blank cursor.
    /// </summary>
    public void Restore()
    {
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETCURSORS, 0, IntPtr.Zero, 0);
    }

    public void Dispose() => Restore();
}
