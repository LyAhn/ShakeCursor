using System.Text.Json;

namespace ShakeCursor;

/// <summary>
/// User settings, persisted as JSON at %APPDATA%\ShakeCursor\settings.json.
/// Missing or corrupt files fall back to defaults.
/// </summary>
public sealed class AppSettings
{
    public float RestScale { get; set; } = 1.0f;      // cursor size at rest (×)
    public float MaxScale { get; set; } = 4.0f;       // cursor size when fully enlarged (×)
    public float Sensitivity { get; set; } = 1.0f;    // shake sensitivity multiplier
    public float LingerSeconds { get; set; } = 1.75f; // enlarged hold time after stopping (s)
    public string FillColor { get; set; } = "#000000";    // arrow body
    public string OutlineColor { get; set; } = "#FFFFFF"; // arrow outline
    public bool Shadow { get; set; } = true;              // soft drop shadow
    public bool AutoUpdate { get; set; } = true;          // check for and download updates in the background
    public bool PauseInFullscreen { get; set; } = true;   // hand the real cursor back while a fullscreen app/game is in front
    public bool PauseHotkey { get; set; } = true;         // Ctrl+Alt+Shift+P toggles pause from anywhere
    public string? LastRunVersion { get; set; }           // version that last ran installed; drives the "what's new" note
    public bool FirstRunShown { get; set; }               // first-run balloon tip already shown
    public List<string> ExcludedApps { get; set; } = new(); // process names, e.g. "game.exe"

    /// <summary>
    /// Desired autostart state, written by the settings app; the tray app applies
    /// it to the registry. Null = never chosen (leave the registry alone).
    /// </summary>
    public bool? StartWithWindows { get; set; }

    /// <summary>
    /// Which arrow-size calibration the scales were saved under. Files without it
    /// predate the 1.0.5 recalibration, where 1.0x was about 1.56x larger.
    /// </summary>
    public int? SizeCalibration { get; set; }

    private const int CurrentSizeCalibration = 2;
    private const float OldToNewScale = 1.25f / 0.8f;

    /// <summary>Normalises a process or file name to the stored "name.exe" form.</summary>
    public static string NormalizeApp(string name)
    {
        string n = Path.GetFileName(name.Trim()).ToLowerInvariant();
        return n.EndsWith(".exe") ? n : n + ".exe";
    }

    public void CopyFrom(AppSettings other)
    {
        RestScale = other.RestScale;
        MaxScale = other.MaxScale;
        Sensitivity = other.Sensitivity;
        LingerSeconds = other.LingerSeconds;
        FillColor = other.FillColor;
        OutlineColor = other.OutlineColor;
        Shadow = other.Shadow;
        AutoUpdate = other.AutoUpdate;
        PauseInFullscreen = other.PauseInFullscreen;
        PauseHotkey = other.PauseHotkey;
        LastRunVersion = other.LastRunVersion;
        FirstRunShown = other.FirstRunShown;
        StartWithWindows = other.StartWithWindows;
        ExcludedApps = new List<string>(other.ExcludedApps);
    }

    /// <summary>Parses "#RRGGBB"; returns false for anything else.</summary>
    public static bool TryParseColor(string? hex, out System.Drawing.Color color)
    {
        color = default;
        if (hex is not { Length: 7 } || hex[0] != '#' ||
            !int.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out int rgb))
        {
            return false;
        }
        color = System.Drawing.Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        return true;
    }

    public static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ShakeCursor", "settings.json");

    /// <summary>Loads settings; a missing or corrupt file gives defaults.</summary>
    public static AppSettings Load() => TryLoad() ?? new AppSettings();

    /// <summary>
    /// Loads settings, retrying briefly if the file is mid-write. Returns null
    /// when the file is missing or unreadable (callers keep their current values).
    /// </summary>
    public static AppSettings? TryLoad()
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return null;
                }
                string json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings is { SizeCalibration: null })
                {
                    // Keep the cursor looking the same after the recalibration.
                    settings.RestScale *= OldToNewScale;
                    settings.MaxScale *= OldToNewScale;
                }
                if (settings is not null)
                {
                    settings.SizeCalibration = CurrentSizeCalibration;
                }
                settings?.Clamp();
                return settings;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Thread.Sleep(40);
            }
        }
        return null;
    }

    /// <summary>
    /// Read-modify-write: reloads the file, applies <paramref name="change"/> and
    /// saves, so one app never clobbers fields only the other app owns.
    /// </summary>
    public static void Update(Action<AppSettings> change)
    {
        var settings = Load();
        change(settings);
        settings.Save();
    }

    public void Save()
    {
        try
        {
            SizeCalibration = CurrentSizeCalibration;
            Clamp();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

            // Write-then-move so a watcher never sees a half-written file.
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch
        {
            // Failed saves must never take the app down.
        }
    }

    private void Clamp()
    {
        RestScale = Math.Clamp(RestScale, 0.5f, 2.0f);
        MaxScale = Math.Clamp(MaxScale, 1.5f, 6.0f);
        Sensitivity = Math.Clamp(Sensitivity, 0.5f, 2.0f);
        LingerSeconds = Math.Clamp(LingerSeconds, 0.25f, 3.0f);
        ExcludedApps = (ExcludedApps ?? new List<string>())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(NormalizeApp)
            .Distinct()
            .ToList();
        if (!TryParseColor(FillColor, out _)) FillColor = "#000000";
        if (!TryParseColor(OutlineColor, out _)) OutlineColor = "#FFFFFF";
    }
}
