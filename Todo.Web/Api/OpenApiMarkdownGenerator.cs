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
        sb.AppendLine("## Guide : Créer des automations");
        sb.AppendLine();
        sb.AppendLine("Les automations permettent de déclencher automatiquement des actions (lancer un agent Claude, déplacer un ticket) en réponse à des événements sur le board.");
        sb.AppendLine();
        sb.AppendLine("### Fichier `automations.json`");
        sb.AppendLine();
        sb.AppendLine("Les automations sont déclarées dans `{WorkspacePath}/.agents/automations.json`. Ce fichier peut aussi être édité via `PUT /api/projects/{slug}/automations`.");
        sb.AppendLine();
        sb.AppendLine("Structure générale :");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("{");
        sb.AppendLine("  \"dailyBudgetUsd\": 70,");
        sb.AppendLine("  \"minDescriptionLength\": 50,");
        sb.AppendLine("  \"automations\": [");
        sb.AppendLine("    {");
        sb.AppendLine("      \"id\": \"unique-id\",");
        sb.AppendLine("      \"name\": \"Nom lisible (optionnel)\",");
        sb.AppendLine("      \"enabled\": true,");
        sb.AppendLine("      \"trigger\": { \"type\": \"...\", ... },");
        sb.AppendLine("      \"conditions\": [ { \"type\": \"...\", ... } ],");
        sb.AppendLine("      \"actions\": [ { \"type\": \"...\", ... } ]");
        sb.AppendLine("    }");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("**Champs racine :**");
        sb.AppendLine();
        sb.AppendLine("| Champ | Type | Description |");
        sb.AppendLine("|-------|------|-------------|");
        sb.AppendLine("| `automations` | array | Liste des automations |");
        sb.AppendLine("| `dailyBudgetUsd` | number | Budget journalier max (bloque les dispatches non-CEO au-delà) |");
        sb.AppendLine("| `minDescriptionLength` | integer | Longueur min de description pour autoriser le dispatch |");
        sb.AppendLine();
        sb.AppendLine("**Champs d'une automation :**");
        sb.AppendLine();
        sb.AppendLine("| Champ | Requis | Description |");
        sb.AppendLine("|-------|--------|-------------|");
        sb.AppendLine("| `id` | oui | Identifiant unique (string) |");
        sb.AppendLine("| `name` | non | Libellé affiché dans l'UI |");
        sb.AppendLine("| `enabled` | non | `true` par défaut. Mettre `false` pour désactiver sans supprimer |");
        sb.AppendLine("| `trigger` | oui | Événement déclencheur (un seul par automation) |");
        sb.AppendLine("| `conditions` | non | Conditions supplémentaires à vérifier avant exécution |");
        sb.AppendLine("| `actions` | non | Actions à exécuter quand le trigger + conditions sont remplis |");
        sb.AppendLine();
        sb.AppendLine("### Triggers disponibles");
        sb.AppendLine();
        sb.AppendLine("| Type | Params | Description |");
        sb.AppendLine("|------|--------|-------------|");
        sb.AppendLine("| `ticketInColumn` | `columns` (string[]), `assigneeSlug?`, `seconds` | Déclenche quand un ticket assigné se trouve dans une des colonnes |");
        sb.AppendLine("| `statusChange` | `from?`, `to?`, `pollSeconds`, `debounceSeconds?` | Déclenche quand un ticket change de statut |");
        sb.AppendLine("| `interval` | `seconds` **ou** `cron` (format cron) | Déclenche périodiquement |");
        sb.AppendLine("| `gitCommit` | `pollSeconds` | Déclenche après un nouveau commit détecté |");
        sb.AppendLine("| `subTicketStatus` | `parentColumn?`, `pollSeconds`, `debounceSeconds?` | Déclenche quand les sous-tickets d'un parent changent |");
        sb.AppendLine("| `boardIdle` | `idleColumns[]`, `pollSeconds` | Déclenche quand tous les tickets sont dans les colonnes idle |");
        sb.AppendLine("| `agentInactivity` | `minutesIdle`, `pollSeconds` | Déclenche quand aucun agent n'a tourné depuis N minutes |");
        sb.AppendLine();
        sb.AppendLine("### Conditions disponibles");
        sb.AppendLine();
        sb.AppendLine("| Type | Params | Description |");
        sb.AppendLine("|------|--------|-------------|");
        sb.AppendLine("| `ticketInColumn` | `columns[]`, `assigneeSlug?`, `negate?` | Vérifie qu'un ticket est dans une colonne donnée |");
        sb.AppendLine("| `noPendingTickets` | `assigneeSlug?`, `columns?` | Vérifie qu'aucun ticket n'est en attente |");
        sb.AppendLine("| `minDescriptionLength` | `length` | Vérifie que la description du ticket est assez longue |");
        sb.AppendLine("| `fieldLength` | `field`, `mode` (min/max), `length`, `negate?` | Vérifie la longueur d'un champ |");
        sb.AppendLine("| `assignedTo` | `slugs[]`, `negate?` | Vérifie l'assignation du ticket |");
        sb.AppendLine("| `labels` | `labels[]`, `negate?` | Vérifie les labels du ticket |");
        sb.AppendLine("| `priority` | `priorities[]`, `negate?` | Vérifie la priorité du ticket |");
        sb.AppendLine();
        sb.AppendLine("### Actions disponibles");
        sb.AppendLine();
        sb.AppendLine("| Type | Params | Description |");
        sb.AppendLine("|------|--------|-------------|");
        sb.AppendLine("| `runAgent` | `agent`, `maxTurns?`, `concurrencyGroup?`, `mutuallyExclusiveWith[]`, `context?`, `env?`, `model?` | Lance l'agent nommé ; son skill est résolu par convention en `.agents/{agent}/SKILL.md` |");
        sb.AppendLine("| `moveTicketStatus` | `to` | Déplace le ticket vers la colonne indiquée |");
        sb.AppendLine();
        sb.AppendLine("### Concurrence");
        sb.AppendLine();
        sb.AppendLine("- **`concurrencyGroup`** : un seul run actif par groupe à la fois (ex: `\"code\"`, `\"producer\"`).");
        sb.AppendLine("- **`mutuallyExclusiveWith`** : liste de groupes bloqués pendant l'exécution.");
        sb.AppendLine("- **Dédup implicite** : pas de second run actif sur le même `(agent, ticketId)`.");
        sb.AppendLine();
        sb.AppendLine("### Cycle de vie");
        sb.AppendLine();
        sb.AppendLine("1. **Créer** : écrire `automations.json` ou appeler `PUT /api/projects/{slug}/automations`.");
        sb.AppendLine("2. **Recharger** : `POST /api/projects/{slug}/automations/reload` pour forcer le rechargement.");
        sb.AppendLine("3. **Exécuter** : le moteur évalue les triggers en continu. Lancement manuel possible via `POST /api/projects/{slug}/automations/{id}/run`.");
        sb.AppendLine("4. **Suivre** : `GET /api/projects/{slug}/runs` liste les runs en cours. `GET /api/projects/{slug}/runs/{runId}/stream` pour le SSE temps réel.");
        sb.AppendLine("5. **Piloter** : `POST /api/projects/{slug}/runs/{runId}/steer` pour envoyer un message à un agent en cours. `POST /api/projects/{slug}/runs/{runId}/stop` pour l'arrêter.");
        sb.AppendLine();
        sb.AppendLine("### Exemple complet");
        sb.AppendLine();
        sb.AppendLine("Automation qui lance un agent `programmer` quand un ticket lui est assigné en Todo ou InProgress :");
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
