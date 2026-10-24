using System.Globalization;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public class DefaultLanguageDetectionTests
{
    [Fact]
    public void New_settings_use_supported_operating_system_language()
    {
        using var dir = new TempDir();

        var settings = new AppSettingsService(dir.Path, CultureInfo.GetCultureInfo("fr-FR"));

        Assert.Equal("fr", settings.Language);
    }

    [Fact]
    public void Unsupported_operating_system_language_falls_back_to_English()
    {
        using var dir = new TempDir();

        var settings = new AppSettingsService(dir.Path, CultureInfo.GetCultureInfo("ko-KR"));

        Assert.Equal("en", settings.Language);
    }

    [Theory]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("pt-PT", "pt-BR")]
    [InlineData("ja-JP", "ja")]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("zh-SG", "zh-CN")]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("zh-Hans-CN", "zh-CN")]
    [InlineData("zh-Hans-SG", "zh-CN")]
    [InlineData("zh-TW", "en")]
    [InlineData("zh-HK", "en")]
    [InlineData("zh-MO", "en")]
    [InlineData("zh-Hant", "en")]
    [InlineData("zh-Hant-TW", "en")]
    public void New_settings_map_new_operating_system_languages(string culture, string expected)
    {
        using var dir = new TempDir();

        var settings = new AppSettingsService(dir.Path, CultureInfo.GetCultureInfo(culture));

        Assert.Equal(expected, settings.Language);
    }

    [Theory]
    [InlineData("it")]
    [InlineData("zh-CN")]
    public void Existing_language_setting_takes_precedence_over_operating_system_language(string language)
    {
        using var dir = new TempDir();
        new AppSettingsService(dir.Path, CultureInfo.GetCultureInfo("de-DE")) { Language = language };

        var settings = new AppSettingsService(dir.Path, CultureInfo.GetCultureInfo("fr-FR"));

        Assert.Equal(language, settings.Language);
    }
}
