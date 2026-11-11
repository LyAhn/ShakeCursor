using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace ShakeCursor.Settings;

public partial class App : Application
{
    private MainWindow? _window;
    private Mutex? _singleInstance;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Log(e.Exception.ToString());
    }

    private static void Log(string text)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "ShakeCursor.Settings.log"), text + Environment.NewLine);
        }
        catch
        {
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstance = new Mutex(true, @"Local\ShakeCursor.Settings.SingleInstance", out bool isNew);
        if (!isNew)
        {
            // Already open: bring that window forward instead of opening another.
            IntPtr existing = FindWindow(null, MainWindow.WindowTitle);
            if (existing != IntPtr.Zero)
            {
                ShowWindow(existing, 9 /* SW_RESTORE */);
                SetForegroundWindow(existing);
            }
            Environment.Exit(0);
            return;
        }

        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            throw;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
