using System.IO;
using System.Text.Json;
using Xunit;

namespace KittyClaw.Core.Tests.Web;

/// <summary>
/// global.json pins the .NET SDK band so every machine (dev, CI, contributors) builds
/// with the same toolchain: no accidental preview SDK, reproducible builds, while
/// latestPatch keeps monthly SDK security patches flowing in automatically.
/// </summary>
public class SdkPinningTests
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

    [Fact]
    public void GlobalJson_PinsSdkWithoutPrerelease()
    {
        var path = Path.Combine(RepoRoot(), "global.json");
        Assert.True(File.Exists(path), "global.json must exist at the repo root.");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var sdk = doc.RootElement.GetProperty("sdk");
        Assert.False(string.IsNullOrWhiteSpace(sdk.GetProperty("version").GetString()));
        Assert.Equal("latestPatch", sdk.GetProperty("rollForward").GetString());
        Assert.False(sdk.GetProperty("allowPrerelease").GetBoolean());
    }
}
