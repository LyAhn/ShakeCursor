using System.Windows.Forms;
using static ShakeCursor.NativeMethods;

namespace ShakeCursor;

/// <summary>
/// A message-only window that owns one global hotkey (Ctrl+Alt+Shift+P) and
/// raises <see cref="Pressed"/> on the UI thread when it is hit.
/// </summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int HotkeyId = 1;
    private const uint KeyP = 0x50;

    private readonly Action _pressed;
    private bool _registered;

    public HotkeyWindow(Action pressed)
    {
        _pressed = pressed;
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) /* HWND_MESSAGE */ });
    }

    /// <summary>Registers or releases the hotkey. Another app may already own it.</summary>
    public void SetEnabled(bool enabled)
    {
        if (enabled == _registered)
        {
            return;
        }

        if (enabled)
        {
            _registered = RegisterHotKey(Handle, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, KeyP);
            if (!_registered)
            {
                Diagnostics.Log("pause hotkey unavailable (already in use)");
            }
        }
        else
        {
            UnregisterHotKey(Handle, HotkeyId);
            _registered = false;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            _pressed();
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        SetEnabled(false);
        DestroyHandle();
    }
}
