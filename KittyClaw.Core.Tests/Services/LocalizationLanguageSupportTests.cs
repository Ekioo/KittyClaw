using System.Text.Json;
using System.Text.RegularExpressions;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Tests.Services;

public class LocalizationLanguageSupportTests
{
    private static readonly string[] SupportedLanguages = ["en", "fr", "es", "de", "it", "pt-BR", "ja", "zh-CN"];
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
    [InlineData("zh-CN", "语言")]
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

    [Fact]
    public void Desktop_strings_follow_language_changes_and_fall_back_to_English()
    {
        using var data = new TemporaryDirectory();
        var settings = new AppSettingsService(data.Path) { Language = "en" };
        var localization = new LocalizationService(settings);
        var changes = 0;
        localization.OnLanguageChanged += () => changes++;

        Assert.Equal("Open KittyClaw", localization["TrayOpen"]);
        settings.Language = "fr";
        Assert.Equal("Ouvrir KittyClaw", localization["TrayOpen"]);
        Assert.Contains("1.2.3", localization.Get("TrayUpdateReady", "1.2.3"));
        settings.Language = "zh-CN";
        Assert.Equal("打开 KittyClaw", localization["TrayOpen"]);
        Assert.Contains("1.2.3", localization.Get("TrayUpdateReady", "1.2.3"));
        settings.Language = "unsupported";
        Assert.Equal("Check for updates", localization["TrayCheckUpdates"]);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void SimplifiedChinese_preserves_named_variables_and_has_no_empty_translations()
    {
        var variables = new Regex(@"\{(?:\d+|[A-Za-z_]\w*)(?::[^{}]*)?\}");
        var folder = Path.Combine(RepoRoot(), "KittyClaw.Core", "Localization");
        foreach (var englishPath in Directory.GetFiles(folder, "*.en.json"))
        {
            var translated = Read(englishPath.Replace(".en.json", ".zh-CN.json"));
            foreach (var (key, value) in Read(englishPath))
            {
                Assert.False(string.IsNullOrWhiteSpace(translated[key]), key);
                Assert.Equal(value.Count(ch => ch == '\n'), translated[key].Count(ch => ch == '\n'));
                Assert.Equal(
                    variables.Matches(value).Select(match => match.Value).Order(),
                    variables.Matches(translated[key]).Select(match => match.Value).Order());
            }
        }
    }

    [Fact]
    public void Settings_offer_every_supported_language()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "Pages", "GlobalSettings.razor"));
        foreach (var language in SupportedLanguages)
            Assert.Contains($"<option value=\"{language}\">", source);

        using var data = new TemporaryDirectory();
        var localization = new LocalizationService(new AppSettingsService(data.Path) { Language = "zh-CN" });
        Assert.Equal("简体中文", localization["LangZhCn"]);
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
