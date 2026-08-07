namespace KittyClaw.Core.Tests.Helpers;

/// <summary>
/// Loads the application stylesheet by reading and concatenating the split CSS files
/// in load order, matching the &lt;link&gt; tags in App.razor.
/// </summary>
internal static class AppCssHelper
{
    private static readonly string[] CssFiles = [
        "css/base.css",
        "css/shared.css",
        "css/board.css",
        "css/ticket-card.css",
        "css/ticket-panel.css",
        "css/agent-run.css",
        "css/settings.css",
        "css/automations.css",
        "css/chat.css",
        "css/dashboard.css",
        "css/home.css",
        "css/settings-editor.css",
    ];

    internal static string ReadAll()
    {
        var root = FindRepoRoot();
        return ReadAll(root);
    }

    internal static string ReadAll(string repoRoot)
    {
        var wwwroot = Path.Combine(repoRoot, "KittyClaw.Web", "wwwroot");
        return string.Concat(CssFiles.Select(f => File.ReadAllText(Path.Combine(wwwroot, f))));
    }

    internal static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "KittyClaw.slnx")) ||
                File.Exists(Path.Combine(dir, "KittyClaw.sln")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new DirectoryNotFoundException("Repository root (KittyClaw.slnx) not found.");
    }
}
