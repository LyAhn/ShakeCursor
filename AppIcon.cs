using System.Reflection;

namespace ShakeCursor;

/// <summary>Loads the embedded multi-size app icon (tray, window).</summary>
internal static class AppIcon
{
    private const string ResourceName = "ShakeCursor.ico";

    /// <summary>The icon entry closest to <paramref name="size"/> (DPI-correct for the tray).</summary>
    public static System.Drawing.Icon Load(System.Drawing.Size size)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        return stream is null
            ? System.Drawing.SystemIcons.Application
            : new System.Drawing.Icon(stream, size);
    }
}
