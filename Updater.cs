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

    /// <summary>
    /// Live status for the menu and tooltip ("Checking...", "Downloading 1.0.4 - 37%").
    /// Null means the check finished and the UI should show its idle state.
    /// </summary>
    public event Action<string?>? Status;

    /// <summary>A one-off message worth a balloon tip; only raised for manual checks.</summary>
    public event Action<string>? Notice;

    public void Start()
    {
        if (!IsInstalled)
        {
            Diagnostics.Log("updater: not a Velopack install, update checks disabled");
            return;
        }

        _pending = _manager.UpdatePendingRestart;
        _timer = new System.Threading.Timer(
            _ => _ = CheckAsync(manual: false), null, FirstCheckDelay, CheckInterval);
    }

    private void Post(Action action) => _ui?.Post(_ => action(), null);

    /// <summary>
    /// Looks for an update and downloads it, reporting progress through
    /// <see cref="Status"/>. A manual check also says what happened via
    /// <see cref="Notice"/>, so it never looks like nothing is going on.
    /// </summary>
    public async Task CheckAsync(bool manual)
    {
        if (!IsInstalled || _pending is not null || !await _busy.WaitAsync(0))
        {
            return;
        }

        string? result = null;
        try
        {
            Post(() => Status?.Invoke("Checking for updates..."));
            if (manual)
            {
                Post(() => Notice?.Invoke("Checking for updates..."));
            }

            UpdateInfo? update = await _manager.CheckForUpdatesAsync();
            if (update is null)
            {
                result = "You're on the latest version.";
                return;
            }

            string version = update.TargetFullRelease.Version.ToString();
            Diagnostics.Log($"updater: downloading {version}");
            Post(() => Status?.Invoke($"Downloading {version}... 0%"));
            if (manual)
            {
                Post(() => Notice?.Invoke($"Version {version} found. Downloading..."));
            }

            int lastPercent = -1;
            await _manager.DownloadUpdatesAsync(update, percent =>
            {
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    Post(() => Status?.Invoke($"Downloading {version}... {percent}%"));
                }
            });
            _pending = update.TargetFullRelease;
            Post(() => UpdateReady?.Invoke(version));
        }
        catch (Exception ex)
        {
            // Offline, rate-limited or a bad feed: try again at the next interval.
            Diagnostics.Log("updater: check failed: " + ex.Message);
            result = "Couldn't check for updates. Try again later.";
        }
        finally
        {
            _busy.Release();
            Post(() => Status?.Invoke(null));
            if (manual && result is not null)
            {
                Post(() => Notice?.Invoke(result));
            }
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
