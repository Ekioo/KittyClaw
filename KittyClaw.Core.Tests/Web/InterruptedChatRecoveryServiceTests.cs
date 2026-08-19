using KittyClaw.Web.Services;

namespace KittyClaw.Core.Tests.Web;

public class InterruptedChatRecoveryServiceTests
{
    [Theory]
    [InlineData("http://localhost:5230", "http://localhost:5230/")]
    [InlineData("http://0.0.0.0:61234", "http://localhost:61234/")]
    [InlineData("http://[::]:61234", "http://localhost:61234/")]
    public void ResolveLoopbackAddress_UsesReachableLocalHost(string address, string expected)
    {
        var result = InterruptedChatRecoveryService.ResolveLoopbackAddress([address]);

        Assert.Equal(expected, result?.AbsoluteUri);
    }

    [Fact]
    public void RecoveryService_IsRegisteredAndUsesTheResumeContract()
    {
        var root = FindRepoRoot();
        var program = File.ReadAllText(Path.Combine(root, "KittyClaw.Web", "Program.cs"));
        var service = File.ReadAllText(Path.Combine(root, "KittyClaw.Web", "Services", "InterruptedChatRecoveryService.cs"));

        Assert.Contains("AddHostedService<KittyClaw.Web.Services.InterruptedChatRecoveryService>()", program);
        Assert.Contains("runs.InterruptedChats()", service);
        Assert.Contains("resumeInterrupted = true", service);
        Assert.Contains("lifetime.ApplicationStarted", service);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KittyClaw.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
