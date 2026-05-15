using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KittyClaw.Core.Services;

/// <summary>
/// Sidecar metadata describing how a dashboard tile is generated and rendered.
/// Stored as <c>tile.yaml</c> inside the tile's folder: <c>.dashboard/&lt;slug&gt;/tile.yaml</c>.
/// A tile without a sidecar is a static tile (no auto-refresh, default rendering by extension).
/// </summary>
/// <param name="Template">Renderer to use (markdown, table, kpi, kpi-grid, progress, sparkline,
/// bar-chart, donut, gauge, status-grid, heatmap, leaderboard, image, mermaid). Required.</param>
/// <param name="Refresh">How often to re-run the pipeline, in seconds. 0 = static (never auto-refresh).</param>
/// <param name="Prompt">LLM instruction executed on each refresh. Empty for static tiles.</param>
/// <param name="Model">Optional Claude model override (null/empty = project default).</param>
/// <param name="Title">Optional custom title shown in the tile header. If null/empty,
/// the tile slug is used.</param>
public sealed record TileSidecar(
    string Template,
    int Refresh,
    string Prompt = "",
    string? Model = null,
    string? Title = null);

public static class TileSidecarSerializer
{
    private static readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer _serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    public static TileSidecar? TryParse(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return null;
        try
        {
            var raw = _deserializer.Deserialize<Dto>(yaml);
            if (raw is null || string.IsNullOrWhiteSpace(raw.Template)) return null;
            return new TileSidecar(
                raw.Template.Trim().ToLowerInvariant(),
                raw.Refresh,
                raw.Prompt ?? "",
                string.IsNullOrWhiteSpace(raw.Model) ? null : raw.Model,
                string.IsNullOrWhiteSpace(raw.Title) ? null : raw.Title);
        }
        catch
        {
            return null;
        }
    }

    public static string Serialize(TileSidecar sidecar)
    {
        var dto = new Dto
        {
            Template = sidecar.Template,
            Refresh = sidecar.Refresh,
            Prompt = sidecar.Prompt,
            Model = sidecar.Model ?? "",
            Title = sidecar.Title ?? "",
        };
        return _serializer.Serialize(dto);
    }

    private sealed class Dto
    {
        public string Template { get; set; } = "";
        public int Refresh { get; set; }
        public string Prompt { get; set; } = "";
        public string Model { get; set; } = "";
        public string Title { get; set; } = "";
    }
}
