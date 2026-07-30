using System.IO;
using System.Text.Json;
using Xunit;

namespace KittyClaw.Core.Tests.Web;

/// <summary>
/// KittyClaw is a local-only tool whose HTTP API is unauthenticated and can dispatch
/// agents: Host-header filtering is the standard defense against DNS rebinding (a
/// malicious page re-pointing its own domain at 127.0.0.1 to drive the local API
/// from the victim's browser). AllowedHosts must therefore stay pinned to loopback
/// names — never the template default "*".
/// </summary>
public class HostFilteringTests
{
    private static string RepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "KittyClaw.sln"))
                               && !File.Exists(Path.Combine(dir, "KittyClaw.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static string AllowedHosts()
    {
        var path = Path.Combine(RepoRoot(), "KittyClaw.Web", "appsettings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("AllowedHosts").GetString() ?? "";
    }

    [Fact]
    public void AllowedHosts_IsNotWildcard()
    {
        Assert.NotEqual("*", AllowedHosts());
        Assert.DoesNotContain("*", AllowedHosts());
    }

    [Fact]
    public void AllowedHosts_CoversLoopbackNames()
    {
        // "localhost" for the normal UI/API path, plus both loopback literals so
        // curl/scripts hitting 127.0.0.1 or [::1] keep working.
        var hosts = AllowedHosts().Split(';');
        Assert.Contains("localhost", hosts);
        Assert.Contains("127.0.0.1", hosts);
        Assert.Contains("[::1]", hosts);
    }
}
