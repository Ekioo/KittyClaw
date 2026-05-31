using System.IO;
using Xunit;

namespace KittyClaw.Core.Tests.Web;

public class Opus48ModelSupportTests
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

    private static string ActionEditor() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "ActionEditor.razor"));

    private static string Dashboard() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "Pages", "Dashboard.razor"));

    private static string ProjectSettings() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "Pages", "ProjectSettings.razor"));

    private static string LocalizationEn() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Core", "Localization", "ProjectSettings.en.json"));

    private static string LocalizationFr() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Core", "Localization", "ProjectSettings.fr.json"));

    // Case 1: ActionEditor DefaultModels contains opus-4-8

    [Fact]
    public void ActionEditor_DefaultModels_ContainsOpus48()
    {
        Assert.Contains("\"claude-opus-4-8\"", ActionEditor());
    }

    [Fact]
    public void ActionEditor_DefaultModels_ContainsOpus48_1m()
    {
        Assert.Contains("\"claude-opus-4-8-1m\"", ActionEditor());
    }

    // Case 2: Dashboard AvailableModels contains opus-4-8

    [Fact]
    public void Dashboard_AvailableModels_ContainsOpus48()
    {
        Assert.Contains("\"claude-opus-4-8\"", Dashboard());
    }

    [Fact]
    public void Dashboard_AvailableModels_ContainsOpus48_1m()
    {
        Assert.Contains("\"claude-opus-4-8-1m\"", Dashboard());
    }

    // Case 3: ProjectSettings fallback select has opus-4-8 option

    [Fact]
    public void ProjectSettings_FallbackSelect_ContainsOpus48Option()
    {
        Assert.Contains("claude-opus-4-8", ProjectSettings());
    }

    // Case edge: localization keys present in both languages

    [Fact]
    public void Localization_En_ContainsFallbackOpus48Key()
    {
        Assert.Contains("FallbackOpus48", LocalizationEn());
    }

    [Fact]
    public void Localization_Fr_ContainsFallbackOpus48Key()
    {
        Assert.Contains("FallbackOpus48", LocalizationFr());
    }
}
