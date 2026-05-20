using System.IO;
using Xunit;

namespace KittyClaw.Core.Tests.Layout;

public class MainLayoutRenderModeTests
{
    private static string MainLayoutPath()
    {
        // Walk up from the test assembly to find the solution root, then locate the file.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "KittyClaw.Web", "Components", "Layout", "MainLayout.razor")))
            dir = dir.Parent;

        Assert.NotNull(dir); // guard: solution root not found
        return Path.Combine(dir!.FullName, "KittyClaw.Web", "Components", "Layout", "MainLayout.razor");
    }

    [Fact]
    public void MainLayout_HasInteractiveServerRenderMode()
    {
        var path = MainLayoutPath();
        var content = File.ReadAllText(path);
        Assert.Contains("@rendermode InteractiveServer", content);
    }
}
