namespace ShakeCursor.Settings;

/// <summary>
/// An optional "ShakeCursor" shortcut on the desktop. The file's existence is
/// the source of truth, so the toggle always matches what is really there.
/// </summary>
internal static class DesktopShortcut
{
    private static string LinkPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ShakeCursor.lnk");

    // The settings app lives in a "Settings" folder beside the tray exe.
    private static string TargetPath =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "ShakeCursor.exe"));

    public static bool Exists() => File.Exists(LinkPath);

    public static void Set(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                File.Delete(LinkPath);
                return;
            }

            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null || Activator.CreateInstance(shellType) is not object shell)
            {
                return;
            }

            object? link = shellType.InvokeMember(
                "CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [LinkPath]);
            if (link is null)
            {
                return;
            }

            Type linkType = link.GetType();
            void SetProperty(string name, string value) =>
                linkType.InvokeMember(name, System.Reflection.BindingFlags.SetProperty, null, link, [value]);

            SetProperty("TargetPath", TargetPath);
            SetProperty("WorkingDirectory", Path.GetDirectoryName(TargetPath)!);
            SetProperty("IconLocation", TargetPath + ",0");
            linkType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, link, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.Reflection.TargetInvocationException
                                      or System.Runtime.InteropServices.COMException)
        {
            // The toggle re-reads the file on next open, so a failure just shows as "off".
        }
    }
}
