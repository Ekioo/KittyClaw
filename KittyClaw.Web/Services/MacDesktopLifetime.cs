using System.Diagnostics;

namespace KittyClaw.Web.Services;

/// <summary>Browser-based shell for experimental macOS bundles, without a native UI dependency.</summary>
internal sealed class MacDesktopLifetime : IDisposable
{
    private readonly FileStream? _instance;
    public bool IsFirstInstance => _instance is not null;

    public MacDesktopLifetime(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        try
        {
            // FileShare.None uses an OS lock on Unix and is released after a crash.
            // Keep the inode: deleting the file would let another process bypass the lock.
            _instance = new FileStream(Path.Combine(dataDirectory, "desktop.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { }
    }

    public void Attach(WebApplication app) => app.Lifetime.ApplicationStarted.Register(() =>
        OpenBrowser(string.Join(';', app.Urls), app.Logger));

    internal static string BrowserUrl(string urls)
    {
        // Never send the browser to a wildcard listener or the remote tunnel origin.
        foreach (var value in urls.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Uri.TryCreate(value.Replace("://*:", "://localhost:").Replace("://+:", "://localhost:"),
                    UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https" && uri.IsLoopback && string.IsNullOrEmpty(uri.UserInfo))
                return uri.AbsoluteUri;
        }
        return "http://localhost:5230/";
    }

    internal static void OpenBrowser(string urls, ILogger? logger)
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            start.ArgumentList.Add(BrowserUrl(urls));
            using var process = Process.Start(start);
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "Could not open KittyClaw in the browser");
        }
    }

    public void Dispose() => _instance?.Dispose();
}
