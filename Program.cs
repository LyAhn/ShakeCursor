using Velopack;
using static ShakeCursor.NativeMethods;

namespace ShakeCursor;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Must run first: handles the installer's install/update/uninstall
        // callbacks and exits immediately when it is one.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => Autostart.Set(false))
            .Run();

        if (args.Length >= 2 && args[0] == "--watchdog" && int.TryParse(args[1], out int parentPid))
        {
            return Watchdog.Run(parentPid);
        }

        using var mutex = new Mutex(true, Watchdog.MutexName, out bool isNewInstance);
        if (!isNewInstance)
        {
            return 0;
        }

        ApplicationConfiguration.Initialize();

        using var appContext = new ShakeCursorAppContext(openSettings: args.Contains("--settings"));
        Application.Run(appContext);
        return 0;
    }
}

/// <summary>
/// Owns the tray icon and wires up lifetime management. There is no visible
/// main window — the tray icon is the app's UI. Settings live in a separate
/// WinUI 3 app that edits settings.json; this process watches the file and
/// applies changes live.
/// </summary>
internal sealed class ShakeCursorAppContext : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly CursorSwapper _swapper;
    private readonly ShakeDetector _shaker = new();
    private readonly PointerWatcher _watcher;
    private readonly NotifyIcon _tray;
    private readonly Updater _updater;
    private readonly ToolStripMenuItem _updateItem;
    private readonly ToolStripMenuItem _pauseItem;
    private bool _userPaused;
    private string? _updateStatusText;
    private Action? _balloonAction; // what clicking the current balloon does; null opens settings
    private readonly EventWaitHandle? _cleanExit;
    private readonly System.Windows.Forms.Timer _reloadDebounce = new() { Interval = 150 };
    private readonly FileSystemWatcher _settingsFile;

    public ShakeCursorAppContext(bool openSettings)
    {
        // The cursor is DPI-scaled from the system DPI (v1 assumes the primary
        // monitor's scale — mixed-DPI refinement comes later).
        float dpiScale = GetDpiForSystem() / 96f;

        _settings = AppSettings.Load();
        _reloadDebounce.Tick += (_, _) =>
        {
            _reloadDebounce.Stop();
            ReloadSettings();
        };
        _swapper = new CursorSwapper(dpiScale);
        _cleanExit = Watchdog.Spawn();
        _watcher = new PointerWatcher(_swapper, _shaker);

        ApplySettings();
        ApplyAutostart();

        // The settings app writes the file; reload on any change to it.
        string dir = Path.GetDirectoryName(AppSettings.FilePath)!;
        Directory.CreateDirectory(dir);
        _settingsFile = new FileSystemWatcher(dir, Path.GetFileName(AppSettings.FilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        // WinForms only installs its sync context once a control exists; the
        // old overlay window used to do that implicitly, so do it explicitly.
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        }
        var ui = SynchronizationContext.Current;
        _updater = new Updater(); // captures the UI sync context set above
        _updater.AutoCheck = _settings.AutoUpdate;
        FileSystemEventHandler onChange = (_, _) => ui?.Post(_ => { _reloadDebounce.Stop(); _reloadDebounce.Start(); }, null);
        _settingsFile.Changed += onChange;
        _settingsFile.Created += onChange;
        _settingsFile.Renamed += (_, _) => ui?.Post(_ => { _reloadDebounce.Stop(); _reloadDebounce.Start(); }, null);

        // Best-effort restore on any exit path so the user is never left
        // without a cursor. A hard kill is covered by the watchdog process.
        Application.ApplicationExit += (_, _) => _swapper.Restore();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _swapper.Restore();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Diagnostics.Log("Unhandled exception: " + e.ExceptionObject);
            _swapper.Restore();
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Settings...", (System.Drawing.Image?)null, (_, _) => ShowSettings());
        _pauseItem = new ToolStripMenuItem("Pause ShakeCursor", null, (_, _) => TogglePause());
        menu.Items.Add(_pauseItem);
        _updateItem = new ToolStripMenuItem("Check for updates", null, (_, _) => OnUpdateItemClicked());
        menu.Items.Add(_updateItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit ShakeCursor", (System.Drawing.Image?)null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Load(SystemInformation.SmallIconSize),
            Text = "ShakeCursor",
            ContextMenuStrip = menu,
            Visible = true,
        };

        _foregroundProc = OnForegroundChanged;
        _foregroundHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _foregroundProc, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);

        _updater.UpdateReady += OnUpdateReady;
        _updater.Status += OnUpdateStatus;
        _updater.UpdateAvailable += OnUpdateAvailable;
        _updater.Notice += OnUpdateNotice;
        _updater.Start();
        RefreshUpdateItem();
        ListenForSettingsRequests();
        PublishUpdateStatus();

        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_balloonAction is not null)
            {
                _balloonAction();
            }
            else
            {
                ShowSettings();
            }
        };
        _tray.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowSettings();
            }
        };

        _watcher.Start();
        UpdateExclusion();
        WarmSettingsFiles();

        if (!_settings.FirstRunShown)
        {
            _tray.ShowBalloonTip(
                8000,
                "ShakeCursor is running",
                "Shake your mouse to enlarge the pointer. Click here for settings.",
                ToolTipIcon.Info);
            _settings.FirstRunShown = true;
            AppSettings.Update(s => s.FirstRunShown = true);
        }

        if (openSettings)
        {
            ShowSettings();
        }
    }

    private void RefreshUpdateItem()
    {
        _updateItem.Text = _updater.PendingVersion is string v ? $"Restart to update to {v}"
            : _updater.AvailableVersion is string a ? $"Download version {a}"
            : "Check for updates";
    }

    private void OnUpdateReady(string version)
    {
        RefreshUpdateItem();
        PublishUpdateStatus();
        _balloonAction = ApplyUpdateAndExit;
        _tray.ShowBalloonTip(
            10000,
            "ShakeCursor update ready",
            $"Version {version} is downloaded. Click here to restart and update.",
            ToolTipIcon.Info);
    }

    private void OnUpdateAvailable(string version)
    {
        RefreshUpdateItem();
        PublishUpdateStatus();
        _balloonAction = OpenReleasePage;
        _tray.ShowBalloonTip(
            10000,
            "ShakeCursor update available",
            $"Version {version} is out. Click here to open the download page.",
            ToolTipIcon.Info);
    }

    private void OpenReleasePage()
    {
        if (_updater.AvailableUrl is string url)
        {
            _ = Task.Run(() => System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose());
        }
    }

    private void OnUpdateStatus(string? status)
    {
        _updateStatusText = status;
        if (status is null)
        {
            _updateItem.Enabled = true;
            RefreshUpdateItem();
        }
        else
        {
            // The menu item doubles as a progress readout.
            _updateItem.Enabled = false;
            _updateItem.Text = status;
        }
        RefreshTrayText();
        PublishUpdateStatus();
    }

    /// <summary>NotifyIcon.Text caps at 63 chars.</summary>
    private void RefreshTrayText()
    {
        string tip = "ShakeCursor" + (_userPaused ? " (paused)" : "")
            + (_updateStatusText is null ? "" : " - " + _updateStatusText);
        _tray.Text = tip.Length <= 63 ? tip : tip[..63];
    }

    /// <summary>Tells the settings window what the updater is doing.</summary>
    private void PublishUpdateStatus()
    {
        string text = _updateStatusText
            ?? (_updater.PendingVersion is string v ? $"Version {v} is ready to install."
                : _updater.AvailableVersion is string a ? $"Version {a} is available."
                : _updater.LastResult ?? "");
        UpdateBridge.Write(new UpdateStatus
        {
            Installed = _updater.IsInstalled,
            Busy = _updateStatusText is not null,
            Text = text,
            PendingVersion = _updater.PendingVersion,
            ReleaseUrl = _updater.AvailableUrl,
        });
    }

    /// <summary>Lets the settings window trigger a check or a restart-to-update.</summary>
    private void ListenForSettingsRequests()
    {
        var check = new EventWaitHandle(false, EventResetMode.AutoReset, UpdateBridge.CheckEvent);
        var apply = new EventWaitHandle(false, EventResetMode.AutoReset, UpdateBridge.ApplyEvent);
        var ui = SynchronizationContext.Current;

        new Thread(() =>
        {
            var handles = new WaitHandle[] { check, apply };
            while (true)
            {
                int which = WaitHandle.WaitAny(handles);
                if (which == 0)
                {
                    ui?.Post(_ => OnUpdateItemClicked(), null);
                }
                else
                {
                    ui?.Post(_ => ApplyUpdateAndExit(), null);
                }
            }
        })
        {
            Name = "SettingsRequests",
            IsBackground = true,
        }.Start();
    }

    private void TogglePause()
    {
        _userPaused = !_userPaused;
        _pauseItem.Checked = _userPaused;
        _pauseItem.Text = _userPaused ? "Resume ShakeCursor" : "Pause ShakeCursor";
        RefreshTrayText();
        UpdateExclusion();
    }

    private void OnUpdateNotice(string text)
    {
        _balloonAction = null;
        _tray.ShowBalloonTip(5000, "ShakeCursor", text, ToolTipIcon.Info);
    }

    private async void OnUpdateItemClicked()
    {
        if (_updater.PendingVersion is not null)
        {
            ApplyUpdateAndExit();
            return;
        }
        if (_updater.AvailableUrl is not null)
        {
            OpenReleasePage();
            return;
        }

        await _updater.CheckAsync(manual: true);
    }

    /// <summary>Hands off to the updater, then exits through the normal path (cursor restored).</summary>
    private void ApplyUpdateAndExit()
    {
        if (_updater.ApplyOnExit())
        {
            ExitThread();
        }
    }

    private void ApplySettings()
    {
        AppSettings.TryParseColor(_settings.FillColor, out var fill);
        AppSettings.TryParseColor(_settings.OutlineColor, out var outline);
        _swapper.Configure(_settings.RestScale, _settings.MaxScale, fill, outline, _settings.Shadow);
        _shaker.Sensitivity = _settings.Sensitivity;
        _shaker.LingerSeconds = _settings.LingerSeconds;
        _updater?.AutoCheck = _settings.AutoUpdate;
    }

    // Re-evaluates the excluded-apps list whenever the foreground window changes.
    private readonly NativeMethods.WinEventProc _foregroundProc;
    private readonly IntPtr _foregroundHook;

    private void OnForegroundChanged(
        IntPtr hHook, uint evt, IntPtr hwnd, int idObject, int idChild,
        uint dwEventThread, uint dwmsEventTime)
    {
        UpdateExclusion();
    }

    private bool _excludedActive;

    /// <summary>
    /// Hands the real arrow back while an excluded app is in front, and takes
    /// it over again afterwards. Runs on the UI thread only.
    /// </summary>
    private void UpdateExclusion()
    {
        bool excluded = false;
        if (_settings.ExcludedApps.Count > 0)
        {
            try
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
                if (pid != 0 && pid != Environment.ProcessId)
                {
                    using var process = System.Diagnostics.Process.GetProcessById((int)pid);
                    excluded = _settings.ExcludedApps.Contains(
                        AppSettings.NormalizeApp(process.ProcessName));
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                          or System.ComponentModel.Win32Exception)
            {
                // Window or process vanished between the event and the lookup.
            }
        }

        excluded |= _userPaused;
        if (excluded == _excludedActive)
        {
            return;
        }
        _excludedActive = excluded;

        if (excluded)
        {
            _watcher.Paused = true;
            _swapper.Suspend();
        }
        else
        {
            _swapper.Resume();
            _watcher.Paused = false;
        }
        Diagnostics.Log($"suspended={excluded} (paused={_userPaused})");
    }

    private void ReloadSettings()
    {
        var fresh = AppSettings.TryLoad();
        if (fresh is null)
        {
            return;
        }
        _settings.CopyFrom(fresh);
        Diagnostics.Log("settings reloaded");
        ApplySettings();
        ApplyAutostart();
        UpdateExclusion();
    }

    private void ApplyAutostart()
    {
        if (_settings.StartWithWindows is bool wanted && wanted != Autostart.IsEnabled())
        {
            Autostart.Set(wanted);
        }
    }

    /// <summary>
    /// Opens the WinUI settings app (it focuses the existing window if already
    /// open). Launched off the UI thread and without the shell: starting an
    /// unsigned exe can stall for seconds (SmartScreen / Defender scan), and
    /// doing that on the UI thread froze the tray menu until it finished.
    /// </summary>
    private static void ShowSettings()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "Settings", "ShakeCursor.Settings.exe");
        _ = Task.Run(() =>
        {
            try
            {
                using var process = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(exe)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(exe)!,
                    });
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Could not start settings app: " + ex.Message);
            }
        });
    }

    /// <summary>
    /// The settings app is a large self-contained WinUI app. Its first launch
    /// after a boot or an update is slow because Windows has to read and
    /// virus-scan all its files cold, so read them once in the background at
    /// low priority; the real launch then hits the cache.
    /// </summary>
    private static void WarmSettingsFiles()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "Settings");
        if (!Directory.Exists(dir))
        {
            return;
        }

        new Thread(() =>
        {
            try
            {
                Thread.Sleep(TimeSpan.FromSeconds(15)); // stay out of the way of startup
                var buffer = new byte[1 << 20];
                foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    using var stream = new FileStream(
                        file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                        bufferSize: 1, FileOptions.SequentialScan);
                    while (stream.Read(buffer, 0, buffer.Length) > 0)
                    {
                    }
                }
                Diagnostics.Log("settings files warmed");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort only.
            }
        })
        {
            Name = "SettingsWarmup",
            IsBackground = true,
            Priority = ThreadPriority.Lowest,
        }.Start();
    }

    /// <summary>Asks an open settings window to close so it doesn't outlive the tray app.</summary>
    private static void CloseSettingsWindow()
    {
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("ShakeCursor.Settings"))
        {
            try
            {
                if (process.CloseMainWindow() && !process.WaitForExit(3000))
                {
                    process.Kill();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already exiting.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    protected override void ExitThreadCore()
    {
        _updater.Dispose();
        _settingsFile.Dispose();
        _watcher.Stop();
        _swapper.Restore();
        _tray.Visible = false;
        _tray.Dispose();
        CloseSettingsWindow();
        _cleanExit?.Set();
        base.ExitThreadCore();
    }
}
