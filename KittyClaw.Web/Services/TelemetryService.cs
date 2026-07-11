using System.Net.Http.Json;
using System.Reflection;
using KittyClaw.Core.Services;

namespace KittyClaw.Web.Services;

/// <summary>
/// Sends one anonymous heartbeat per 24h to Umami Cloud so we can see how many
/// KittyClaw instances are alive and which versions they run (see README "Telemetry").
/// Payload: random instance id, app version, OS family — nothing else.
/// Always on (product decision); never registered in Development
/// (dotnet watch, QaRunner test instances).
/// </summary>
public class TelemetryService : BackgroundService
{
    private const string Endpoint = "https://cloud.umami.is/api/send";
    private const string WebsiteId = "aede6698-208c-4f67-a593-91ab3cc8e661";

    private readonly AppSettingsService _settings;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<TelemetryService> _logger;
    private readonly string _version;

    public TelemetryService(AppSettingsService settings, IHttpClientFactory httpFactory, ILogger<TelemetryService> logger)
    {
        _settings = settings;
        _httpFactory = httpFactory;
        _logger = logger;
        _version = ResolveCurrentVersion();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            do
            {
                await SendIfDueAsync(stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }

    private async Task SendIfDueAsync(CancellationToken ct)
    {
        var last = _settings.TelemetryLastSent;
        if (last.HasValue && DateTime.UtcNow - last.Value < TimeSpan.FromHours(24)) return;

        try
        {
            var client = _httpFactory.CreateClient();
            // Short timeout: a firewall that black-holes egress must never stall the host.
            client.Timeout = TimeSpan.FromSeconds(3);
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.UserAgent.ParseAdd(BrowserUserAgent());
            request.Content = JsonContent.Create(new
            {
                type = "event",
                payload = new
                {
                    website = WebsiteId,
                    hostname = "app.kittyclaw.local",
                    url = "/",
                    name = "heartbeat",
                    // Umami "distinct id": makes each instance a stable, unique
                    // identified visitor regardless of IP/UA hashing.
                    id = _settings.TelemetryInstanceId,
                    data = new
                    {
                        instanceId = _settings.TelemetryInstanceId,
                        version = _version,
                        os = OsFamily(),
                    },
                },
            });
            var response = await client.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                // Only mark success so transient failures retry on the next hourly tick.
                _settings.TelemetryLastSent = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Telemetry heartbeat failed");
        }
    }

    private static string ResolveCurrentVersion()
    {
        var info = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return VersionFormatter.Format(info);
    }

    private static string OsFamily() =>
        OperatingSystem.IsWindows() ? "Windows"
        : OperatingSystem.IsMacOS() ? "macOS"
        : OperatingSystem.IsLinux() ? "Linux"
        : "Other";

    // Umami silently discards events whose User-Agent looks like a bot, so the
    // heartbeat must present a plausible browser UA. Matching the real OS keeps
    // the dashboard's native OS breakdown truthful.
    private static string BrowserUserAgent()
    {
        var platform = OperatingSystem.IsMacOS() ? "Macintosh; Intel Mac OS X 10_15_7"
            : OperatingSystem.IsLinux() ? "X11; Linux x86_64"
            : "Windows NT 10.0; Win64; x64";
        return $"Mozilla/5.0 ({platform}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
    }
}
