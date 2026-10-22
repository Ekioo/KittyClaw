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
        if (!_ownsMutex) OpenBrowser("http://localhost:5230");
    }

    public void Attach(WebApplication app)
    {
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var url = app.Urls.FirstOrDefault() ?? "http://localhost:5230";
            _thread = new Thread(() =>
            {
                _context = new Forms.ApplicationContext();
                using var dispatcher = new Forms.Control();
                _ = dispatcher.Handle;
                _dispatcher = dispatcher;
                using var menu = new Forms.ContextMenuStrip();
                menu.Items.Add("Open KittyClaw", null, (_, _) => OpenBrowser(url));
                menu.Items.Add("Quit KittyClaw", null, (_, _) => app.Lifetime.StopApplication());
                using var icon = new Forms.NotifyIcon
                {
                    Text = "KittyClaw",
                    Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)
                        ?? System.Drawing.SystemIcons.Application,
                    ContextMenuStrip = menu,
                    Visible = true,
                };
                icon.DoubleClick += (_, _) => OpenBrowser(url);
                OpenBrowser(url);
                Forms.Application.Run(_context);
                icon.Visible = false;
                _dispatcher = null;
            }) { IsBackground = true, Name = "KittyClaw tray" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        });
    }

    private static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Trace.TraceWarning("Could not open KittyClaw: {0}", ex.Message); }
    }

    public void Dispose()
    {
        if (_dispatcher is { IsDisposed: false } dispatcher)
        {
            try { dispatcher.BeginInvoke(() => _context?.ExitThread()); }
            catch (InvalidOperationException) { }
        }
        _thread?.Join(TimeSpan.FromSeconds(2));
        if (_ownsMutex) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
#endif
