using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Web;
using Markdig;

namespace KittyClaw.Core.Services;

/// <summary>
/// Renders a tile's raw file content to HTML based on the template id.
/// All output is HTML-encoded at boundaries; the result is meant to be inserted as MarkupString.
/// Charts are SVG inline — no client-side dependencies (mermaid is the exception, handled in JS).
/// </summary>
public static class TileRenderer
{
    // Soft-line breaks become <br> so agents can output multi-line content (haikus, lists,
    // ASCII art) without having to remember Markdown's two-space line-break trick.
    private static readonly MarkdownPipeline _md =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().UseSoftlineBreakAsHardlineBreak().Build();

    private static readonly string[] _palette =
    [
        "#22c55e", "#3b82f6", "#f59e0b", "#ef4444", "#a855f7",
        "#06b6d4", "#ec4899", "#84cc16", "#f97316", "#14b8a6",
    ];

    /// <summary>
    /// Render <paramref name="content"/> for the given <paramref name="template"/>.
    /// On parse error, falls back to a &lt;pre&gt; raw block so the user always sees something.
    /// </summary>
    public static string Render(string template, string content, string fileName)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "<span class=\"tile-empty\">—</span>";

        try
        {
            return template switch
            {
                TileTemplate.Markdown    => RenderMarkdown(content),
                TileTemplate.Table       => RenderTable(content),
                TileTemplate.Kpi         => RenderKpi(content),
                TileTemplate.KpiGrid     => RenderKpiGrid(content),
                TileTemplate.Progress    => RenderProgress(content),
                TileTemplate.Sparkline   => RenderSparkline(content),
                TileTemplate.BarChart    => RenderBarChart(content),
                TileTemplate.Donut       => RenderDonut(content),
                TileTemplate.Gauge       => RenderGauge(content),
                TileTemplate.StatusGrid  => RenderStatusGrid(content),
                TileTemplate.Heatmap     => RenderHeatmap(content),
                TileTemplate.Leaderboard => RenderLeaderboard(content),
                TileTemplate.Timeline    => RenderTimeline(content),
                TileTemplate.Mermaid     => RenderMermaid(content),
                _                        => RenderByExtension(content, fileName),
            };
        }
        catch
        {
            return Raw(content);
        }
    }

    /// <summary>Fallback when no template is set: render based on file extension (markdown/json/raw).</summary>
    public static string RenderByExtension(string content, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".md"   => RenderMarkdown(content),
            ".json" => RenderJsonAuto(content),
            _       => Raw(content),
        };
    }

    // ── Markdown ─────────────────────────────────────────────────────────────

    private static string RenderMarkdown(string content) =>
        $"<div class=\"tile-markdown\">{Markdown.ToHtml(content, _md)}</div>";

    // ── Table ────────────────────────────────────────────────────────────────

    private static string RenderTable(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            return "<span class=\"tile-empty\">[]</span>";

        var first = root[0];
        if (first.ValueKind != JsonValueKind.Object) return Raw(json);

        var cols = first.EnumerateObject().Select(p => p.Name).ToList();
        var sb = new StringBuilder();
        sb.Append("<div class=\"tile-table-wrap\"><table class=\"tile-table\"><thead><tr>");
        foreach (var c in cols) sb.Append($"<th>{Esc(c)}</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var row in root.EnumerateArray())
        {
            sb.Append("<tr>");
            foreach (var c in cols)
            {
                var v = row.TryGetProperty(c, out var p) ? Stringify(p) : "";
                sb.Append($"<td>{Esc(v)}</td>");
            }
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></div>");
        return sb.ToString();
    }

    // ── KPI ──────────────────────────────────────────────────────────────────

    private static string RenderKpi(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return RenderKpiObject(doc.RootElement, large: true);
    }

    private static string RenderKpiGrid(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array) return Raw(json);
        var sb = new StringBuilder("<div class=\"tile-kpi-grid\">");
        foreach (var item in root.EnumerateArray())
            sb.Append(RenderKpiObject(item, large: false));
        sb.Append("</div>");
        return sb.ToString();
    }

    private static string RenderKpiObject(JsonElement obj, bool large)
    {
        if (obj.ValueKind != JsonValueKind.Object) return "";
        var value = obj.TryGetProperty("value", out var v) ? Stringify(v) : "—";
        var label = obj.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
        var unit  = obj.TryGetProperty("unit",  out var u) ? u.GetString() ?? "" : "";
        var trend = obj.TryGetProperty("trend", out var t) ? (t.GetString() ?? "").ToLowerInvariant() : "";
        var delta = obj.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.Number
            ? d.GetDouble() : (double?)null;

        var arrow = trend switch { "up" => "▲", "down" => "▼", "flat" => "▬", _ => "" };
        var trendClass = trend switch { "up" => "trend-up", "down" => "trend-down", _ => "trend-flat" };
        var sizeClass = large ? "tile-kpi tile-kpi--large" : "tile-kpi";

        var sb = new StringBuilder($"<div class=\"{sizeClass}\">");
        sb.Append($"<div class=\"tile-kpi-value\">{Esc(value)}");
        if (!string.IsNullOrEmpty(unit)) sb.Append($"<span class=\"tile-kpi-unit\">{Esc(unit)}</span>");
        sb.Append("</div>");
        if (!string.IsNullOrEmpty(label))
            sb.Append($"<div class=\"tile-kpi-label\">{Esc(label)}</div>");
        if (delta is not null || arrow.Length > 0)
        {
            sb.Append($"<div class=\"tile-kpi-delta {trendClass}\">{arrow}");
            if (delta is not null)
            {
                var sign = delta > 0 ? "+" : "";
                sb.Append($" {sign}{delta.Value.ToString("0.##", CultureInfo.InvariantCulture)}");
            }
            sb.Append("</div>");
        }
        sb.Append("</div>");
        return sb.ToString();
    }

    // ── Progress ─────────────────────────────────────────────────────────────

    private static string RenderProgress(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array) return Raw(json);
        var sb = new StringBuilder("<div class=\"tile-progress\">");
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var label = item.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
            var val = item.TryGetProperty("value", out var vv) && vv.ValueKind == JsonValueKind.Number ? vv.GetDouble() : 0;
            var max = item.TryGetProperty("max", out var mm) && mm.ValueKind == JsonValueKind.Number ? mm.GetDouble() : 100;
            var color = item.TryGetProperty("color", out var cc) ? cc.GetString() ?? "" : "";
            var pct = max > 0 ? Math.Clamp(val / max * 100, 0, 100) : 0;
            var style = string.IsNullOrEmpty(color)
                ? $"width:{pct.ToString("0.#", CultureInfo.InvariantCulture)}%"
                : $"width:{pct.ToString("0.#", CultureInfo.InvariantCulture)}%;background:{Esc(color)}";
            sb.Append($"""
                <div class="tile-progress-row">
                  <div class="tile-progress-head"><span class="tile-progress-label">{Esc(label)}</span><span class="tile-progress-val">{Fmt(val)} / {Fmt(max)}</span></div>
                  <div class="tile-progress-bar"><div class="tile-progress-fill" style="{style}"></div></div>
                </div>
                """);
        }
        sb.Append("</div>");
        return sb.ToString();
    }

    // ── Sparkline ────────────────────────────────────────────────────────────

    private static string RenderSparkline(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("points", out var pts) || pts.ValueKind != JsonValueKind.Array)
            return Raw(json);

        var values = pts.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Number)
            .Select(e => e.GetDouble()).ToArray();
        if (values.Length < 2) return "<span class=\"tile-empty\">—</span>";

        var label = root.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
        var current = root.TryGetProperty("current", out var c) ? Stringify(c) : Fmt(values[^1]);
        var unit = root.TryGetProperty("unit", out var u) ? u.GetString() ?? "" : "";

        var min = values.Min(); var max = values.Max();
        var range = max - min;
        if (range < 1e-9) range = 1;
        const int W = 200, H = 50;
        var step = (double)W / (values.Length - 1);
        var pointsAttr = string.Join(' ', values.Select((v, i) =>
        {
            var x = (i * step).ToString("0.##", CultureInfo.InvariantCulture);
            var y = (H - (v - min) / range * H).ToString("0.##", CultureInfo.InvariantCulture);
            return $"{x},{y}";
        }));

        return $"""
            <div class="tile-sparkline">
              <div class="tile-sparkline-head">
                <span class="tile-sparkline-current">{Esc(current)}<span class="tile-kpi-unit">{Esc(unit)}</span></span>
                <span class="tile-sparkline-label">{Esc(label)}</span>
              </div>
              <svg class="tile-sparkline-svg" viewBox="0 0 {W} {H}" preserveAspectRatio="none" xmlns="http://www.w3.org/2000/svg">
                <polyline fill="none" stroke="currentColor" stroke-width="2" points="{pointsAttr}" />
              </svg>
            </div>
            """;
    }

    // ── Bar chart ────────────────────────────────────────────────────────────

    private static string RenderBarChart(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return Raw(json);
        if (!root.TryGetProperty("labels", out var ls) || !root.TryGetProperty("values", out var vs))
            return Raw(json);

        var labels = ls.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
        var values = vs.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Number)
            .Select(e => e.GetDouble()).ToArray();
        var n = Math.Min(labels.Length, values.Length);
        if (n == 0) return "<span class=\"tile-empty\">—</span>";

        var max = values.Take(n).DefaultIfEmpty(0).Max();
        if (max <= 0) max = 1;

        var sb = new StringBuilder("<div class=\"tile-bar-chart\">");
        for (var i = 0; i < n; i++)
        {
            var pct = values[i] / max * 100;
            sb.Append($"""
                <div class="tile-bar-row" title="{Esc(labels[i])}: {Fmt(values[i])}">
                  <div class="tile-bar-label">{Esc(labels[i])}</div>
                  <div class="tile-bar-track"><div class="tile-bar-fill" style="width:{pct.ToString("0.#", CultureInfo.InvariantCulture)}%;background:{_palette[i % _palette.Length]}"></div></div>
                  <div class="tile-bar-val">{Fmt(values[i])}</div>
                </div>
                """);
        }
        sb.Append("</div>");
        return sb.ToString();
    }

    // ── Donut ────────────────────────────────────────────────────────────────

    private static string RenderDonut(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array) return Raw(json);
        var slices = root.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
        if (slices.Count == 0) return "<span class=\"tile-empty\">—</span>";

        var entries = slices.Select((s, i) => new
        {
            Label = s.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "",
            Value = s.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0,
            Color = s.TryGetProperty("color", out var c) && !string.IsNullOrEmpty(c.GetString()) ? c.GetString()! : _palette[i % _palette.Length],
        }).ToList();

        var total = entries.Sum(e => e.Value);
        if (total <= 0) return "<span class=\"tile-empty\">—</span>";

        // Build conic-gradient stops
        var stops = new StringBuilder();
        double acc = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            var start = acc / total * 100;
            acc += entries[i].Value;
            var end = acc / total * 100;
            if (i > 0) stops.Append(", ");
            stops.Append($"{entries[i].Color} {start.ToString("0.##", CultureInfo.InvariantCulture)}% {end.ToString("0.##", CultureInfo.InvariantCulture)}%");
        }

        var legend = new StringBuilder("<ul class=\"tile-donut-legend\">");
        foreach (var e in entries)
        {
            var pct = e.Value / total * 100;
            legend.Append($"<li><span class=\"tile-donut-swatch\" style=\"background:{Esc(e.Color)}\"></span><span class=\"tile-donut-label\">{Esc(e.Label)}</span><span class=\"tile-donut-pct\">{pct.ToString("0.#", CultureInfo.InvariantCulture)}%</span></li>");
        }
        legend.Append("</ul>");

        return $"""
            <div class="tile-donut">
              <div class="tile-donut-chart" style="background: conic-gradient({stops})"><div class="tile-donut-hole"></div></div>
              {legend}
            </div>
            """;
    }

    // ── Gauge ────────────────────────────────────────────────────────────────

    private static string RenderGauge(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return Raw(json);
        var value = root.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        var min = root.TryGetProperty("min", out var mn) && mn.ValueKind == JsonValueKind.Number ? mn.GetDouble() : 0;
        var max = root.TryGetProperty("max", out var mx) && mx.ValueKind == JsonValueKind.Number ? mx.GetDouble() : 100;
        var label = root.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
        var unit = root.TryGetProperty("unit", out var u) ? u.GetString() ?? "" : "";

        var range = max - min;
        if (range <= 0) range = 1;
        var pct = Math.Clamp((value - min) / range, 0, 1);

        // Half-circle SVG, 180° sweep from left (20,70) to right (140,70).
        // The fill arc covers pct * 180°, so largeArc is always 0.
        const int R = 60, Cx = 80, Cy = 70;
        var angle = Math.PI * (1 - pct);
        var ex = Cx + R * Math.Cos(angle);
        var ey = Cy - R * Math.Sin(angle);

        var color = pct < 0.5 ? "#22c55e" : pct < 0.8 ? "#f59e0b" : "#ef4444";

        // At pct ≈ 0 the arc is degenerate; SVG round-linecap then renders a single dot, which
        // looks fine as a "needle at minimum" indicator.
        var arcPath = pct < 0.001
            ? ""
            : $"<path d=\"M 20 70 A {R} {R} 0 0 1 {ex.ToString("0.##", CultureInfo.InvariantCulture)} {ey.ToString("0.##", CultureInfo.InvariantCulture)}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"12\" stroke-linecap=\"round\" />";

        return $"""
            <div class="tile-gauge">
              <svg viewBox="0 0 160 90" xmlns="http://www.w3.org/2000/svg">
                <path d="M 20 70 A {R} {R} 0 0 1 140 70" fill="none" stroke="var(--surface3)" stroke-width="12" stroke-linecap="round" />
                {arcPath}
              </svg>
              <div class="tile-gauge-value">{Fmt(value)}<span class="tile-kpi-unit">{Esc(unit)}</span></div>
              <div class="tile-gauge-label">{Esc(label)}</div>
            </div>
            """;
    }

    // ── Status grid ──────────────────────────────────────────────────────────

    private static string RenderStatusGrid(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array) return Raw(json);
        var sb = new StringBuilder("<div class=\"tile-status-grid\">");
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var label = item.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
            var status = item.TryGetProperty("status", out var s) ? (s.GetString() ?? "").ToLowerInvariant() : "";
            var detail = item.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "";
            var cls = status switch { "ok" => "ok", "warn" => "warn", "err" => "err", _ => "" };
            sb.Append($"""
                <div class="tile-status-cell tile-status-{cls}">
                  <div class="tile-status-dot"></div>
                  <div class="tile-status-body"><div class="tile-status-label">{Esc(label)}</div><div class="tile-status-detail">{Esc(detail)}</div></div>
                </div>
                """);
        }
        sb.Append("</div>");
        return sb.ToString();
    }

    // ── Heatmap (calendar) ───────────────────────────────────────────────────

    private static string RenderHeatmap(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Accept either a plain array or {data:[...], legend:[{label,color}]}
        JsonElement dataEl;
        JsonElement? legendEl = null;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (!root.TryGetProperty("data", out dataEl) || dataEl.ValueKind != JsonValueKind.Array) return Raw(json);
            if (root.TryGetProperty("legend", out var lEl) && lEl.ValueKind == JsonValueKind.Array)
                legendEl = lEl;
        }
        else if (root.ValueKind == JsonValueKind.Array)
            dataEl = root;
        else
            return Raw(json);

        // Parse entries; track max value per color group for independent intensity scaling.
        var entries = new Dictionary<DateOnly, (double Value, string? Color)>();
        var maxPerGroup = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in dataEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("date", out var dEl) || dEl.ValueKind != JsonValueKind.String) continue;
            if (!DateOnly.TryParseExact(dEl.GetString(), "yyyy-MM-dd", out var date)) continue;
            var val = item.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            var color = item.TryGetProperty("color", out var cEl) && cEl.ValueKind == JsonValueKind.String ? cEl.GetString() : null;
            entries[date] = (val, color);
            var key = color ?? "";
            if (!maxPerGroup.TryGetValue(key, out var prev) || val > prev) maxPerGroup[key] = val;
        }
        if (entries.Count == 0) return "<span class=\"tile-empty\">—</span>";

        var end = entries.Keys.Max();
        var start = end.AddDays(-7 * 12 + 1); // ~12 weeks
        // align start to a Monday
        while (start.DayOfWeek != DayOfWeek.Monday) start = start.AddDays(-1);

        var weeks = new List<DateOnly>();
        for (var d = start; d <= end; d = d.AddDays(7)) weeks.Add(d);

        // One label per column = the week's start date. Format "MMM d" so each column is
        // unique and the month context is always visible. Rendered at -45° above the column.
        var monthLabels = new string[weeks.Count];
        for (int i = 0; i < weeks.Count; i++)
            monthLabels[i] = weeks[i].ToString("MMM d", CultureInfo.InvariantCulture);

        // Single CSS grid: 1 day-label col + N week cols × 1 month-label row + 7 day rows.
        var sb = new StringBuilder();
        sb.Append($"<div class=\"tile-heatmap-grid\" style=\"grid-template-columns: 28px repeat({weeks.Count}, 11px)\">");

        // Row 1: empty corner + tilted month labels.
        sb.Append("<div class=\"tile-heatmap-corner\"></div>");
        for (int i = 0; i < weeks.Count; i++)
            sb.Append($"<span class=\"tile-heatmap-month\">{monthLabels[i]}</span>");

        // Rows 2..8: one day label per row, all 7 days.
        string[] dayLabels = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
        for (int day = 0; day < 7; day++)
        {
            sb.Append($"<span class=\"tile-heatmap-day\">{dayLabels[day]}</span>");
            for (int w = 0; w < weeks.Count; w++)
            {
                var date = weeks[w].AddDays(day);
                if (date > end) { sb.Append("<div class=\"tile-heatmap-cell tile-heatmap-empty\"></div>"); continue; }
                if (!entries.TryGetValue(date, out var entry))
                {
                    sb.Append($"<div class=\"tile-heatmap-cell tile-heatmap-l0\" title=\"{date:yyyy-MM-dd}: 0\"></div>");
                    continue;
                }
                var (v, color) = entry;
                var groupMax = maxPerGroup.GetValueOrDefault(color ?? "", 1);
                var lvl = groupMax <= 0 ? 0 : (int)Math.Ceiling(v / groupMax * 4);
                lvl = Math.Clamp(lvl, 0, 4);

                if (color != null && TryParseHexColor(color, out var r, out var g, out var b))
                {
                    // Intensity via rgba alpha: l0 = transparent (falls back to surface3 base), l4 = fully opaque.
                    var alpha = lvl switch { 0 => 0.0, 1 => 0.25, 2 => 0.5, 3 => 0.75, _ => 1.0 };
                    var alphaStr = alpha.ToString("F2", CultureInfo.InvariantCulture);
                    sb.Append($"<div class=\"tile-heatmap-cell\" style=\"background:rgba({r},{g},{b},{alphaStr})\" title=\"{date:yyyy-MM-dd}: {Fmt(v)}\"></div>");
                }
                else
                {
                    sb.Append($"<div class=\"tile-heatmap-cell tile-heatmap-l{lvl}\" title=\"{date:yyyy-MM-dd}: {Fmt(v)}\"></div>");
                }
            }
        }
        sb.Append("</div>");

        if (legendEl.HasValue)
        {
            sb.Append("<div class=\"tile-heatmap-legend\">");
            foreach (var item in legendEl.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var label = item.TryGetProperty("label", out var lEl) ? Esc(lEl.GetString() ?? "") : "";
                var color = item.TryGetProperty("color", out var cEl) ? Esc(cEl.GetString() ?? "") : "";
                var swatchStyle = color.Length > 0 ? $" style=\"background:{color}\"" : "";
                sb.Append($"<span class=\"tile-heatmap-legend-item\"><span class=\"tile-heatmap-legend-swatch\"{swatchStyle}></span>{label}</span>");
            }
            sb.Append("</div>");
        }

        return sb.ToString();
    }

    private static bool TryParseHexColor(string hex, out int r, out int g, out int b)
    {
        r = g = b = 0;
        var h = hex.TrimStart('#');
        if (h.Length == 6)
        {
            r = Convert.ToInt32(h[..2], 16);
            g = Convert.ToInt32(h[2..4], 16);
            b = Convert.ToInt32(h[4..6], 16);
            return true;
        }
        if (h.Length == 3)
        {
            r = Convert.ToInt32(new string(h[0], 2), 16);
            g = Convert.ToInt32(new string(h[1], 2), 16);
            b = Convert.ToInt32(new string(h[2], 2), 16);
            return true;
        }
        return false;
    }

    // ── Leaderboard ──────────────────────────────────────────────────────────

    private static string RenderLeaderboard(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array) return Raw(json);
        var sb = new StringBuilder("<ol class=\"tile-leaderboard\">");
        var rank = 0;
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            rank++;
            var label = item.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
            var score = item.TryGetProperty("score", out var s) ? Stringify(s) : "";
            var medal = rank switch { 1 => "🥇", 2 => "🥈", 3 => "🥉", _ => "" };
            sb.Append($"<li><span class=\"tile-lb-rank\">{(medal.Length > 0 ? medal : "#" + rank)}</span><span class=\"tile-lb-label\">{Esc(label)}</span><span class=\"tile-lb-score\">{Esc(score)}</span></li>");
        }
        sb.Append("</ol>");
        return sb.ToString();
    }

    // ── Timeline ─────────────────────────────────────────────────────────────

    private static string RenderTimeline(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array) return Raw(json);

        var events = new List<(DateOnly Date, string Label, string Type)>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("date", out var dEl) || dEl.ValueKind != JsonValueKind.String) continue;
            if (!DateOnly.TryParseExact(dEl.GetString(), "yyyy-MM-dd", out var date)) continue;
            var label = item.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
            var type = item.TryGetProperty("type", out var t) ? (t.GetString() ?? "").ToLowerInvariant() : "";
            events.Add((date, label, type));
        }
        if (events.Count == 0) return "<span class=\"tile-empty\">—</span>";

        events = events.OrderBy(e => e.Date).ToList();
        var min = events[0].Date;
        var max = events[^1].Date;
        var range = Math.Max(1, max.DayNumber - min.DayNumber);

        var sb = new StringBuilder("<div class=\"tile-timeline\"><div class=\"tile-timeline-track\"><div class=\"tile-timeline-axis\"></div>");
        for (int i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            // Inset positions to [4%, 96%] so dots and cards don't clip at the tile edges.
            var pct = 4 + (double)(ev.Date.DayNumber - min.DayNumber) / range * 92;
            var pos = i % 2 == 0 ? "tile-timeline-above" : "tile-timeline-below";
            var typeCls = ev.Type switch
            {
                "release"   => "tile-timeline-release",
                "incident"  => "tile-timeline-incident",
                "milestone" => "tile-timeline-milestone",
                "freeze"    => "tile-timeline-freeze",
                _           => "tile-timeline-default",
            };
            var leftStr = pct.ToString("0.##", CultureInfo.InvariantCulture);
            sb.Append($"""
                <div class="tile-timeline-event {pos}" style="left:{leftStr}%">
                  <div class="tile-timeline-card">
                    <div class="tile-timeline-label">{Esc(ev.Label)}</div>
                    <div class="tile-timeline-date">{ev.Date.ToString("MMM d", CultureInfo.InvariantCulture)}</div>
                  </div>
                  <div class="tile-timeline-stem"></div>
                </div>
                <div class="tile-timeline-dot {typeCls}" style="left:{leftStr}%"></div>
                """);
        }
        sb.Append("</div>");

        sb.Append($"""
            <div class="tile-timeline-axis-labels">
              <span>{min.ToString("MMM yyyy", CultureInfo.InvariantCulture)}</span>
              <span>{max.ToString("MMM yyyy", CultureInfo.InvariantCulture)}</span>
            </div>
            </div>
            """);
        return sb.ToString();
    }

    // ── Mermaid ──────────────────────────────────────────────────────────────

    private static string RenderMermaid(string source) =>
        $"<div class=\"tile-mermaid\"><pre class=\"mermaid\">{Esc(source)}</pre></div>";

    // ── JSON auto (fallback for files without a template) ────────────────────

    private static string RenderJsonAuto(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
                && root[0].ValueKind == JsonValueKind.Object)
                return RenderTable(json);
            if (root.ValueKind == JsonValueKind.Object)
                return RenderKpiGrid(BuildKpiGridFromObject(root));
            return Raw(json);
        }
        catch
        {
            return Raw(json);
        }
    }

    private static string BuildKpiGridFromObject(JsonElement obj)
    {
        var sb = new StringBuilder("[");
        var first = true;
        foreach (var p in obj.EnumerateObject())
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"value\":").Append(JsonSerializer.Serialize(Stringify(p.Value)))
              .Append(",\"label\":").Append(JsonSerializer.Serialize(p.Name)).Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string Raw(string s) => $"<pre class=\"tile-raw\">{Esc(s)}</pre>";
    private static string Esc(string s) => HttpUtility.HtmlEncode(s ?? "");
    private static string Fmt(double v) => v.ToString(Math.Abs(v - Math.Round(v)) < 1e-9 ? "0" : "0.##", CultureInfo.InvariantCulture);

    private static string Stringify(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Number => Fmt(e.GetDouble()),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "",
        _ => e.ToString(),
    };
}
