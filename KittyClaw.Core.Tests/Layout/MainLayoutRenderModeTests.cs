using System.IO;
using System.Linq;
using Xunit;

namespace KittyClaw.Core.Tests.Layout;

public class MainLayoutRenderModeTests
{
    private static string WebFile(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "KittyClaw.Web", "Components", "Layout", "MainLayout.razor")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName, "KittyClaw.Web" }.Concat(relative).ToArray());
    }

    [Fact]
    public void EscapeKeyPlumbing_IsCorrectlyWired()
    {
        // Three invariants that together guarantee:
        //  (a) the app does not crash with the Body/RenderFragment serialization error
        //      when something tries to render MainLayout as an interactive root, and
        //  (b) the Escape-key JS interop actually initializes so popups close.
        var mainLayout = File.ReadAllText(WebFile("Components", "Layout", "MainLayout.razor"));
        var escapeHost = File.ReadAllText(WebFile("Components", "EscapeKeyHost.razor"));

        // (1) MainLayout must NOT declare a render mode. LayoutComponentBase receives
        //     Body as a non-serializable RenderFragment parameter; declaring an
        //     interactive render mode throws InvalidOperationException at request time.
        Assert.DoesNotContain("@rendermode", mainLayout);

        // (2) EscapeKeyHost must declare InteractiveServer so OnAfterRenderAsync fires
        //     and escapeStack.init(dotNetRef) runs — without this, every Escape keydown
        //     is silently dropped in escape-stack.js (state.dotNet stays null).
        Assert.Contains("@rendermode InteractiveServer", escapeHost);

        // (3) EscapeKeyHost must be mounted from MainLayout so the JS interop boots
        //     on every page, not just on pages that opt in explicitly.
        Assert.Contains("<EscapeKeyHost", mainLayout);
    }
}
