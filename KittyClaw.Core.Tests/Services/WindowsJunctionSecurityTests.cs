using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class WindowsJunctionSecurityTests
{
    [Theory]
    [InlineData("source", "target")]
    [InlineData("source` ! [deps] %COMSPEC%", "target` ! [deps] %COMSPEC%")]
    [InlineData("source&whoami&tail", "target&whoami&tail")]
    [InlineData("source%COMSPEC%", "target%COMSPEC%")]
    [InlineData("source [deps] ; $value '", "target [deps] ; $value '")]
    public void Junction_paths_are_literal_data_and_point_to_the_exact_source(string sourceName, string targetName)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDir();
        var source = Path.Combine(temp.Path, sourceName);
        var target = Path.Combine(temp.Path, targetName);
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "sentinel.txt"), "synthetic dependency");
        var previous = Environment.GetEnvironmentVariable("KITTYCLAW_JUNCTION_SOURCE");
        try
        {
            DurableWriteRouter.CreateDirectoryJunction(target, source);
            Assert.True((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0);
            Assert.Equal(Path.GetFullPath(source), new DirectoryInfo(target).ResolveLinkTarget(true)!.FullName,
                ignoreCase: true);
            Assert.Equal("synthetic dependency", File.ReadAllText(Path.Combine(target, "sentinel.txt")));
            Assert.Equal(previous, Environment.GetEnvironmentVariable("KITTYCLAW_JUNCTION_SOURCE"));
        }
        finally
        {
            if (Directory.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(target); // Remove the junction itself before deleting the fixture.
        }
    }

    [Fact]
    public void Existing_destination_is_preserved_when_junction_creation_fails()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDir();
        var source = Path.Combine(temp.Path, "source");
        var target = Path.Combine(temp.Path, "existing");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "sentinel.txt");
        File.WriteAllText(sentinel, "keep");
        Assert.Throws<IOException>(() => DurableWriteRouter.CreateDirectoryJunction(target, source));
        Assert.Equal("keep", File.ReadAllText(sentinel));
        Assert.Equal(0, (int)(File.GetAttributes(target) & FileAttributes.ReparsePoint));
    }
}