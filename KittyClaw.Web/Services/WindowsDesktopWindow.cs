#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Forms = System.Windows.Forms;

namespace KittyClaw.Web.Services;

/// <summary>Hosts the local UI while the server and agents keep running in the tray.</summary>
internal sealed class WindowsDesktopWindow : Forms.Form
{
    private readonly WebView2 _view = new() { Dock = Forms.DockStyle.Fill };
    private readonly Uri _origin;
    private readonly string _profile;
    private readonly Action<Exception> _onFailure;

    public WindowsDesktopWindow(string url, string profile, Action<Exception> onFailure)
    {
        _origin = new Uri(url);
        _profile = profile;
        _onFailure = onFailure;
        Text = "KittyClaw";
        FormBorderStyle = Forms.FormBorderStyle.None;
        BackColor = System.Drawing.Color.FromArgb(5, 5, 10);
        ForeColor = System.Drawing.Color.FromArgb(228, 228, 240);
        Width = 1440;
        Height = 960;
        MinimumSize = new System.Drawing.Size(800, 600);
        StartPosition = Forms.FormStartPosition.CenterScreen;
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        Controls.Add(_view);
        Padding = new Forms.Padding(5);
        Shown += InitializeView;
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == Forms.CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    private void ToggleMaximize() => WindowState = WindowState == Forms.FormWindowState.Maximized
        ? Forms.FormWindowState.Normal : Forms.FormWindowState.Maximized;

    protected override Forms.CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            // Keep native resize, system menu, taskbar and Win+Arrow behavior.
            parameters.Style |= 0x00CF0000;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var dark = 1;
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        var rounded = 2;
        DwmSetWindowAttribute(Handle, 33, ref rounded, sizeof(int));
    }

    protected override void WndProc(ref Forms.Message message)
    {
        if (message.Msg == 0x0024) // Keep a maximized borderless window inside the working area.
        {
            base.WndProc(ref message);
            var info = Marshal.PtrToStructure<MinMaxInfo>(message.LParam);
            var screen = Forms.Screen.FromHandle(Handle);
            info.MaxPosition = new System.Drawing.Point(screen.WorkingArea.Left - screen.Bounds.Left,
                screen.WorkingArea.Top - screen.Bounds.Top);
            info.MaxSize = screen.WorkingArea.Size;
            Marshal.StructureToPtr(info, message.LParam, false);
            return;
        }
        if (message.Msg == 0x0083 && message.WParam != 0) // Remove native non-client chrome.
        {
            message.Result = 0;
            return;
        }
        if (message.Msg == 0x00A3 && message.WParam == 2)
        {
            ToggleMaximize();
            return;
        }
        if (message.Msg == 0x0084 && WindowState == Forms.FormWindowState.Normal)
        {
            var packed = message.LParam.ToInt64();
            var point = PointToClient(new System.Drawing.Point((short)packed, (short)(packed >> 16)));
            var grip = Math.Max(5, 6 * DeviceDpi / 96);
            var left = point.X < grip;
            var right = point.X >= ClientSize.Width - grip;
            var top = point.Y < grip;
            var bottom = point.Y >= ClientSize.Height - grip;
            var hit = top ? (left ? 13 : right ? 14 : 12)
                : bottom ? (left ? 16 : right ? 17 : 15) : left ? 10 : right ? 11 : 0;
            if (hit != 0) { message.Result = hit; return; }
        }
        base.WndProc(ref message);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint handle, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public System.Drawing.Point Reserved;
        public System.Drawing.Size MaxSize;
        public System.Drawing.Point MaxPosition;
        public System.Drawing.Point MinTrackSize;
        public System.Drawing.Point MaxTrackSize;
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == Forms.FormWindowState.Minimized)
            WindowState = Forms.FormWindowState.Normal;
        Activate();
    }

    private async void InitializeView(object? sender, EventArgs e)
    {
        // Shown fires once; retain the control and its session when hidden.
        Shown -= InitializeView;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _profile);
            if (IsDisposed) return;
            await _view.EnsureCoreWebView2Async(environment);
            if (IsDisposed) return;
            _view.CoreWebView2.Settings.AreHostObjectsAllowed = false;
            _view.CoreWebView2.Settings.IsNonClientRegionSupportEnabled = true;
            _view.DefaultBackgroundColor = BackColor;
            _view.CoreWebView2.WebMessageReceived += (_, args) =>
            {
                if (!IsLocal(args.Source)) return;
                string command;
                try { command = args.TryGetWebMessageAsString(); }
                catch (ArgumentException) { return; }
                switch (command)
                {
                    case "window:minimize": WindowState = Forms.FormWindowState.Minimized; break;
                    case "window:maximize": ToggleMaximize(); break;
                    case "window:close": Hide(); break;
                }
            };
            using var scriptStream = typeof(WindowsDesktopWindow).Assembly.GetManifestResourceStream(
                "KittyClaw.Web.Services.WindowsDesktopShell.js")!;
            using var scriptReader = new StreamReader(scriptStream);
            await _view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(await scriptReader.ReadToEndAsync());
            _view.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (IsLocal(args.Uri)) return;
                args.Cancel = true;
                OpenExternal(args.Uri);
            };
            _view.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (IsLocal(args.Uri)) _view.CoreWebView2.Navigate(args.Uri);
                else OpenExternal(args.Uri);
            };
            _view.CoreWebView2.Navigate(_origin.AbsoluteUri);
        }
        catch (Exception exception)
        {
            if (!IsDisposed) _onFailure(exception);
        }
    }

    private bool IsLocal(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == _origin.Scheme && uri.Host == _origin.Host && uri.Port == _origin.Port;

    private static void OpenExternal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "mailto")) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { Trace.TraceWarning("Could not open external link: {0}", ex.Message); }
    }
}
#endif
