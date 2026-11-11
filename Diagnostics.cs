namespace ShakeCursor;

/// <summary>
/// Tiny fail-safe diagnostics: writes to %TEMP%\ShakeCursor.log so a
/// completely invisible failure mode can still be diagnosed afterwards.
/// Capped at a few dozen lines to avoid growing unbounded.
/// </summary>
internal static class Diagnostics
{
    private static readonly object _lock = new();
    private static int _lines;

    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "ShakeCursor.log");

    public static void LogULWFailure(int win32Error)
    {
        Log($"UpdateLayeredWindow failed (win32 error {win32Error})");
    }

    public static void Log(string message)
    {
        lock (_lock)
        {
            if (_lines >= 400)
            {
                return;
            }
            _lines++;
            try
            {
                using var writer = new StreamWriter(LogPath, append: _lines > 1);
                writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
            }
            catch
            {
                // Diagnostics must never take the app down.
            }
        }
    }
}
