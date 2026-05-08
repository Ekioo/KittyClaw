using System.Net.Http.Headers;

namespace KittyClaw.QaRunner;

/// <summary>
/// Uploads PNG screenshots to a KittyClaw instance's <c>POST /api/images</c> endpoint
/// (the *target* host — typically the stable orchestrator that owns the ticket, not
/// the throwaway test instance the screenshot was taken from).
/// </summary>
public sealed class ScreenshotUploader
{
    private readonly HttpClient _http;
    private readonly string _targetApi;

    public ScreenshotUploader(string targetApi, HttpClient? http = null)
    {
        _targetApi = targetApi.TrimEnd('/');
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>Returns the URL the server reports for the uploaded file (e.g. <c>/uploads/abc123.png</c>).</summary>
    public async Task<string> UploadAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("Screenshot not found", filePath);

        await using var fs = File.OpenRead(filePath);
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(fs);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(fileContent, "file", Path.GetFileName(filePath));

        var resp = await _http.PostAsync($"{_targetApi}/api/images", content, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);
        // Response shape: {"url":"/uploads/{filename}"}
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("url").GetString()
            ?? throw new InvalidOperationException("Upload response missing 'url'");
    }
}
