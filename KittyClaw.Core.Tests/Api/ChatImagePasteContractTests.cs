using System.IO;
using System.Text.RegularExpressions;

namespace KittyClaw.Core.Tests.Api;

/// <summary>
/// Source-text contract tests for ticket #115 (image paste in chat). The plan in
/// comment #867 specifies the wiring across JS, Razor, contracts, endpoint,
/// runner, and service. Tests assert the contract on the committed source so
/// they are RED before the implementation lands and GREEN after.
/// </summary>
public class ChatImagePasteContractTests
{
    private static string LocateRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate {relative} from {AppContext.BaseDirectory}");
    }

    private static string Read(string relative) => File.ReadAllText(LocateRepoFile(relative));

    [Fact]
    public void Contracts_define_ChatImageDto_and_Images_field_on_ChatStartRequest()
    {
        var src = Read("KittyClaw.Web/Api/Contracts.cs");

        Assert.Matches(new Regex(@"record\s+ChatImageDto\s*\("), src);
        Assert.Contains("DataUrl", src);
        Assert.Contains("Mime", src);
        Assert.Contains("SizeBytes", src);

        // ChatStartRequest must expose an Images collection of ChatImageDto.
        Assert.Matches(new Regex(@"ChatStartRequest[\s\S]{0,400}?Images"), src);
        Assert.Matches(new Regex(@"IReadOnlyList<\s*ChatImageDto\s*>"), src);
    }

    [Fact]
    public void ClaudeRunContext_exposes_ImagePaths()
    {
        var src = Read("KittyClaw.Core/Automation/ClaudeRunner.cs");
        Assert.Matches(new Regex(@"ClaudeRunContext[\s\S]+?ImagePaths"), src);
        Assert.Matches(new Regex(@"IReadOnlyList<\s*string\s*>\??\s+ImagePaths"), src);
    }

    [Fact]
    public void ClaudeRunner_prompt_appends_attached_images_block_when_paths_present()
    {
        var src = Read("KittyClaw.Core/Automation/ClaudeRunner.cs");
        // Plan: BuildPromptAsync must emit an "[Attached images]" header listing the absolute paths.
        Assert.Contains("[Attached images]", src);
        // The block must be guarded by an ImagePaths null/empty check.
        Assert.Matches(new Regex(@"ImagePaths[\s\S]{0,200}?(Count\s*>\s*0|Any\(\)|!=\s*null)"), src);
    }

    [Fact]
    public void ClaudeRunner_cleans_up_image_temp_files_after_run()
    {
        var src = Read("KittyClaw.Core/Automation/ClaudeRunner.cs");
        // Plan: best-effort File.Delete on each ImagePaths entry after the process exits.
        Assert.Matches(new Regex(@"File\.Delete\([\s\S]{0,200}?ImagePaths|foreach[\s\S]{0,200}?ImagePaths[\s\S]{0,300}?File\.Delete"), src);
    }

    [Fact]
    public void ChatStart_endpoint_validates_images_and_returns_image_rejected_400()
    {
        var src = Read("KittyClaw.Web/Api/Endpoints.Chat.cs");
        // Plan: validation envelope uses error code "image_rejected".
        Assert.Contains("image_rejected", src);
        // Enforces a MIME allow-list (JPEG/PNG/GIF/WebP).
        Assert.Contains("image/png", src);
        Assert.Contains("image/jpeg", src);
        Assert.Contains("image/gif", src);
        Assert.Contains("image/webp", src);
        // Enforces a per-image size cap (5 MB) and a per-turn count cap (5).
        Assert.Matches(new Regex(@"5\s*\*\s*1024\s*\*\s*1024|5_?242_?880|5\s*\*\s*1_?048_?576"), src);
        // Returns 400 BadRequest on rejection.
        Assert.Matches(new Regex(@"Results\.BadRequest|StatusCode\s*=\s*400|return\s+BadRequest"), src);
    }

    [Fact]
    public void ChatStart_endpoint_writes_images_to_channel_tmp_and_forwards_paths_to_runner()
    {
        var src = Read("KittyClaw.Web/Api/Endpoints.Chat.cs");
        // Plan: decode data URL, write under <workspace>/.agents/channel/tmp/.
        Assert.Matches(new Regex(@"channel[/\\]+tmp"), src);
        // Forwards to ClaudeRunContext.ImagePaths.
        Assert.Matches(new Regex(@"ImagePaths\s*="), src);
    }

    [Fact]
    public void ChatService_persists_images_column_for_history_rerender()
    {
        var src = Read("KittyClaw.Core/Services/ChatService.cs");
        // Plan: inline ALTER TABLE adds imagesJson column for history re-render.
        Assert.Matches(new Regex(@"ALTER\s+TABLE[\s\S]{0,200}?imagesJson", RegexOptions.IgnoreCase), src);
    }

    [Fact]
    public void ChatDrawer_razor_installs_paste_handler_and_wires_jsinvokable_callbacks()
    {
        var src = Read("KittyClaw.Web/Components/ClaudeChatDrawer.razor");
        Assert.Contains("chatDrawerInstallPasteHandler", src);
        Assert.Contains("OnImagePasted", src);
        Assert.Contains("OnImagePasteError", src);
        // Per plan: image-only turn must be sendable (Send enabled when text empty but images attached).
        Assert.Matches(new Regex(@"_pendingImages[\s\S]{0,400}?Count\s*>\s*0|_pendingImages\.Count\s*>\s*0"), src);
    }

    [Fact]
    public void ChatDrawer_js_exports_paste_handler_with_mime_and_size_validation()
    {
        var src = Read("KittyClaw.Web/wwwroot/js/chat-drawer.js");
        Assert.Contains("chatDrawerInstallPasteHandler", src);
        // Reads clipboard items and filters images.
        Assert.Matches(new Regex(@"clipboardData[\s\S]{0,200}?items"), src);
        Assert.Matches(new Regex(@"image/"), src);
        // Per-image cap (5 MB) enforced client-side.
        Assert.Matches(new Regex(@"5\s*\*\s*1024\s*\*\s*1024|5242880|5_?242_?880"), src);
        // JSInvokable bridge.
        Assert.Contains("invokeMethodAsync", src);
        Assert.Contains("OnImagePasted", src);
    }

    [Fact]
    public void App_css_styles_paste_previews_and_error_banner()
    {
        var src = Read("KittyClaw.Web/wwwroot/app.css");
        Assert.Contains(".chat-paste-preview", src);
        Assert.Contains(".chat-paste-error", src);
    }
}
