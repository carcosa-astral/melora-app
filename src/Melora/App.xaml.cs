using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace Melora;

public partial class App : Application
{
    Mutex? _instance;
    EventWaitHandle? _showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        // One Melora per folder: launching it again brings the running one back from the tray.
        var id = "Melora-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.BaseDir.ToLowerInvariant())))[..16];
        _instance = new Mutex(true, id, out var isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, id + "-show");
        if (!isFirst)
        {
            AllowSetForegroundWindow(ASFW_ANY); // let the running copy come to the front
            _showSignal.Set();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        new Thread(() =>
        {
            while (_showSignal.WaitOne()) Dispatcher.BeginInvoke(window.ShowFromTray);
        }) { IsBackground = true, Name = "Melora single-instance" }.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }

    const int ASFW_ANY = -1;

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);

    static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write($"Unexpected error: {e.Exception}");
        MessageDialog.Inform(Current.MainWindow, "Something went wrong", e.Exception.Message);
        e.Handled = true;
    }
}
