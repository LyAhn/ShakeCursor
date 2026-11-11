using Velopack;
using Velopack.Sources;

namespace ShakeCursor;

/// <summary>
/// Checks GitHub Releases for a newer version, downloads it in the background
/// and hands it to Velopack's updater on request. Does nothing when the app was
/// not installed by the Velopack installer (a dev build or the portable zip),
/// so those keep working exactly as before.
/// </summary>
internal sealed class Updater : IDisposable
{
    private const string RepoUrl = "https://github.com/LyAhn/ShakeCursor";

    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);

    private readonly UpdateManager _manager = new(new GithubSource(RepoUrl, null, false));
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private System.Threading.Timer? _timer;
    private VelopackAsset? _pending;

    /// <summary>True when this copy can update itself.</summary>
    public bool IsInstalled => _manager.IsInstalled;

    /// <summary>The downloaded version waiting for a restart, or null.</summary>
    public string? PendingVersion => _pending?.Version.ToString();

    /// <summary>Raised on the UI thread when a new version has finished downloading.</summary>
    public event Action<string>? UpdateReady;

    public void Start()
    {
        if (!IsInstalled)
        {
            Diagnostics.Log("updater: not a Velopack install, update checks disabled");
            return;
        }

        _pending = _manager.UpdatePendingRestart;
        _timer = new System.Threading.Timer(
            _ => _ = CheckAsync(), null, FirstCheckDelay, CheckInterval);
    }

    /// <summary>
    /// Looks for an update and downloads it. Returns a short status line for
    /// manual checks; background checks ignore it.
    /// </summary>
    public async Task<string> CheckAsync()
    {
        if (!IsInstalled)
        {
            return "Updates are only available in the installed version.";
        }
        if (!await _busy.WaitAsync(0))
        {
            return "Already checking for updates.";
        }

        try
        {
            if (_pending is not null)
            {
                return $"Version {_pending.Version} is ready. Restart to update.";
            }

            UpdateInfo? update = await _manager.CheckForUpdatesAsync();
            if (update is null)
            {
                return "You're on the latest version.";
            }

            Diagnostics.Log($"updater: downloading {update.TargetFullRelease.Version}");
            await _manager.DownloadUpdatesAsync(update);
            _pending = update.TargetFullRelease;

            string version = _pending.Version.ToString();
            _ui?.Post(_ => UpdateReady?.Invoke(version), null);
            return $"Version {version} is ready. Restart to update.";
        }
        catch (Exception ex)
        {
            // Offline, rate-limited or a bad feed: try again at the next interval.
            Diagnostics.Log("updater: check failed: " + ex.Message);
            return "Couldn't check for updates.";
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>
    /// Tells Velopack to apply the downloaded update once this process exits.
    /// The caller must then exit normally so the cursor is restored first.
    /// </summary>
    public bool ApplyOnExit()
    {
        if (_pending is null)
        {
            return false;
        }

        _manager.WaitExitThenApplyUpdates(_pending, silent: true, restart: true);
        return true;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _busy.Dispose();
    }
}
