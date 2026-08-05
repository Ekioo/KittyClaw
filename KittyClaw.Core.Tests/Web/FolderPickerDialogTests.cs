using System.Text.Json;

namespace KittyClaw.Core.Tests.Web;

public sealed class FolderPickerDialogTests
{
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KittyClaw.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static string Read(params string[] path) =>
        File.ReadAllText(Path.Combine([RepoRoot(), .. path]));

    [Fact]
    public void FolderPicker_IsIntegratedAndCrossPlatform()
    {
        var picker = Read("KittyClaw.Web", "Components", "FolderPickerDialog.razor");

        Assert.Contains("DriveInfo.GetDrives()", picker);
        Assert.Contains("Environment.SpecialFolder.UserProfile", picker);
        Assert.Contains("DirectoryInfo", picker);
        Assert.Contains("OperatingSystem.IsWindows()", picker);
        Assert.DoesNotContain("WindowsFolderPicker", picker);
    }

    [Theory]
    [InlineData("KittyClaw.Web/Components/ProjectCreation.razor")]
    [InlineData("KittyClaw.Web/Components/Pages/ProjectSettings.razor")]
    public void WorkspaceEditors_UseIntegratedPicker(string relativePath)
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Contains("<FolderPickerDialog", source);
        Assert.DoesNotContain("/api/browse/folder", source);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("it")]
    public void FolderPickerTranslations_AreComplete(string language)
    {
        var json = Read("KittyClaw.Core", "Localization", $"ProjectSettings.{language}.json");
        var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;

        foreach (var key in new[]
                 {
                     "FolderPickerTitle",
                     "FolderPickerPath",
                     "FolderPickerRoots",
                     "FolderPickerParent",
                     "FolderPickerSelect",
                     "FolderPickerError",
                 })
        {
            Assert.True(translations.ContainsKey(key), $"Missing {key} in {language}.");
        }
    }
}
