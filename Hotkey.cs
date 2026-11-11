namespace ShakeCursor;

/// <summary>
/// The pause shortcut as text ("Ctrl+Alt+Shift+P") and as the modifier flags and
/// virtual-key code that RegisterHotKey wants. Shared by the tray app (which
/// registers it) and the settings app (which captures a new one).
///
/// Only a letter, digit or F1-F24 plus at least one of Ctrl, Alt or Shift is
/// accepted: that avoids Windows-reserved keys and shortcuts that would fire
/// while typing.
/// </summary>
public static class Hotkey
{
    public const string Default = "Ctrl+Alt+Shift+P";

    public const uint ModAlt = 0x1;
    public const uint ModControl = 0x2;
    public const uint ModShift = 0x4;

    public static bool IsSupportedKey(uint vk) =>
        vk is >= 0x41 and <= 0x5A      // A-Z
           or >= 0x30 and <= 0x39      // 0-9
           or >= 0x70 and <= 0x87;     // F1-F24

    /// <summary>Builds the text for a captured shortcut; null if it isn't allowed.</summary>
    public static string? Format(bool ctrl, bool alt, bool shift, uint vk)
    {
        if (!(ctrl || alt || shift) || !IsSupportedKey(vk))
        {
            return null;
        }

        var parts = new List<string>();
        if (ctrl) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        parts.Add(vk >= 0x70 ? $"F{vk - 0x70 + 1}" : ((char)vk).ToString());
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        uint key = 0;
        foreach (string raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL": modifiers |= ModControl; break;
                case "ALT": modifiers |= ModAlt; break;
                case "SHIFT": modifiers |= ModShift; break;
                case var token when key == 0 && TryParseKey(token, out uint parsed):
                    key = parsed;
                    break;
                default:
                    return false;
            }
        }

        if (modifiers == 0 || key == 0)
        {
            return false;
        }
        vk = key;
        return true;
    }

    private static bool TryParseKey(string token, out uint vk)
    {
        vk = 0;
        if (token.Length == 1 && IsSupportedKey(token[0]))
        {
            vk = token[0];
            return true;
        }
        if (token.Length is 2 or 3 && token[0] == 'F'
            && uint.TryParse(token.AsSpan(1), out uint n) && n is >= 1 and <= 24)
        {
            vk = 0x70 + n - 1;
            return true;
        }
        return false;
    }

    /// <summary>For display: "Ctrl + Alt + Shift + P".</summary>
    public static string Pretty(string text) => text.Replace("+", " + ");
}
