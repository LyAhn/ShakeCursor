using System.Windows.Forms;
using static ShakeCursor.NativeMethods;

namespace ShakeCursor;

/// <summary>
/// A message-only window that owns one global hotkey and raises the callback
/// on the UI thread when it is hit. The shortcut can change at any time.
/// </summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int HotkeyId = 1;

    private readonly Action _pressed;
    private bool _registered;
    private string? _attempted; // the combo last tried, so a failing one isn't retried on every settings reload

    /// <summary>Raised (once per attempt) when Windows refuses the shortcut, usually because another app owns it.</summary>
    public event Action<string>? Failed;

    public HotkeyWindow(Action pressed)
    {
        _pressed = pressed;
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) /* HWND_MESSAGE */ });
    }

    /// <summary>Registers the shortcut, or releases it when disabled.</summary>
    public void Apply(bool enabled, string combo)
    {
        if (!enabled)
        {
            Release();
            _attempted = null;
            return;
        }

        if (combo == _attempted)
        {
            return;
        }

        Release();
        _attempted = combo;
        if (!Hotkey.TryParse(combo, out uint modifiers, out uint vk))
        {
            Failed?.Invoke(combo);
            return;
        }

        _registered = RegisterHotKey(Handle, HotkeyId, modifiers | MOD_NOREPEAT, vk);
        if (!_registered)
        {
            Diagnostics.Log($"pause hotkey {combo} unavailable (already in use)");
            Failed?.Invoke(combo);
        }
    }

    private void Release()
    {
        if (_registered)
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
        Release();
        DestroyHandle();
    }
}
