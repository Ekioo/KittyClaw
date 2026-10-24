using System.Text;
using System.Text.Json;

namespace KittyClaw.Web.Api;

public static class OpenApiMarkdownGenerator
{
    public static string Generate(JsonDocument doc)
    {
        var root = doc.RootElement;
        var sb = new StringBuilder();

        // Title
        var title = root.TryGetProperty("info", out var info) && info.TryGetProperty("title", out var t)
            ? t.GetString() : "API";
        sb.AppendLine($"# {title}");
        sb.AppendLine();

        // Servers
        if (root.TryGetProperty("servers", out var servers))
        {
            foreach (var server in servers.EnumerateArray())
            {
                if (server.TryGetProperty("url", out var url))
                    sb.AppendLine($"Base URL: `{url.GetString()}`");
            }
            sb.AppendLine();
        }

        // Group paths by tag
        var grouped = new Dictionary<string, List<(string method, string path, JsonElement op)>>();

        if (root.TryGetProperty("paths", out var paths))
        {
            foreach (var pathProp in paths.EnumerateObject())
            {
                var path = pathProp.Name;
                foreach (var methodProp in pathProp.Value.EnumerateObject())
                {
                    var method = methodProp.Name.ToUpperInvariant();
                    var op = methodProp.Value;
                    var tag = "Other";
                    if (op.TryGetProperty("tags", out var tags) && tags.GetArrayLength() > 0)
                        tag = tags[0].GetString() ?? "Other";
                    if (!grouped.ContainsKey(tag))
                        grouped[tag] = [];
                    grouped[tag].Add((method, path, op));
                }
            }
        }

        // Render each tag group
        foreach (var (tag, ops) in grouped)
        {
            sb.AppendLine($"## {tag}");
            sb.AppendLine();

            foreach (var (method, path, op) in ops)
            {
                var summary = op.TryGetProperty("summary", out var s) ? s.GetString() : null;
                var operationId = op.TryGetProperty("operationId", out var oid) ? oid.GetString() : null;
                var heading = summary ?? operationId ?? $"{method} {path}";

                sb.AppendLine($"### {heading}");
                sb.AppendLine();
                sb.AppendLine("```");
                sb.AppendLine($"{method} {path}");
                sb.AppendLine("```");
                sb.AppendLine();

                // Path & query parameters
                if (op.TryGetProperty("parameters", out var parameters) && parameters.GetArrayLength() > 0)
                {
                    sb.AppendLine("**Parameters:**");
                    sb.AppendLine();
                    sb.AppendLine("| Name | In | Type | Required |");
                    sb.AppendLine("|------|-----|------|----------|");
                    foreach (var param in parameters.EnumerateArray())
                    {
                        var name = param.GetProperty("name").GetString();
                        var location = param.GetProperty("in").GetString();
                        var required = param.TryGetProperty("required", out var req) && req.GetBoolean();
                        var pType = GetSchemaType(param, root);
                        sb.AppendLine($"| `{name}` | {location} | {pType} | {(required ? "Yes" : "No")} |");
                    }
                    sb.AppendLine();
                }

                // Request body
                if (op.TryGetProperty("requestBody", out var body))
                {
                    sb.AppendLine("**Request body:** `application/json`");
                    sb.AppendLine();
                    if (body.TryGetProperty("content", out var content) &&
                        content.TryGetProperty("application/json", out var json) &&
                        json.TryGetProperty("schema", out var schema))
                    {
                        var resolved = ResolveSchema(schema, root);
                        RenderSchemaAsJson(sb, resolved, root);
                    }
                    sb.AppendLine();
                }

                // Responses
                if (op.TryGetProperty("responses", out var responses))
                {
                    sb.AppendLine("**Responses:**");
                    sb.AppendLine();
                    foreach (var resp in responses.EnumerateObject())
                    {
                        var desc = resp.Value.TryGetProperty("description", out var d) ? d.GetString() : "";
                        sb.AppendLine($"- `{resp.Name}` {desc}");
                    }
                    sb.AppendLine();
                }

                sb.AppendLine("---");
                sb.AppendLine();
            }
        }

        // Schemas
        if (root.TryGetProperty("components", out var components) &&
            components.TryGetProperty("schemas", out var schemas))
        {
            sb.AppendLine("## Models");
            sb.AppendLine();

            foreach (var schemaProp in schemas.EnumerateObject())
            {
                // Skip request/response wrapper types
                sb.AppendLine($"### {schemaProp.Name}");
                sb.AppendLine();

                var schemaObj = schemaProp.Value;

                if (schemaProp.Name == "TileSidecar")
                {
                    sb.AppendLine("| Field | Type | Required | Description |");
                    sb.AppendLine("|-------|------|----------|-------------|");
                    sb.AppendLine("| template | string | Yes | Renderer to use. Allowed values: `markdown`, `table`, `kpi`, `kpi-grid`, `progress`, `sparkline`, `bar-chart`, `donut`, `gauge`, `status-grid`, `heatmap`, `leaderboard`, `timeline`, `image`, `mermaid`. |");
                    sb.AppendLine("| refresh | integer | Yes | Refresh interval in seconds. `0` = static tile (no auto-refresh). |");
                    sb.AppendLine("| prompt | string | No | LLM instruction executed on each refresh. Empty for static tiles. |");
                    sb.AppendLine("| model | string? | No | Optional Claude model override. Null/empty falls back to the project default. |");
                    sb.AppendLine("| title | string? | No | Optional custom tile header. Null/empty falls back to the tile slug. |");
                    sb.AppendLine();
                    continue;
                }

                if (schemaObj.TryGetProperty("enum", out var enumValues))
                {
                    sb.AppendLine("Enum values:");
                    sb.AppendLine();
                    foreach (var val in enumValues.EnumerateArray())
                    {
                        var str = val.ValueKind == JsonValueKind.String ? val.GetString() : val.GetRawText();
                        if (!string.IsNullOrEmpty(str))
                            sb.AppendLine($"- `{str}`");
                    }
                    sb.AppendLine();
                    continue;
                }

                if (schemaObj.TryGetProperty("properties", out var props))
                {
                    var requiredProps = new HashSet<string>();
                    if (schemaObj.TryGetProperty("required", out var reqArr))
                        foreach (var r in reqArr.EnumerateArray())
                            requiredProps.Add(r.GetString()!);

                    sb.AppendLine("| Field | Type | Required |");
                    sb.AppendLine("|-------|------|----------|");
                    foreach (var prop in props.EnumerateObject())
                    {
                        var pType = GetTypeFromSchema(prop.Value, root);
                        var isReq = requiredProps.Contains(prop.Name);
                        sb.AppendLine($"| `{prop.Name}` | {pType} | {(isReq ? "Yes" : "No")} |");
                    }
                    sb.AppendLine();
                }
            }
        }

        // Footer
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Conventions");
        sb.AppendLine();
        sb.AppendLine("- `author` **(required on all mutating endpoints)**: identifies who is performing the action. Use `\"owner\"` for the human user, or the agent name directly (e.g. `\"programmer\"`, `\"groomer\"`). Omitting it returns HTTP 400.");
        sb.AppendLine("- **Hand-offs are atomic**: `PATCH /api/projects/{slug}/tickets/{id}` applies every provided field — `status` included — in one write. Prefer a single call like `{\"status\":\"Todo\",\"assignedTo\":\"programmer\",\"author\":\"qa-tester\"}` over separate status/assignee calls, so automations never observe a half-applied transition. Optional `expectedStatus` makes the update conditional (HTTP 409 if the ticket already moved on). `PATCH .../tickets/{id}/status` remains available for status-only moves.");
        sb.AppendLine("- **Unknown fields are rejected**: a request body containing a field the endpoint does not support returns HTTP 400 naming the field, instead of silently ignoring it.");
        sb.AppendLine("- **Adding/removing labels**: use `PATCH /api/projects/{slug}/tickets/{id}/labels` with `{\"add\":[\"name\"],\"remove\":[\"name\"],\"author\":\"...\"}` — the server merges into the current labels, so concurrent writers never overwrite each other. `labelIds` on the root ticket PATCH (and the PUT) REPLACES the whole collection; only use it when that is what you mean.");
        sb.AppendLine("- Fields marked `// required` in request body examples must be present; fields marked `// optional` may be omitted.");
        sb.AppendLine("- OpenAPI JSON: `GET /openapi/v1.json`");
        sb.AppendLine("- This documentation is auto-generated from the OpenAPI spec.");
        sb.AppendLine();


        AppendColumnActionsGuide(sb);

        // Dashboard tiles guide
        AppendDashboardTilesGuide(sb);

        return sb.ToString();
    }

    private static void AppendColumnActionsGuide(StringBuilder sb)
    {
        sb.AppendLine("## Guide: Column and scheduled-task actions");
        sb.AppendLine();
        sb.AppendLine("Processors and scheduled tasks use ordered deterministic actions. Each action has an id, an action object with a type discriminator, and an optional failureTargetColumnId. Agent execution and successful routing belong to the processor configuration.");
        sb.AppendLine();
        sb.AppendLine("| Type | Purpose | Fields |");
        sb.AppendLine("|------|---------|--------|");
        sb.AppendLine("| `setLabels` | Add or remove ticket labels | add, remove |");
        sb.AppendLine("| `addComment` | Post a ticket comment | content, author |");
        sb.AppendLine("| `executePowerShell` | Run a script or script file | script or scriptFile, arguments, timeoutSeconds, env |");
        sb.AppendLine("| `createTicket` | Create a ticket | title, description, status, priority, labels, parentId, createdBy, skipIfExists |");
        sb.AppendLine("| `httpRequest` | Send an HTTP request | method, url, headers, body, contentType, timeoutSeconds, allowLocalTargets |");
        sb.AppendLine();
        sb.AppendLine("Ticket comments and labels require a ticket target. Use a scheduled task's FirstTicket scope for these actions. Failures stop the chain and follow its configured failure route; completed scheduled actions are checkpointed for restart recovery.");
        sb.AppendLine();
    }

    private static void AppendDashboardTilesGuide(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Guide: Dashboard tiles");
        sb.AppendLine();
        sb.AppendLine("Dashboard tiles are auto-refreshing widgets rendered on the project dashboard. Each tile lives in `{WorkspacePath}/.dashboard/<slug>/` and is described by a `tile.yaml` sidecar.");
        sb.AppendLine();
        sb.AppendLine("### Sidecar (`tile.yaml`) format");
        sb.AppendLine();
        sb.AppendLine("```yaml");
        sb.AppendLine("template: markdown   # required — renderer (see table below)");
        sb.AppendLine("refresh: 300         # required — seconds between refreshes (0 = static)");
        sb.AppendLine("prompt: |            # optional — LLM instruction executed on each refresh");
        sb.AppendLine("  Summarize the most recent CI runs.");
        sb.AppendLine("model: claude-sonnet-4-6  # optional — override the project default model");
        sb.AppendLine("title: Recent CI runs     # optional — header label (falls back to tile slug)");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("### Available templates");
        sb.AppendLine();
        sb.AppendLine("| Template | Default file | Description |");
        sb.AppendLine("|----------|--------------|-------------|");
        foreach (var id in TileTemplateCatalog)
        {
            var ext = id switch
            {
                "markdown" => ".md",
                "mermaid"  => ".mmd",
                "image"    => ".png/.jpg/.svg",
                _          => ".json",
            };
            var desc = TileTemplateDescriptions.TryGetValue(id, out var d) ? d : "";
            sb.AppendLine($"| `{id}` | `{ext}` | {desc} |");
        }
        sb.AppendLine();
        sb.AppendLine("### Creating a tile — walkthrough");
        sb.AppendLine();
        sb.AppendLine("1. **Register the tile** with `POST /dashboard/tiles` (body: `{ \"tileSlug\": \"my-tile\", ... }`). This creates the tile folder.");
        sb.AppendLine("2. **Write the sidecar** with `PUT /api/projects/{slug}/dashboard/tiles/{tileSlug}/sidecar` (body: a `TileSidecar` JSON object). This is what tells the renderer which template to use and how often to refresh.");
        sb.AppendLine("3. **Seed the initial output** with `PUT /api/projects/{slug}/dashboard/tiles/{tileSlug}/output` so the tile renders something before the first refresh runs.");
        sb.AppendLine("4. **Trigger a refresh** with `POST /api/projects/{slug}/dashboard/tiles/{tileSlug}/refresh` to run the LLM pipeline once on demand (auto-refreshes also happen every `refresh` seconds).");
        sb.AppendLine();
    }

    // Kept in this file (rather than referencing TileTemplate.All) so the generated docs
    // remain stable even if Core's catalogue is restructured. Order is intentional —
    // matches the order surfaced in the Dashboard "add tile" picker.
    private static readonly string[] TileTemplateCatalog =
    [
        "markdown", "table", "kpi", "kpi-grid", "progress", "sparkline",
        "bar-chart", "donut", "gauge", "status-grid", "heatmap",
        "leaderboard", "timeline", "image", "mermaid",
    ];

    private static readonly Dictionary<string, string> TileTemplateDescriptions = new()
    {
        ["markdown"]     = "Free-form text, headings, lists, tables, **bold**.",
        ["table"]        = "Structured rows/columns from a JSON array of objects.",
        ["kpi"]          = "One big number with optional unit, delta, trend.",
        ["kpi-grid"]     = "2-6 KPIs side by side.",
        ["progress"]     = "Labeled progress bars.",
        ["sparkline"]    = "Mini trend curve plus current value.",
        ["bar-chart"]    = "Categorical bars (labels + values).",
        ["donut"]        = "Proportional slices.",
        ["gauge"]        = "Value within a min/max range.",
        ["status-grid"]  = "Red/yellow/green status cells.",
        ["heatmap"]      = "GitHub-style activity calendar (date,value).",
        ["leaderboard"]  = "Ranked list with medals.",
        ["timeline"]     = "Events on a horizontal time axis.",
        ["image"]        = "Static or generated PNG/JPG/SVG.",
        ["mermaid"]      = "Mermaid diagram (flowchart, sequence, gantt, …).",
    };

    private static JsonElement ResolveSchema(JsonElement schema, JsonElement root)
    {
        if (schema.TryGetProperty("$ref", out var refProp))
        {
            var refPath = refProp.GetString()!; // e.g. "#/components/schemas/Ticket"
            var parts = refPath.TrimStart('#', '/').Split('/');
            var current = root;
            foreach (var part in parts)
            {
                if (current.TryGetProperty(part, out var next))
                    current = next;
                else
                    return schema;
            }
            return current;
        }
        return schema;
    }

    private static string GetSchemaType(JsonElement param, JsonElement root)
    {
        if (param.TryGetProperty("schema", out var schema))
            return GetTypeFromSchema(schema, root);
        return "string";
    }

    private static string GetTypeFromSchema(JsonElement schema, JsonElement root)
    {
        var resolved = ResolveSchema(schema, root);

        if (schema.TryGetProperty("$ref", out var refProp))
        {
            var name = refProp.GetString()!.Split('/').Last();
            return $"`{name}`";
        }

        if (resolved.TryGetProperty("type", out var type))
        {
            var t = GetTypeString(type);
            if (t == "array" && resolved.TryGetProperty("items", out var items))
                return $"{GetTypeFromSchema(items, root)}[]";
            if (resolved.TryGetProperty("format", out var fmt))
                return $"{t} ({fmt.GetString()})";
            var isNullable = type.ValueKind == JsonValueKind.Array && 
                type.EnumerateArray().Any(x => x.GetString() == "null");
            return isNullable ? $"{t}?" : t;
        }

        return "object";
    }

    private static string GetTypeString(JsonElement type)
    {
        if (type.ValueKind == JsonValueKind.String)
            return type.GetString()!;
        if (type.ValueKind == JsonValueKind.Array)
            return type.EnumerateArray()
                .Select(x => x.GetString())
                .FirstOrDefault(x => x != "null") ?? "string";
        return "object";
    }

    private static void RenderSchemaAsJson(StringBuilder sb, JsonElement schema, JsonElement root)
    {
        if (!schema.TryGetProperty("properties", out var props)) return;

        var requiredProps = new HashSet<string>();
        if (schema.TryGetProperty("required", out var reqArr))
            foreach (var r in reqArr.EnumerateArray())
                requiredProps.Add(r.GetString()!);

        sb.AppendLine("```json");
        sb.AppendLine("{");
        var entries = new List<string>();
        foreach (var prop in props.EnumerateObject())
        {
            var example = GetExampleValue(prop.Name, prop.Value, root);
            var required = requiredProps.Contains(prop.Name);
            var comment = required ? "  // required" : "  // optional";
            entries.Add($"  \"{prop.Name}\": {example}{comment}");
        }
        sb.AppendLine(string.Join(",\n", entries));
        sb.AppendLine("}");
        sb.AppendLine("```");
    }

    private static string GetExampleValue(string name, JsonElement schema, JsonElement root)
    {
        var resolved = ResolveSchema(schema, root);

        if (resolved.TryGetProperty("enum", out var enumVals) && enumVals.GetArrayLength() > 0)
        {
            var first = enumVals[0];
            return first.ValueKind == JsonValueKind.String ? $"\"{first.GetString()}\"" : first.GetRawText();
        }

        if (name == "author")
            return "\"owner\"";

        if (resolved.TryGetProperty("type", out var type))
        {
            var t = GetTypeString(type);
            return t switch
            {
                "string" => "\"...\"",
                "integer" => "0",
                "number" => "0",
                "boolean" => "false",
                "array" => "[]",
                _ => "{}"
            };
        }

        if (schema.TryGetProperty("$ref", out _))
            return "{}";

        return "null";
    }
}
