using System.Text.Json;

namespace ShakeCursor;

/// <summary>What the tray app tells the settings window about updates.</summary>
public sealed class UpdateStatus
{
    /// <summary>False for dev builds and the portable zip, which can't update themselves.</summary>
    public bool Installed { get; set; }
    public bool Busy { get; set; }
    public string Text { get; set; } = "";
    public string? PendingVersion { get; set; }

    /// <summary>For copies that can't self-update: where to download a newer release.</summary>
    public string? ReleaseUrl { get; set; }
}

/// <summary>
/// Link between the tray app (which owns Velopack) and the settings app:
/// the tray publishes its update status to a small JSON file, and the settings
/// window asks it to check or to restart-and-update through named events.
/// </summary>
public static class UpdateBridge
{
    public const string CheckEvent = @"Local\ShakeCursor.CheckUpdates";
    public const string ApplyEvent = @"Local\ShakeCursor.ApplyUpdate";

    private static string StatusPath =>
        Path.Combine(Path.GetDirectoryName(AppSettings.FilePath)!, "update-status.json");

    public static void Write(UpdateStatus status)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatusPath)!);
            string temp = StatusPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(status));
            File.Move(temp, StatusPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Status is cosmetic; never let it take the tray app down.
        }
    }

    public static UpdateStatus? Read()
    {
        try
        {
            return File.Exists(StatusPath)
                ? JsonSerializer.Deserialize<UpdateStatus>(File.ReadAllText(StatusPath))
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True while the tray app is running and listening for requests.</summary>
    public static bool TrayListening()
    {
        if (EventWaitHandle.TryOpenExisting(CheckEvent, out var handle))
        {
            handle.Dispose();
            return true;
        }
        return false;
    }

    /// <summary>Signals the tray app. Returns false when it isn't running.</summary>
    public static bool Signal(string eventName)
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(eventName);
            return handle.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
