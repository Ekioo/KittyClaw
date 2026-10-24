using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace KittyClaw.Web.Services;

public interface ICloudflaredRuntime
{
    Task RunAsync(string tunnelToken, Action<bool> readiness, CancellationToken cancellationToken);
}

/// <summary>Downloads a pinned upstream asset, verifies it and supervises a child connector.</summary>
public sealed class CloudflaredRuntime(RemoteAccessStore store) : ICloudflaredRuntime
{
    internal const string Version = "2026.10.3";
    private const long MaximumAssetBytes = 128 * 1024 * 1024;
    internal sealed record Asset(string Name, string Sha256);
    internal static Asset CurrentAsset()
    {
        var arch = RuntimeInformation.OSArchitecture;
        if (OperatingSystem.IsWindows() && arch == Architecture.X64)
            return new("cloudflared-windows-amd64.exe", "76b2805253d0d6ffacb092a7acfbee99e7af6917835c0c5410e23f3ef5052da2");
        if (OperatingSystem.IsLinux()) return arch switch
        {
            Architecture.X64 => new("cloudflared-linux-amd64", "053b80b0a23031a038fdbf3c073f59f82b8f1f152aa67971d7383ffafc0dbab0"),
            Architecture.Arm64 => new("cloudflared-linux-arm64", "89297ebfb9e8c99c0dc013a1140601453cf3798d8306c5591651f4a3f5210be8"),
            _ => throw new RemoteAccessException("RemotePlatformUnsupported")
        };
        if (OperatingSystem.IsMacOS()) return arch switch
        {
            Architecture.X64 => new("cloudflared-darwin-amd64.tgz", "81b7b85f2376815c4f59b1aa323887b62e16ce8b67555c2e1577dc23f2a82000"),
            Architecture.Arm64 => new("cloudflared-darwin-arm64.tgz", "1165d14b6f6360a3b8c283d7d1100024d6ec7cd9bc9e32519e96150645f3ec55"),
            _ => throw new RemoteAccessException("RemotePlatformUnsupported")
        };
        throw new RemoteAccessException("RemotePlatformUnsupported");
    }

    internal static async Task VerifyAsync(string path, string expected, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var digest = await SHA256.HashDataAsync(stream, ct);
        if (!CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(expected)))
            throw new RemoteAccessException("RemoteBinaryInvalid");
    }
    private async Task<string> InstallAsync(CancellationToken ct)
    {
        var asset = CurrentAsset();
        var directory = Path.Combine(store.DataDirectory, "cloudflared", Version);
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var cached = Path.Combine(directory, asset.Name + ".asset");
        if (!File.Exists(cached))
        {
            var temporary = cached + ".download";
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
                using var response = await http.GetAsync($"https://github.com/cloudflare/cloudflared/releases/download/{Version}/{asset.Name}", HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaximumAssetBytes) throw new RemoteAccessException("RemoteBinaryInvalid");
                await using (var destination = File.Create(temporary))
                await using (var source = await response.Content.ReadAsStreamAsync(ct))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int length;
                    while ((length = await source.ReadAsync(buffer, ct)) > 0)
                    {
                        if ((total += length) > MaximumAssetBytes) throw new RemoteAccessException("RemoteBinaryInvalid");
                        await destination.WriteAsync(buffer.AsMemory(0, length), ct);
                    }
                }
                await VerifyAsync(temporary, asset.Sha256, ct);
                File.Move(temporary, cached, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        try { await VerifyAsync(cached, asset.Sha256, ct); }
        catch (RemoteAccessException) { File.Delete(cached); throw; }
        var executable = Path.Combine(directory, OperatingSystem.IsWindows() ? "cloudflared.exe" : "cloudflared");
        var staged = executable + ".new";
        try
        {
            if (asset.Name.EndsWith(".tgz", StringComparison.Ordinal))
            {
                await using var compressed = File.OpenRead(cached);
                await using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
                using var tar = new TarReader(gzip);
                var found = false;
                while (await tar.GetNextEntryAsync(cancellationToken: ct) is { } entry)
                {
                    if (entry.Name is not ("cloudflared" or "./cloudflared")) continue;
                    if (found || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                        || entry.Length > MaximumAssetBytes || entry.DataStream is null) throw new RemoteAccessException("RemoteBinaryInvalid");
                    await using var output = File.Create(staged);
                    await entry.DataStream.CopyToAsync(output, ct);
                    found = true;
                }
                if (!found) throw new RemoteAccessException("RemoteBinaryInvalid");
            }
            else File.Copy(cached, staged, true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(staged, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            File.Move(staged, executable, true);
            return executable;
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }

    internal static ProcessStartInfo StartInfo(string executable, string token, int metricsPort)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "tunnel", "--no-autoupdate", "--metrics", $"127.0.0.1:{metricsPort}", "run" })
            info.ArgumentList.Add(argument);
        // Supported by cloudflared. The secret never appears in command arguments,
        // log messages or a plaintext token file.
        info.Environment["TUNNEL_TOKEN"] = token;
        return info;
    }

    public async Task RunAsync(string tunnelToken, Action<bool> readiness, CancellationToken cancellationToken)
    {
        var executable = await InstallAsync(cancellationToken);
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var metricsPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var process = new Process { StartInfo = StartInfo(executable, tunnelToken, metricsPort) };
        process.Start();
        process.StartInfo.Environment.Remove("TUNNEL_TOKEN");
        void Kill() { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
        using var registration = cancellationToken.Register(Kill);
        // Upstream diagnostics can contain sensitive values. Drain without logging.
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            using var probe = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
            while (!process.HasExited)
            {
                var ready = false;
                try
                {
                    using var response = await probe.GetAsync($"http://127.0.0.1:{metricsPort}/ready", cancellationToken);
                    ready = response.StatusCode == HttpStatusCode.OK;
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                readiness(ready);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
        finally
        {
            readiness(false);
            Kill();
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
