namespace KittyClaw.Core.Tests.Web;

public sealed class BoardCreateTicketDialogObservabilityTests
{
    [Fact]
    public void ImagePasteInitializationFailureIsLoggedWithoutSensitiveContext()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "KittyClaw.Web", "Components", "BoardCreateTicketDialog.razor"));

        Assert.Contains("@inject ILogger<BoardCreateTicketDialog> Logger", source);
        Assert.Contains("catch (Exception ex)", source);
        Assert.Contains("Logger.LogWarning(ex, \"Image paste initialization failed\")", source);
        Assert.DoesNotContain("catch { }", source);
        Assert.DoesNotContain("{Image", source);
        Assert.DoesNotContain("{Content", source);
        Assert.DoesNotContain("{Response", source);
        Assert.DoesNotContain("{Identifier", source);
        Assert.DoesNotContain("{Secret", source);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KittyClaw.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
