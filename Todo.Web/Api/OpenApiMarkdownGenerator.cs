using System.Text;
using System.Text.Json;

namespace Todo.Web.Api;

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
        sb.AppendLine("- `createdBy` / `author`: `\"owner\"` pour l'utilisateur, `\"agent:{name}\"` pour les agents (ex: `\"agent:claude\"`)");
        sb.AppendLine("- OpenAPI JSON: `GET /openapi/v1.json`");
        sb.AppendLine("- Cette doc est auto-générée depuis la spec OpenAPI.");
        sb.AppendLine();

        // Automations guide
        AppendAutomationsGuide(sb);

        return sb.ToString();
    }

    private static void AppendAutomationsGuide(StringBuilder sb)
    {
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Guide: Creating automations");
        sb.AppendLine();
        sb.AppendLine("Automations let you trigger actions automatically (launch a Claude agent, move a ticket) in response to events on the board.");
        sb.AppendLine();
        sb.AppendLine("### `automations.json` file");
        sb.AppendLine();
        sb.AppendLine("Automations are declared in `{WorkspacePath}/.agents/automations.json`. The file can also be edited via `PUT /api/projects/{slug}/automations`.");
        sb.AppendLine();
        sb.AppendLine("General structure:");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("{");
        sb.AppendLine("  \"dailyBudgetUsd\": 70,");
        sb.AppendLine("  \"minDescriptionLength\": 50,");
        sb.AppendLine("  \"automations\": [");
        sb.AppendLine("    {");
        sb.AppendLine("      \"id\": \"unique-id\",");
        sb.AppendLine("      \"name\": \"Human-readable name (optional)\",");
        sb.AppendLine("      \"enabled\": true,");
        sb.AppendLine("      \"trigger\": { \"type\": \"...\", ... },");
        sb.AppendLine("      \"conditions\": [ { \"type\": \"...\", ... } ],");
        sb.AppendLine("      \"actions\": [ { \"type\": \"...\", ... } ]");
        sb.AppendLine("    }");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("**Root fields:**");
        sb.AppendLine();
        sb.AppendLine("| Field | Type | Description |");
        sb.AppendLine("|-------|------|-------------|");
        sb.AppendLine("| `automations` | array | List of automations |");
        sb.AppendLine("| `dailyBudgetUsd` | number | Daily budget cap (blocks non-CEO dispatches beyond this) |");
        sb.AppendLine("| `minDescriptionLength` | integer | Minimum description length required to allow dispatch |");
        sb.AppendLine();
        sb.AppendLine("**Automation fields:**");
        sb.AppendLine();
        sb.AppendLine("| Field | Required | Description |");
        sb.AppendLine("|-------|----------|-------------|");
        sb.AppendLine("| `id` | yes | Unique identifier (string) |");
        sb.AppendLine("| `name` | no | Label shown in the UI |");
        sb.AppendLine("| `enabled` | no | `true` by default. Set to `false` to disable without removing |");
        sb.AppendLine("| `trigger` | yes | Trigger event (one per automation) |");
        sb.AppendLine("| `conditions` | no | Extra conditions to check before execution |");
        sb.AppendLine("| `actions` | no | Actions to run when trigger + conditions are satisfied |");
        sb.AppendLine();
        sb.AppendLine("### Available triggers");
        sb.AppendLine();
        sb.AppendLine("| Type | Params | Description |");
        sb.AppendLine("|------|--------|-------------|");
        sb.AppendLine("| `ticketInColumn` | `columns` (string[]), `assigneeSlug?`, `seconds` | Fires when an assigned ticket sits in one of the given columns |");
        sb.AppendLine("| `statusChange` | `from?`, `to?`, `pollSeconds`, `debounceSeconds?` | Fires when a ticket changes status |");
        sb.AppendLine("| `interval` | `seconds` **or** `cron` (cron format) | Fires periodically |");
        sb.AppendLine("| `gitCommit` | `pollSeconds` | Fires after a new commit is detected |");
        sb.AppendLine("| `subTicketStatus` | `parentColumn?`, `pollSeconds`, `debounceSeconds?` | Fires when a parent's sub-tickets change |");
        sb.AppendLine("| `boardIdle` | `idleColumns[]`, `pollSeconds` | Fires when every ticket is in an idle column |");
        sb.AppendLine("| `agentInactivity` | `minutesIdle`, `pollSeconds` | Fires when no agent has run for N minutes |");
        sb.AppendLine();
        sb.AppendLine("### Available conditions");
        sb.AppendLine();
        sb.AppendLine("| Type | Params | Description |");
        sb.AppendLine("|------|--------|-------------|");
        sb.AppendLine("| `ticketInColumn` | `columns[]`, `assigneeSlug?`, `negate?` | Checks the ticket is in one of the given columns |");
        sb.AppendLine("| `ticketCountInColumn` | `columns[]`, `assigneeSlug?`, `sameAssignee?`, `operator`, `value` | Counts tickets in columns and compares to a threshold (e.g. `== 0` = no pending) |");
        sb.AppendLine("| `minDescriptionLength` | `length` | Checks the ticket description is long enough |");
        sb.AppendLine("| `fieldLength` | `field`, `mode` (min/max), `length`, `negate?` | Checks the length of a field |");
        sb.AppendLine("| `assignedTo` | `slugs[]`, `negate?` | Checks the ticket assignment |");
        sb.AppendLine("| `labels` | `labels[]`, `negate?` | Checks the ticket labels |");
        sb.AppendLine("| `priority` | `priorities[]`, `negate?` | Checks the ticket priority |");
        sb.AppendLine();
        sb.AppendLine("### Available actions");
        sb.AppendLine();
        sb.AppendLine("| Type | Params | Description |");
        sb.AppendLine("|------|--------|-------------|");
        sb.AppendLine("| `runAgent` | `agent`, `maxTurns?`, `concurrencyGroup?`, `mutuallyExclusiveWith[]`, `context?`, `env?`, `model?` | Launches the named agent; its skill is resolved by convention to `.agents/{agent}/SKILL.md` |");
        sb.AppendLine("| `moveTicketStatus` | `to` | Moves the ticket to the given column |");
        sb.AppendLine();
        sb.AppendLine("### Concurrency");
        sb.AppendLine();
        sb.AppendLine("- **`concurrencyGroup`**: only one active run per group at a time (e.g. `\"code\"`, `\"producer\"`).");
        sb.AppendLine("- **`mutuallyExclusiveWith`**: list of groups blocked while this one runs.");
        sb.AppendLine("- **Implicit dedup**: no second active run for the same `(agent, ticketId)`.");
        sb.AppendLine();
        sb.AppendLine("### Life cycle");
        sb.AppendLine();
        sb.AppendLine("1. **Create**: write `automations.json` or call `PUT /api/projects/{slug}/automations`.");
        sb.AppendLine("2. **Reload**: `POST /api/projects/{slug}/automations/reload` to force a reload.");
        sb.AppendLine("3. **Execute**: the engine evaluates triggers continuously.");
        sb.AppendLine("4. **Watch**: `GET /api/projects/{slug}/runs` lists active runs. `GET /api/projects/{slug}/runs/{runId}/stream` for the real-time SSE stream.");
        sb.AppendLine("5. **Steer**: `POST /api/projects/{slug}/runs/{runId}/steer` to send a message to a running agent. `POST /api/projects/{slug}/runs/{runId}/stop` to stop it.");
        sb.AppendLine();
        sb.AppendLine("### Complete example");
        sb.AppendLine();
        sb.AppendLine("Automation that launches a `programmer` agent when a ticket is assigned to them in Todo or InProgress:");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("{");
        sb.AppendLine("  \"automations\": [");
        sb.AppendLine("    {");
        sb.AppendLine("      \"id\": \"programmer\",");
        sb.AppendLine("      \"name\": \"Programmer\",");
        sb.AppendLine("      \"enabled\": true,");
        sb.AppendLine("      \"trigger\": {");
        sb.AppendLine("        \"type\": \"ticketInColumn\",");
        sb.AppendLine("        \"columns\": [\"Todo\", \"InProgress\"],");
        sb.AppendLine("        \"assigneeSlug\": \"programmer\",");
        sb.AppendLine("        \"seconds\": 30");
        sb.AppendLine("      },");
        sb.AppendLine("      \"actions\": [");
        sb.AppendLine("        { \"type\": \"moveTicketStatus\", \"to\": \"InProgress\" },");
        sb.AppendLine("        {");
        sb.AppendLine("          \"type\": \"runAgent\",");
        sb.AppendLine("          \"agent\": \"programmer\",");
        sb.AppendLine("          \"maxTurns\": 200,");
        sb.AppendLine("          \"concurrencyGroup\": \"code\"");
        sb.AppendLine("        }");
        sb.AppendLine("      ]");
        sb.AppendLine("    }");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine("```");
    }

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
            var comment = required ? "" : "  // optional";
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
