using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace ShakeCursor.Settings;

/// <summary>
/// The WinUI 3 settings window. It edits %APPDATA%\ShakeCursor\settings.json;
/// the tray app watches that file and applies changes live. The preview runs
/// its own <see cref="ShakeDetector"/> on the real pointer, so shaking anywhere
/// animates the sample arrow with the values being edited.
/// </summary>
public sealed partial class MainWindow : Window
{
    public const string WindowTitle = "ShakeCursor Settings";

    // The arrow scales about its own centre (see ScaleTransform in the XAML),
    // so park that centre in the middle of the 176 px round stage.
    private const double StageCentre = 88;
    private const double ArrowCentreX = 5.6; // 14 units * 0.8 / 2
    private const double ArrowCentreY = 9.6; // 24 units * 0.8 / 2

    private readonly ShakeDetector _preview = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ObservableCollection<string> _apps = new();
    private readonly DispatcherQueueTimer _saveTimer;
    private readonly DispatcherQueueTimer _updateTimer;
    private DateTime _updateRequestedAt = DateTime.MinValue;
    private readonly UIElement[] _pages;
    private double _lastTime;
    private string _fill = "#000000";
    private string _outline = "#FFFFFF";
    private bool _ready;

    public MainWindow()
    {
        InitializeComponent();
        Title = WindowTitle;
        _pages = [PageSize, PageShake, PageAppearance, PageApps, PageGeneral];

        ConfigureWindow();

        var settings = AppSettings.Load();
        RestSlider.Value = settings.RestScale;
        MaxSlider.Value = settings.MaxScale;
        SensSlider.Value = settings.Sensitivity;
        LingerSlider.Value = settings.LingerSeconds;
        _fill = settings.FillColor;
        _outline = settings.OutlineColor;
        ShadowToggle.IsOn = settings.Shadow;
        AutostartToggle.IsOn = IsAutostartEnabled();
        ShortcutToggle.IsOn = DesktopShortcut.Exists();
        AutoUpdateToggle.IsOn = settings.AutoUpdate;
        UpdateCard.Header = "ShakeCursor " + AppVersion();
        foreach (string app in settings.ExcludedApps)
        {
            _apps.Add(app);
        }
        AppList.ItemsSource = _apps;
        _apps.CollectionChanged += (_, _) => OnChanged();

        BuildArrow();
        RefreshAll();

        _saveTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(150);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => Save();

        foreach (var slider in new[] { RestSlider, MaxSlider, SensSlider, LingerSlider })
        {
            slider.ValueChanged += (_, _) => OnChanged();
        }
        ShadowToggle.Toggled += (_, _) => OnChanged();
        AutostartToggle.Toggled += (_, _) => AppSettings.Update(s => s.StartWithWindows = AutostartToggle.IsOn);
        ShortcutToggle.Toggled += (_, _) => DesktopShortcut.Set(ShortcutToggle.IsOn);
        AutoUpdateToggle.Toggled += (_, _) => AppSettings.Update(s => s.AutoUpdate = AutoUpdateToggle.IsOn);
        UpdateButton.Click += (_, _) => OnUpdateButtonClicked();

        _updateTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _updateTimer.Interval = TimeSpan.FromMilliseconds(500);
        _updateTimer.Tick += (_, _) => RefreshUpdateUi();
        _updateTimer.Start();
        RefreshUpdateUi();
        SwapButton.Click += (_, _) =>
        {
            (_fill, _outline) = (_outline, _fill);
            OnChanged();
        };
        FillPicker.ColorChanged += (_, e) => PickerChanged(ref _fill, e.NewColor);
        OutlinePicker.ColorChanged += (_, e) => PickerChanged(ref _outline, e.NewColor);
        ResetButton.Click += (_, _) => ResetToDefaults();

        Nav.SelectedItem = Nav.MenuItems[0];

        CompositionTarget.Rendering += OnRender;
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRender;
            _updateTimer.Stop();
            Save();
        };

        _lastTime = _clock.Elapsed.TotalSeconds;
        _ready = true;
    }

    // --- Window chrome -------------------------------------------------------

    private void ConfigureWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        IntPtr hwnd = WindowNative.GetWindowHandle(this);
        AppWindow appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));

        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "ShakeCursor.ico");
        if (File.Exists(icon))
        {
            appWindow.SetIcon(icon);
        }

        appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        double scale = GetDpiForWindow(hwnd) / 96.0;
        appWindow.Resize(new Windows.Graphics.SizeInt32((int)(1000 * scale), (int)(780 * scale)));

        var work = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        appWindow.Move(new Windows.Graphics.PointInt32(
            work.X + (work.Width - appWindow.Size.Width) / 2,
            work.Y + (work.Height - appWindow.Size.Height) / 2));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    // --- Navigation ----------------------------------------------------------

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }
        PageSize.Visibility = Show(tag == "Size");
        PageShake.Visibility = Show(tag == "Shake");
        PageAppearance.Visibility = Show(tag == "Appearance");
        PageApps.Visibility = Show(tag == "Apps");
        PageGeneral.Visibility = Show(tag == "General");
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    // --- Preview -------------------------------------------------------------

    // A PointCollection can belong to only one shape, so build one per polygon.
    private static PointCollection ArrowPoints(double dx, double dy)
    {
        var points = new PointCollection();
        foreach (var (x, y) in ArrowGeometry.Points)
        {
            points.Add(new Point(x * ArrowGeometry.UnitsToPx + dx, y * ArrowGeometry.UnitsToPx + dy));
        }
        return points;
    }

    private void BuildArrow()
    {
        ArrowFill.Points = ArrowPoints(0, 0);
        ArrowOutline.Points = ArrowPoints(0, 0);
        ArrowShadow.Points = ArrowPoints(2.5, 3.5);
        Canvas.SetLeft(ArrowHost, StageCentre - ArrowCentreX);
        Canvas.SetTop(ArrowHost, StageCentre - ArrowCentreY);
    }

    private void OnRender(object? sender, object e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = now - _lastTime;
        _lastTime = now;

        GetCursorPos(out POINT pos);
        float progress = _preview.Update(new System.Drawing.Point(pos.X, pos.Y), dt);

        double scale = RestSlider.Value + (MaxSlider.Value - RestSlider.Value) * Math.Clamp(progress, 0f, 1f);
        ArrowScale.ScaleX = ArrowScale.ScaleY = scale;
        ScaleReadout.Text = $"{scale:0.0#}×";
    }

    // --- Values --------------------------------------------------------------

    private static SolidColorBrush Brush(string hex)
    {
        AppSettings.TryParseColor(hex, out var c);
        return new SolidColorBrush(Color.FromArgb(255, c.R, c.G, c.B));
    }

    private void PickerChanged(ref string target, Color color)
    {
        target = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        OnChanged();
    }

    private void RefreshAll()
    {
        RestValue.Text = $"{RestSlider.Value:0.0#}×";
        MaxValue.Text = $"{MaxSlider.Value:0.0#}×";
        SensValue.Text = $"{SensSlider.Value:0.0#}×";
        LingerValue.Text = $"{LingerSlider.Value:0.0#} s";

        _preview.Sensitivity = (float)SensSlider.Value;
        _preview.LingerSeconds = (float)LingerSlider.Value;

        FillHex.Text = _fill;
        OutlineHex.Text = _outline;
        FillSwatch.Background = Brush(_fill);
        OutlineSwatch.Background = Brush(_outline);
        ArrowFill.Fill = Brush(_fill);
        ArrowOutline.Stroke = Brush(_outline);
        ArrowShadow.Visibility = ShadowToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;

        // Keep the pickers in step without re-firing ColorChanged back into us.
        SetPicker(FillPicker, _fill);
        SetPicker(OutlinePicker, _outline);

        AppsEmpty.Visibility = _apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void SetPicker(ColorPicker picker, string hex)
    {
        AppSettings.TryParseColor(hex, out var c);
        var color = Color.FromArgb(255, c.R, c.G, c.B);
        if (picker.Color != color)
        {
            picker.Color = color;
        }
    }

    private void OnChanged()
    {
        RefreshAll();
        if (_ready)
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }
    }

    private void Save()
    {
        _saveTimer.Stop();
        AppSettings.Update(s =>
        {
            s.RestScale = (float)RestSlider.Value;
            s.MaxScale = (float)MaxSlider.Value;
            s.Sensitivity = (float)SensSlider.Value;
            s.LingerSeconds = (float)LingerSlider.Value;
            s.FillColor = _fill;
            s.OutlineColor = _outline;
            s.Shadow = ShadowToggle.IsOn;
            s.ExcludedApps = _apps.ToList();
        });
    }

    private void ResetToDefaults()
    {
        var d = new AppSettings();
        RestSlider.Value = d.RestScale;
        MaxSlider.Value = d.MaxScale;
        SensSlider.Value = d.Sensitivity;
        LingerSlider.Value = d.LingerSeconds;
        _fill = d.FillColor;
        _outline = d.OutlineColor;
        ShadowToggle.IsOn = d.Shadow;
        OnChanged();
    }

    // --- Updates (the tray app owns Velopack; we show its status and send requests) ---

    private static string AppVersion()
    {
        string? info = typeof(MainWindow).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        return info is null ? "" : info.Split('+')[0];
    }

    private void OnUpdateButtonClicked()
    {
        var status = UpdateBridge.Read();
        if (status?.ReleaseUrl is string url)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            return;
        }
        bool sent = UpdateBridge.Signal(
            status?.PendingVersion is not null ? UpdateBridge.ApplyEvent : UpdateBridge.CheckEvent);
        if (sent)
        {
            _updateRequestedAt = DateTime.UtcNow;
        }
        RefreshUpdateUi();
    }

    private void RefreshUpdateUi()
    {
        if (!UpdateBridge.TrayListening())
        {
            UpdateCard.Description = "ShakeCursor isn't running, so it can't check for updates.";
            UpdateButton.Content = "Check for updates";
            UpdateButton.IsEnabled = false;
            return;
        }

        var status = UpdateBridge.Read();
        if (status is null)
        {
            UpdateCard.Description = "";
            UpdateButton.Content = "Check for updates";
            UpdateButton.IsEnabled = true;
            return;
        }

        // The status file lags a click by a moment; bridge the gap so the button never looks dead.
        bool justClicked = (DateTime.UtcNow - _updateRequestedAt).TotalSeconds < 1.5 && !status.Busy;
        if (justClicked && status.PendingVersion is null)
        {
            UpdateCard.Description = "Checking for updates...";
            UpdateButton.Content = "Checking...";
            UpdateButton.IsEnabled = false;
            return;
        }

        UpdateCard.Description = status.Text.Length > 0 ? status.Text
            : status.Installed ? "" : "This copy updates manually. Checks GitHub for newer releases.";
        UpdateButton.IsEnabled = !status.Busy;
        UpdateButton.Content = status.Busy ? "Working..."
            : status.PendingVersion is not null ? "Restart to update"
            : status.ReleaseUrl is not null ? "Open download page"
            : "Check for updates";
    }

    // --- Autostart (the tray app owns the write; we only read the registry) ---

    private static bool IsAutostartEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue("ShakeCursor") is string;
        }
        catch
        {
            return false;
        }
    }

    // --- Excluded apps ---------------------------------------------------------

    private void AddAppMenu_Opening(object? sender, object e)
    {
        AddAppMenu.Items.Clear();

        var running = Process.GetProcesses()
            .Where(p => p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Length > 0
                        && p.Id != Environment.ProcessId)
            .GroupBy(p => AppSettings.NormalizeApp(p.ProcessName))
            .Where(g => !_apps.Contains(g.Key))
            .OrderBy(g => g.Key)
            .Take(25);

        foreach (var group in running)
        {
            string title = group.First().MainWindowTitle;
            if (title.Length > 40)
            {
                title = title[..40] + "…";
            }
            string name = group.Key;
            var item = new MenuFlyoutItem { Text = $"{name}  —  {title}" };
            item.Click += (_, _) => AddApp(name);
            AddAppMenu.Items.Add(item);
        }

        if (AddAppMenu.Items.Count > 0)
        {
            AddAppMenu.Items.Add(new MenuFlyoutSeparator());
        }
        var browse = new MenuFlyoutItem { Text = "Browse…" };
        browse.Click += async (_, _) => await BrowseForApp();
        AddAppMenu.Items.Add(browse);
    }

    private async Task BrowseForApp()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".exe");
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            AddApp(file.Path);
        }
    }

    private void AddApp(string name)
    {
        string app = AppSettings.NormalizeApp(name);
        if (!_apps.Contains(app))
        {
            _apps.Add(app);
        }
    }

    private void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string app })
        {
            _apps.Remove(app);
        }
    }
}
