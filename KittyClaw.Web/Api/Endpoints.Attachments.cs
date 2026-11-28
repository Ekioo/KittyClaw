using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Web.Services;
using Microsoft.AspNetCore.Http.Features;

namespace KittyClaw.Web.Api;

public sealed record TicketAttachmentResponse(string Id, int TicketId, string FileName, long Size, string Author, DateTime CreatedAt, string Url, string Sha256);
public sealed record TicketAttachmentUpload(IFormFile File, string Author);
public sealed record DeleteTicketAttachmentRequest(string Author);

public static partial class Endpoints
{
    private static TicketAttachmentResponse AttachmentResponse(string slug, TicketAttachment a)
        => new(a.Id, a.TicketId, a.FileName, a.Size, a.Author, a.CreatedAt, TicketAttachmentService.DownloadUrl(slug, a.TicketId, a.Id), a.Sha256);

    internal static void MapAttachments(RouteGroupBuilder api)
    {
        api.MapPost("/projects/{slug}/tickets/{id:int}/attachments/{attachmentId}/reveal", async (string slug, int id, string attachmentId,
            HttpContext context, TicketAttachmentLocationService locations, CancellationToken ct) =>
        {
            if (!NetworkAccessMiddleware.IsLocal(context) || NetworkAccessMiddleware.IsCrossOriginBrowserRequest(context))
                return Results.Json(new { error = "local_access_required" }, statusCode: 403);
            try
            {
                return await locations.RevealAsync(slug, id, attachmentId, ct) ? Results.Ok()
                    : Results.NotFound(new { error = "attachment_not_found" });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
            { return Results.Json(new { error = "file_location_unavailable" }, statusCode: 503); }
        }).WithTags("Attachments").WithSummary("Local desktop only: reveal a ticket attachment in the operating system file manager.")
            .Produces(200).ProducesProblem(403).ProducesProblem(404);
        api.MapPost("/projects/{slug}/tickets/{id:int}/attachments", UploadTicketAttachment)
            .WithTags("Attachments").DisableAntiforgery()
            .WithSummary("Attach one file (1 byte–100 MiB) using multipart fields file and author. Send a stable Idempotency-Key header for safe retries; replay returns 200, changed content returns 409, a removed attachment returns 410.")
            .Accepts<TicketAttachmentUpload>("multipart/form-data")
            .Produces<TicketAttachmentResponse>(201).Produces<TicketAttachmentResponse>(200).ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(410).ProducesProblem(413);
        api.MapDelete("/projects/{slug}/tickets/{id:int}/attachments/{attachmentId}", async (string slug, int id, string attachmentId,
            [Microsoft.AspNetCore.Mvc.FromBody] DeleteTicketAttachmentRequest request, TicketAttachmentService files, BoardUpdateNotifier notifier, CancellationToken ct) =>
        {
            try
            {
                if (!await files.DeleteAsync(slug, id, attachmentId, request.Author, ct))
                    return Results.NotFound(new { error = "attachment_not_found" });
                notifier.NotifyProjectUpdated(slug);
                return Results.NoContent();
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).WithTags("Attachments").WithSummary("Remove a ticket attachment and its download link. Repeated deletion returns 204; author is required in the JSON body.")
            .Produces(204).ProducesProblem(400).ProducesProblem(404);
        api.MapGet("/projects/{slug}/tickets/{id:int}/attachments", async (string slug, int id, TicketAttachmentService files, CancellationToken ct) =>
        {
            var attachments = await files.ListAsync(slug, id, ct);
            return attachments is null ? Results.NotFound(new { error = "ticket_not_found" }) : Results.Ok(attachments.Select(a => AttachmentResponse(slug, a)));
        }).WithTags("Attachments").Produces<TicketAttachmentResponse[]>().ProducesProblem(404);
        api.MapGet("/projects/{slug}/tickets/{id:int}/attachments/{attachmentId}", async (string slug, int id, string attachmentId, TicketAttachmentService files, HttpContext context, CancellationToken ct) =>
        {
            var attachment = await files.FindAsync(slug, id, attachmentId, ct);
            if (attachment is null) return Results.NotFound(new { error = "attachment_not_found" });
            FileStream stream;
            try { stream = new FileStream(files.BlobPath(attachment.Id), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete); }
            catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            { return Results.NotFound(new { error = "attachment_not_found" }); }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
            return Results.File(stream, "application/octet-stream", attachment.FileName);
        }).WithTags("Attachments").WithSummary("Download a ticket attachment as an opaque file, never inline active content.").Produces(200, contentType: "application/octet-stream").ProducesProblem(404);
    }

    private static async Task<IResult> UploadTicketAttachment(string slug, int id, HttpRequest request,
        TicketAttachmentService files, BoardUpdateNotifier notifier, CancellationToken ct)
    {
        if (request.ContentType?.StartsWith("multipart/form-data;", StringComparison.OrdinalIgnoreCase) != true)
            return Results.BadRequest(new { error = "Use multipart/form-data with file and author fields." });
        const long maxBody = TicketAttachmentService.MaximumBytes + 64 * 1024;
        if (request.ContentLength > maxBody) return AttachmentSizeRejected();
        var feature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = maxBody;
        request.Body = new AttachmentBodyLimitStream(request.Body, maxBody);
        request.HttpContext.Features.Set<IFormFeature>(new FormFeature(request, new FormOptions
        {
            MultipartBodyLengthLimit = maxBody,
            ValueLengthLimit = 1024, ValueCountLimit = 4, MultipartHeadersLengthLimit = 4096,
            MemoryBufferThreshold = 64 * 1024
        }));
        try
        {
            var keys = request.Headers["Idempotency-Key"];
            if (keys.Count > 1) return Results.BadRequest(new { error = "Send only one Idempotency-Key header." });
            var form = await request.ReadFormAsync(ct);
            if (form.Files.Count != 1 || form.Files[0].Name != "file" || form["author"].Count != 1)
                return Results.BadRequest(new { error = "Exactly one file and one author are required." });
            var file = form.Files[0];
            if (file.Length > TicketAttachmentService.MaximumBytes) return AttachmentSizeRejected();
            await using var input = file.OpenReadStream();
            var attachment = await files.AddAsync(slug, id, file.FileName, form["author"].ToString(), input, ct, keys.Count == 0 ? null : keys.ToString());
            if (attachment is null) return Results.NotFound(new { error = "ticket_not_found" });
            if (attachment.Replayed)
            {
                request.HttpContext.Response.Headers["Idempotency-Replayed"] = "true";
                return Results.Ok(AttachmentResponse(slug, attachment));
            }
            notifier.NotifyProjectUpdated(slug);
            return Results.Created(TicketAttachmentService.DownloadUrl(slug, id, attachment.Id), AttachmentResponse(slug, attachment));
        }
        catch (AttachmentTooLargeException) { return AttachmentSizeRejected(); }
        catch (AttachmentIdempotencyConflictException) { return Results.Conflict(new { error = "idempotency_conflict" }); }
        catch (AttachmentGoneException) { return Results.Json(new { error = "attachment_removed" }, statusCode: 410); }
        catch (BadHttpRequestException ex) when (ex.StatusCode == 413) { return AttachmentSizeRejected(); }
        catch (InvalidDataException ex) when (ex.Message.Contains("length limit", StringComparison.OrdinalIgnoreCase))
        { return AttachmentSizeRejected(); }
        catch (InvalidDataException) { return Results.BadRequest(new { error = "Invalid or oversized multipart form." }); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    private static IResult AttachmentSizeRejected()
        => Results.Json(new { error = "attachment_too_large", maximumBytes = TicketAttachmentService.MaximumBytes }, statusCode: 413);

    private sealed class AttachmentBodyLimitStream(Stream inner, long limit) : Stream
    {
        private long _read;
        private int Count(int length)
        {
            _read += length;
            if (_read > limit) throw new BadHttpRequestException("Attachment request is too large.", 413);
            return length;
        }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Count(await inner.ReadAsync(buffer, ct));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
