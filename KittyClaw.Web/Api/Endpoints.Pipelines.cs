using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Web.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapPipelines(RouteGroupBuilder api)
    {
        api.MapGet("/projects/{slug}/pipelines", async (string slug, PipelineService pipelines) =>
            Results.Ok(await pipelines.ListAsync(slug)))
            .WithTags("Pipelines");

        api.MapPost("/projects/{slug}/pipelines", async (
            string slug, CreatePipelineRequest req, PipelineService pipelines, BoardUpdateNotifier notifier) =>
        {
            try
            {
                var pipeline = await pipelines.CreateAsync(slug, req.Name);
                notifier.NotifyProjectUpdated(slug);
                return Results.Created($"/api/projects/{slug}/pipelines/{pipeline.Id}", pipeline);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithTags("Pipelines");

        api.MapPatch("/projects/{slug}/pipelines/{pipelineId:int}", async (
            string slug, int pipelineId, UpdatePipelineRequest req,
            PipelineService pipelines, BoardUpdateNotifier notifier) =>
        {
            try
            {
                var pipeline = await pipelines.UpdateAsync(slug, pipelineId, req.Name);
                if (pipeline is not null) notifier.NotifyProjectUpdated(slug);
                return pipeline is null ? Results.NotFound() : Results.Ok(pipeline);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithTags("Pipelines");

        api.MapGet("/projects/{slug}/pipelines/{pipelineId:int}/export", async (
            string slug, int pipelineId, PipelineExportService exporter) =>
        {
            try
            {
                var kit = await exporter.ExportAsync(slug, pipelineId);
                return kit is null
                    ? Results.NotFound()
                    : Results.File(kit.Content, "application/zip", kit.FileName);
            }
            catch (PipelineExportBlockedException ex)
            {
                return Results.Json(
                    new PipelineExportBlockedResponse("export_blocked", ex.Findings),
                    statusCode: StatusCodes.Status409Conflict);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithTags("Pipelines")
        .WithSummary("Download the pipeline as a sanitized, portable .kittyclaw-pipeline kit (ZIP). "
            + "Returns 409 with the blocking findings while probable secrets, credentials, absolute paths "
            + "or out-of-folder skill references remain.")
        .Produces(StatusCodes.Status200OK, contentType: "application/zip")
        .Produces(StatusCodes.Status404NotFound)
        .Produces<PipelineExportBlockedResponse>(StatusCodes.Status409Conflict);

        api.MapPost("/projects/{slug}/pipeline-kits/analyze", async (
            string slug, HttpRequest request, PipelineImportService importer) =>
        {
            var archive = await ReadPipelineKitAsync(request);
            if (archive is null) return Results.BadRequest(new { error = "A .kittyclaw-pipeline ZIP body is required." });
            var preview = await importer.AnalyzeAsync(slug, archive);
            return preview is null ? Results.NotFound() : Results.Ok(preview);
        })
        .WithTags("Pipelines")
        .WithSummary("Analyze an untrusted .kittyclaw-pipeline kit without writing project data or files.")
        .Accepts<byte[]>("application/zip")
        .Produces<PipelineImportPreview>()
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound);

        api.MapPost("/projects/{slug}/pipeline-kits/confirm", async (
            string slug, HttpRequest request, PipelineImportService importer, BoardUpdateNotifier notifier) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data with fields 'kit' and 'confirmation' is required." });
            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("kit");
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "A non-empty 'kit' file is required." });
            PipelineImportConfirmation? confirmation;
            try
            {
                confirmation = System.Text.Json.JsonSerializer.Deserialize<PipelineImportConfirmation>(
                    form["confirmation"].ToString(), new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                    });
            }
            catch (System.Text.Json.JsonException ex)
            {
                return Results.BadRequest(new { error = $"Invalid confirmation JSON: {ex.Message}" });
            }
            if (confirmation is null)
                return Results.BadRequest(new { error = "A valid 'confirmation' JSON field is required." });
            try
            {
                await using var stream = file.OpenReadStream();
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                var result = await importer.InstallAsync(slug, buffer.ToArray(), confirmation);
                if (result is null) return Results.NotFound();
                notifier.NotifyProjectUpdated(slug);
                return Results.Created($"/api/projects/{slug}/pipelines/{result.PipelineId}", result);
            }
            catch (PipelineImportRejectedException ex)
            {
                return Results.Json(new PipelineImportRejectedResponse("kit_rejected", ex.Issues),
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            catch (PipelineImportConflictException ex)
            {
                return Results.Json(new PipelineImportRejectedResponse("import_conflict", ex.Issues),
                    statusCode: StatusCodes.Status409Conflict);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithTags("Pipelines")
        .WithSummary("Atomically install a previously reviewed kit; executable content is never run.")
        .Accepts<IFormFile>("multipart/form-data")
        .Produces<PipelineImportResult>(StatusCodes.Status201Created)
        .Produces<PipelineImportRejectedResponse>(StatusCodes.Status409Conflict)
        .Produces<PipelineImportRejectedResponse>(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<byte[]?> ReadPipelineKitAsync(HttpRequest request)
    {
        if (request.ContentLength is 0) return null;
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("kit") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0) return null;
            await using var source = file.OpenReadStream();
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer);
            return buffer.ToArray();
        }
        using var body = new MemoryStream();
        await request.Body.CopyToAsync(body);
        return body.Length == 0 ? null : body.ToArray();
    }
}
