using KittyClaw.Web.Services;

namespace KittyClaw.Core.Tests.Services;

public class MacDesktopLifetimeTests
{
    [Fact]
    public void RelaunchCannotOwnSameDataDirectory_UntilFirstInstanceExits()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kittyclaw-mac-lock-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var first = new MacDesktopLifetime(directory))
            {
                Assert.True(first.IsFirstInstance);
                using var second = new MacDesktopLifetime(directory);
                Assert.False(second.IsFirstInstance);
                using var independent = new MacDesktopLifetime(Path.Combine(directory, "other-data"));
                Assert.True(independent.IsFirstInstance);
            }
            using var afterExit = new MacDesktopLifetime(directory);
            Assert.True(afterExit.IsFirstInstance);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("http://localhost:5232", "http://localhost:5232/")]
    [InlineData("https://remote.example:5234;http://127.0.0.1:5232", "http://127.0.0.1:5232/")]
    [InlineData("http://*:5232", "http://localhost:5232/")]
    [InlineData("http://+:5232", "http://localhost:5232/")]
    [InlineData("file://localhost/path;https://localhost:5232", "https://localhost:5232/")]
    [InlineData("http://user:password@localhost:9999", "http://localhost:5230/")]
    [InlineData("https://remote.example", "http://localhost:5230/")]
    public void BrowserUsesOnlyLocalHttpListener(string listeners, string expected) =>
        Assert.Equal(expected, MacDesktopLifetime.BrowserUrl(listeners));
}
