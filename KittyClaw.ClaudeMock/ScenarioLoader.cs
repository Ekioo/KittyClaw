using System.Reflection;

namespace KittyClaw.ClaudeMock;

internal sealed class ScenarioLoader
{
    private readonly string? _externalDir;

    public ScenarioLoader(string? externalDir)
    {
        _externalDir = externalDir;
    }

    public string[]? Load(string name)
    {
        if (_externalDir is not null)
        {
            var path = Path.Combine(_externalDir, name + ".ndjson");
            if (File.Exists(path)) return File.ReadAllLines(path);
        }

        var asm = Assembly.GetExecutingAssembly();
        var resName = $"KittyClaw.ClaudeMock.Scenarios.{name}.ndjson";
        using var stream = asm.GetManifestResourceStream(resName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        return content.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
    }
}
