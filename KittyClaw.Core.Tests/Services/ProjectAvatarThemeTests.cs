using KittyClaw.Core.Services;

namespace KittyClaw.Core.Tests.Services;

public class ProjectAvatarThemeTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("kittyclaw-avatar-").FullName;

    [Fact]
    public void New_and_legacy_settings_default_to_colorful_backgrounds()
    {
        Assert.Equal("B", new AppSettingsService(_directory).ProjectAvatarTheme);
        File.WriteAllText(Path.Combine(_directory, "settings.json"), "{\"Language\":\"fr\",\"McpEnabled\":true}");
        var settings = new AppSettingsService(_directory);
        Assert.Equal("B", settings.ProjectAvatarTheme);
        Assert.True(settings.McpEnabled);
    }

    [Fact]
    public void Selection_is_persisted_and_notifies_once_per_change()
    {
        var settings = new AppSettingsService(_directory);
        var notifications = 0;
        settings.OnProjectAvatarThemeChanged += () => notifications++;
        settings.ProjectAvatarTheme = "C";
        settings.ProjectAvatarTheme = "C";
        Assert.Equal(1, notifications);
        Assert.Equal("C", new AppSettingsService(_directory).ProjectAvatarTheme);
        settings.ProjectAvatarTheme = "unsupported";
        Assert.Equal("B", settings.ProjectAvatarTheme);
        Assert.Equal("B", new AppSettingsService(_directory).ProjectAvatarTheme);
    }

    [Fact]
    public void Invalid_persisted_theme_falls_back_to_B()
    {
        File.WriteAllText(Path.Combine(_directory, "settings.json"), "{\"ProjectAvatarTheme\":\"unknown\"}");
        Assert.Equal("B", new AppSettingsService(_directory).ProjectAvatarTheme);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("B")]
    [InlineData("C")]
    public void Every_palette_keeps_the_cat_and_dots_readable(string theme)
    {
        var palettes = Enumerable.Range(0, 256).Select(seed => ProjectAvatarColors.Select((byte)seed, theme)).Distinct().ToList();
        Assert.Equal(8, palettes.Count);
        foreach (var palette in palettes)
        {
            Assert.True(Contrast(palette.Background, palette.Cat) >= 3, $"{theme}: cat against background");
            Assert.True(Contrast(palette.Cat, palette.Dots) >= 3, $"{theme}: dots against cat");
        }
    }

    private static double Contrast(string a, string b)
    {
        var first = Luminance(a);
        var second = Luminance(b);
        return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }

    private static double Luminance(string hex)
    {
        double Channel(int start)
        {
            var value = Convert.ToInt32(hex.Substring(start, 2), 16) / 255d;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        return .2126 * Channel(1) + .7152 * Channel(3) + .0722 * Channel(5);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
