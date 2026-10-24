#if WINDOWS
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Forms = System.Windows.Forms;

namespace KittyClaw.Web.Services;

/// <summary>Desktop shell for installer builds; never used by dotnet watch or the QA host.</summary>
internal sealed class WindowsDesktopLifetime : IDisposable
{
    private readonly Mutex _mutex;
    private readonly bool _ownsMutex;
    private Forms.ApplicationContext? _context;
    private Forms.Control? _dispatcher;
    private Thread? _thread;
    private readonly EventWaitHandle _openRequested;
    private readonly string _profile;
    private RegisteredWaitHandle? _openRegistration;
    public bool IsFirstInstance => _ownsMutex;

    public static void ConfirmInstallation() => Forms.MessageBox.Show(
        "KittyClaw has been installed.\n\nShortcuts are available on your desktop and in the Start menu.\n\nClick OK to start KittyClaw. Once running, its icon appears in the notification area near the clock.",
        "KittyClaw installation complete", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);

    public void ReportStartupFailure(Exception exception)
    {
        var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ekioo.KittyClaw", "logs", "startup-error.log");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, $"{DateTimeOffset.Now:O}\n{exception}\n\n");
        }
        catch (Exception logError) { Trace.TraceWarning("Could not write startup log: {0}", logError.Message); }

        var addressInUse = false;
        for (Exception? cause = exception; cause is not null; cause = cause.InnerException)
            if (cause is System.Net.Sockets.SocketException socket &&
                socket.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
                addressInUse = true;
        var message = addressInUse
            ? "KittyClaw could not start because its port is already in use. Another KittyClaw installation may already be running.\n\nClose the other instance, then launch KittyClaw from its desktop shortcut."
            : exception is Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException
            ? "KittyClaw requires Microsoft Edge WebView2 Runtime. Run KittyClaw Setup to install it, or install the Evergreen WebView2 Runtime from https://developer.microsoft.com/microsoft-edge/webview2/."
            : "KittyClaw could not start. Please check the startup log for details.";
        Forms.MessageBox.Show($"{message}\n\nStartup log: {logPath}", "KittyClaw could not start",
            Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
        Environment.ExitCode = 1;
    }

    public WindowsDesktopLifetime(string dataDir)
    {
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())));
        _mutex = new Mutex(false, $"Local\\KittyClaw-{identity}");
        try { _ownsMutex = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsMutex = true; }
        _profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ekioo.KittyClaw", "WebView2", identity);
        _openRequested = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\KittyClaw-Open-{identity}");
        if (!_ownsMutex) _openRequested.Set();
    }

    public void Attach(WebApplication app)
    {
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var url = app.Urls.FirstOrDefault() ?? "http://localhost:5230";
            _thread = new Thread(() =>
            {
                Forms.Application.EnableVisualStyles();
                Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
                _context = new Forms.ApplicationContext();
                using var dispatcher = new Forms.Control();
                _ = dispatcher.Handle;
                _dispatcher = dispatcher;
                using var window = new WindowsDesktopWindow(url, _profile, exception =>
                {
                    ReportStartupFailure(exception);
                    app.Lifetime.StopApplication();
                });
                _openRegistration = ThreadPool.RegisterWaitForSingleObject(_openRequested, (_, _) =>
                {
                    try { dispatcher.BeginInvoke(window.ShowAndActivate); }
                    catch (InvalidOperationException) { }
                }, null, Timeout.Infinite, false);
                using var menu = new Forms.ContextMenuStrip();
                menu.Items.Add("Open KittyClaw", null, (_, _) => window.ShowAndActivate());
                var checkUpdates = menu.Items.Add("Check for updates");
                menu.Items.Add(new Forms.ToolStripSeparator());
                menu.Items.Add("Quit KittyClaw", null, (_, _) => app.Lifetime.StopApplication());
                using var icon = new Forms.NotifyIcon
                {
                    Text = "KittyClaw",
                    Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)
                        ?? System.Drawing.SystemIcons.Application,
                    ContextMenuStrip = menu,
                    Visible = true,
                };
                icon.DoubleClick += (_, _) => window.ShowAndActivate();
                icon.BalloonTipClicked += (_, _) => window.ShowAndActivate();
                checkUpdates.Click += async (_, _) =>
                {
                    checkUpdates.Enabled = false;
                    checkUpdates.Text = "Checking for updates…";
                    try
                    {
                        var updates = app.Services.GetRequiredService<InstalledUpdateService>();
                        var succeeded = await updates.CheckAsync();
                        if (app.Lifetime.ApplicationStopping.IsCancellationRequested) return;
                        var message = updates.IsReady
                            ? $"Version {updates.AvailableVersion} is ready. Open KittyClaw to confirm installation and restart."
                            : updates.IsBusy ? "An update is already downloading in the background."
                            : succeeded ? "KittyClaw is up to date."
                            : "Updates could not be checked or downloaded. Please try again later.";
                        icon.ShowBalloonTip(5000, "KittyClaw updates", message,
                            succeeded || updates.IsBusy || updates.IsReady ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceWarning("Could not check for updates from tray: {0}", ex.Message);
                        if (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
                            icon.ShowBalloonTip(5000, "KittyClaw updates", "Updates could not be checked. Please try again later.", Forms.ToolTipIcon.Warning);
                    }
                    finally
                    {
                        if (!checkUpdates.IsDisposed)
                        {
                            checkUpdates.Text = "Check for updates";
                            checkUpdates.Enabled = true;
                        }
                    }
                };
                window.ShowAndActivate();
                Forms.Application.Run(_context);
                _openRegistration.Unregister(null);
                icon.Visible = false;
                _dispatcher = null;
            }) { IsBackground = true, Name = "KittyClaw tray" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        });
    }

    public void Dispose()
    {
        if (_dispatcher is { IsDisposed: false } dispatcher)
        {
            try { dispatcher.BeginInvoke(() => _context?.ExitThread()); }
            catch (InvalidOperationException) { }
        }
        _thread?.Join(TimeSpan.FromSeconds(2));
        _openRequested.Dispose();
        if (_ownsMutex) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
#endif
