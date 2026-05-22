using System.IO;
using System.Text.RegularExpressions;

namespace KittyClaw.Core.Tests.Web;

// Source-text tests for AskUserQuestion support.
// All assertions are RED on dev because the feature is not yet implemented.

public class AskUserQuestionChatTests
{
    private static string LoadFile(string relativePath)
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "KittyClaw.sln"))
                               && !File.Exists(Path.Combine(dir, "KittyClaw.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        Assert.NotNull(dir);
        var path = Path.Combine(dir!, relativePath);
        Assert.True(File.Exists(path), $"File not found: {path}");
        return File.ReadAllText(path);
    }

    private static string ChatDrawer() =>
        LoadFile(Path.Combine("KittyClaw.Web", "Components", "ClaudeChatDrawer.razor"));

    private static string EndpointsChat() =>
        LoadFile(Path.Combine("KittyClaw.Web", "Api", "Endpoints.Chat.cs"));

    private static string StreamPump() =>
        LoadFile(Path.Combine("KittyClaw.Core", "Automation", "ClaudeStreamPump.cs"));

    // ClaudeStreamPump — Case 1: AskUserQuestion tool_use must be emitted as "ask_user_question" kind
    [Fact]
    public void StreamPump_EmitsAskUserQuestionKind_ForAskUserQuestionToolUse()
    {
        var src = StreamPump();
        Assert.Matches(new Regex(@"AskUserQuestion"), src);
        Assert.Matches(new Regex(@"""ask_user_question"""), src);
    }

    // ClaudeChatDrawer — Case 1: AskUserQuestionMessage record is defined
    [Fact]
    public void ChatDrawer_DefinesAskUserQuestionMessage_Record()
    {
        var src = ChatDrawer();
        Assert.Contains("AskUserQuestionMessage", src);
    }

    // ClaudeChatDrawer — Case 1: ReceiveSse handles "ask_user_question" kind and adds AskUserQuestionMessage
    [Fact]
    public void ChatDrawer_ReceiveSse_HandlesAskUserQuestionKind_And_AddsMessage()
    {
        var src = ChatDrawer();
        Assert.Matches(new Regex(@"""ask_user_question"""), src);
        Assert.Matches(new Regex(@"new\s+AskUserQuestionMessage"), src);
    }

    // ClaudeChatDrawer — Case 2: free-text prompt renders a textarea element
    [Fact]
    public void ChatDrawer_Renders_TextareaForFreeTextPrompt()
    {
        var src = ChatDrawer();
        Assert.Matches(new Regex(@"AskUserQuestionMessage[\s\S]{0,2000}<textarea", RegexOptions.Multiline), src);
    }

    // ClaudeChatDrawer — Case 3: multiple-choice prompt iterates over Options to render buttons
    [Fact]
    public void ChatDrawer_Renders_ChoiceButtonsForOptions()
    {
        var src = ChatDrawer();
        Assert.Matches(new Regex(@"AskUserQuestionMessage[\s\S]{0,2000}\.Options", RegexOptions.Multiline), src);
    }

    // ClaudeChatDrawer — Case 4: SubmitAskUserQuestion method exists
    [Fact]
    public void ChatDrawer_HasSubmitAskUserQuestion_Method()
    {
        var src = ChatDrawer();
        Assert.Contains("SubmitAskUserQuestion", src);
    }

    // ClaudeChatDrawer — Case 4: prompt widget becomes disabled/answered after submission
    [Fact]
    public void ChatDrawer_PromptBecomesAnswered_AfterSubmit()
    {
        var src = ChatDrawer();
        // Must have an "answered" or "IsAnswered" flag on AskUserQuestionMessage
        Assert.Matches(new Regex(@"AskUserQuestionMessage[\s\S]{0,3000}(IsAnswered|isAnswered|Answered\s*=\s*true|answered)", RegexOptions.Multiline), src);
    }

    // ClaudeChatDrawer — Case 5: history reload reconstructs AskUserQuestionMessage for role "ask_user_question"
    [Fact]
    public void ChatDrawer_HistoryReload_ReconstructsAskUserQuestionMessage()
    {
        var src = ChatDrawer();
        Assert.Matches(new Regex(
            @"""ask_user_question""[\s\S]{0,500}AskUserQuestionMessage|AskUserQuestionMessage[\s\S]{0,500}""ask_user_question""",
            RegexOptions.Multiline), src);
    }

    // ClaudeChatDrawer — Case 6: submit is guarded against null _activeRunId
    [Fact]
    public void ChatDrawer_SubmitGuard_ChecksActiveRunIdIsNotNull()
    {
        var src = ChatDrawer();
        Assert.Matches(new Regex(@"SubmitAskUserQuestion[\s\S]{0,600}_activeRunId\s*(==|is)\s*null|_activeRunId\s*(==|is)\s*null[\s\S]{0,600}SubmitAskUserQuestion", RegexOptions.Multiline), src);
    }

    // ClaudeChatDrawer — Case 7: empty prompt text falls back to a placeholder string
    [Fact]
    public void ChatDrawer_EmptyPromptText_ShowsFallbackPlaceholder()
    {
        var src = ChatDrawer();
        // Must use IsNullOrEmpty/IsNullOrWhiteSpace on the prompt in the AskUserQuestionMessage rendering block
        Assert.Matches(new Regex(
            @"AskUserQuestionMessage[\s\S]{0,3000}(IsNullOrEmpty|IsNullOrWhiteSpace)[\s\S]{0,200}(asking for input|prompt|fallback)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline), src);
    }

    // Endpoints.Chat.cs — PersistChatEvent must handle "ask_user_question" kind
    [Fact]
    public void EndpointsChat_PersistChatEvent_HandlesAskUserQuestionKind()
    {
        var src = EndpointsChat();
        Assert.Matches(new Regex(@"ask_user_question"), src);
    }
}
