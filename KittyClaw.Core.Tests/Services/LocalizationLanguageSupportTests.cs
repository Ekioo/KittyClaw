using System.Text.Json;
using System.Text.RegularExpressions;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Tests.Services;

public class LocalizationLanguageSupportTests
{
    private static readonly string[] SupportedLanguages = ["en", "fr", "es", "de", "it", "pt-BR", "ja"];
    private static readonly Regex PlaceholderPattern = new(@"\{\d+(?::[^}]*)?\}");

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "KittyClaw.Core", "Localization")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    [Fact]
    public void EveryBundle_HasMatchingKeysAndFormatPlaceholders_InAllSupportedLanguages()
    {
        var localization = Path.Combine(RepoRoot(), "KittyClaw.Core", "Localization");
        foreach (var englishPath in Directory.GetFiles(localization, "*.en.json"))
        {
            var english = Read(englishPath);
            foreach (var language in SupportedLanguages.Skip(1))
            {
                var translatedPath = englishPath.Replace(".en.json", $".{language}.json");
                Assert.True(File.Exists(translatedPath), $"Missing localization bundle: {Path.GetFileName(translatedPath)}");
                var translated = Read(translatedPath);
                Assert.Equal(english.Keys.Order(), translated.Keys.Order());
                foreach (var key in english.Keys)
                    Assert.Equal(Placeholders(english[key]), Placeholders(translated[key]));
            }
        }
    }

    [Theory]
    [InlineData("es", "Idioma")]
    [InlineData("de", "Sprache")]
    [InlineData("it", "Lingua")]
    [InlineData("pt-BR", "Idioma")]
    [InlineData("ja", "言語")]
    public void LocalizationService_LoadsNewLanguages(string language, string expected)
    {
        using var data = new TemporaryDirectory();
        var settings = new AppSettingsService(data.Path) { Language = language };
        var localization = new LocalizationService(settings);
        Assert.Equal(expected, localization["Language"]);
    }

    [Fact]
    public void UnknownLanguage_FallsBackToEnglish()
    {
        using var data = new TemporaryDirectory();
        var settings = new AppSettingsService(data.Path) { Language = "unsupported" };
        var localization = new LocalizationService(settings);
        Assert.Equal("Language", localization["Language"]);
    }

    private static Dictionary<string, string> Read(string path) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;

    private static string[] Placeholders(string value) =>
        PlaceholderPattern.Matches(value).Select(match => match.Value).Order().ToArray();

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"kittyclaw-loc-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
