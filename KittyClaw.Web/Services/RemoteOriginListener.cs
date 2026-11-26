using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Configuration.Memory;
using System.Globalization;

namespace KittyClaw.Web.Services;

/// <summary>Adds the dedicated loopback endpoint without restarting the web host.</summary>
public sealed class RemoteOriginListener : IOptionsChangeTokenSource<HostFilteringOptions>, IDisposable
{
    private const string EndpointKey = "Endpoints:KittyClawTunnel:Url";
    private readonly IConfigurationRoot _configuration;
    private readonly MemoryConfigurationProvider _runtime;
    private readonly RemoteTunnelOptions _origin;
    private readonly string _proof = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly HttpClient _probe = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromMilliseconds(500) };
    private volatile bool _ready;
    private int _bindingAttempt;
    public bool IsReady => _ready;
    public IConfiguration Configuration => _configuration;
    public string Name => Options.DefaultName;
    public IChangeToken GetChangeToken() => _configuration.GetReloadToken();

    public RemoteOriginListener(IConfiguration kestrel, string urls, RemoteTunnelOptions origin, int networkPort)
    {
        _origin = origin;
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var addresses = urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (origin.Port == networkPort || addresses.Any(url =>
            Uri.TryCreate(url.Replace("*", "localhost").Replace("+", "localhost"), UriKind.Absolute, out var uri)
            && uri.Port == origin.Port)
            || kestrel.GetSection("Endpoints").GetChildren().Any(endpoint =>
                Uri.TryCreate(endpoint["Url"]?.Replace("*", "localhost").Replace("+", "localhost"), UriKind.Absolute, out var uri)
                && uri.Port == origin.Port))
            throw new InvalidOperationException("RemoteTunnel:Port must be distinct from every existing listener.");
        // Explicit Kestrel endpoints take precedence over UseUrls, just as before.
        if (!kestrel.GetSection("Endpoints").GetChildren().Any())
            for (var i = 0; i < addresses.Length; i++) settings[$"Endpoints:KittyClawLocal{i}:Url"] = addresses[i];
        if (kestrel.GetSection("Endpoints:KittyClawTunnel").Exists())
            throw new InvalidOperationException("The KittyClawTunnel endpoint name is reserved.");
        if (origin.Enabled) settings[EndpointKey] = $"http://127.0.0.1:{origin.Port}";
        _configuration = new ConfigurationBuilder().AddConfiguration(kestrel).AddInMemoryCollection(settings).Build();
        _runtime = _configuration.Providers.OfType<MemoryConfigurationProvider>().Single();
    }

    public async Task EnsureReadyAsync(CancellationToken ct)
    {
        await _commands.WaitAsync(ct);
        try
        {
            if (_ready) return;
            _runtime.Set(EndpointKey, $"http://127.0.0.1:{_origin.Port}");
            // Kestrel caches failed endpoint configurations. Change only this endpoint's
            // generation on retry so a released port can be bound on the next attempt.
            if (_bindingAttempt++ > 0) _runtime.Set("Endpoints:KittyClawTunnel:BindingAttempt",
                _bindingAttempt.ToString(CultureInfo.InvariantCulture));
            // Also refresh the exact host allowlist after first registration.
            _configuration.Reload();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                while (true)
                {
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_origin.Port}/.kittyclaw-origin");
                        request.Headers.Add("X-KittyClaw-Origin-Probe", _proof);
                        using var response = await _probe.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                        if (response.StatusCode == HttpStatusCode.NoContent
                            && response.Headers.TryGetValues("X-KittyClaw-Origin-Probe", out var proof)
                            && proof.SequenceEqual([_proof]))
                        { _ready = true; return; }
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !deadline.IsCancellationRequested) { }
                    await Task.Delay(50, deadline.Token);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new RemoteAccessException("RemoteOriginUnavailable"); }
        }
        finally { _commands.Release(); }
    }

    internal bool IsProbe(HttpContext context) => context.Connection.LocalPort == _origin.Port
        && context.Connection.RemoteIpAddress is { } peer && IPAddress.IsLoopback(peer)
        && context.Request.Path == "/.kittyclaw-origin" && HttpMethods.IsGet(context.Request.Method)
        && context.Request.Host.Host == "127.0.0.1"
        && context.Request.Headers["X-KittyClaw-Origin-Probe"] == _proof;

    internal void ReplyToProbe(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        context.Response.Headers["X-KittyClaw-Origin-Probe"] = _proof;
    }

    public void Dispose() { _probe.Dispose(); _commands.Dispose(); (_configuration as IDisposable)?.Dispose(); }
}
