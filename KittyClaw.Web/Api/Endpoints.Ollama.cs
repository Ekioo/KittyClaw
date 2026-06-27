using System.Net.Http.Json;
using System.Text.Json.Serialization;
using KittyClaw.Core.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapOllama(RouteGroupBuilder api)
    {
        api.MapGet("/projects/{slug}/ollama-models", async (string slug, ProjectService ps, HttpClient http) =>
        {
            var project = await ps.GetProjectAsync(slug);
            if (project is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(project.LocalModelBaseUrl)) return Results.Ok(Array.Empty<string>());

            try
            {
                var baseUrl = project.LocalModelBaseUrl.TrimEnd('/');
                var resp = await http.GetAsync($"{baseUrl}/api/tags");
                if (!resp.IsSuccessStatusCode) return Results.Ok(Array.Empty<string>());

                var body = await resp.Content.ReadFromJsonAsync<OllamaTagsResponse>();
                var models = body?.Models
                    ?.Select(m => m.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .ToList() ?? new List<string>();
                return Results.Ok(models);
            }
            catch
            {
                return Results.Ok(Array.Empty<string>());
            }
        }).WithTags("Ollama");
    }

    private sealed record OllamaTagsResponse([property: JsonPropertyName("models")] List<OllamaModel>? Models);
    private sealed record OllamaModel([property: JsonPropertyName("name")] string Name);
}
