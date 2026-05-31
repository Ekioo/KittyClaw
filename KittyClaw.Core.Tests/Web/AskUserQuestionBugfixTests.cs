using System.IO;
using System.Text.RegularExpressions;

namespace KittyClaw.Core.Tests.Web;

// Source-text tests for [PLAN-REVISED] bugfixes on the AskUserQuestion widget.
// Cases A-E are all RED on dev before the fix.

public class AskUserQuestionBugfixTests
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

    private static string AgentRunSrc() =>
        LoadFile(Path.Combine("KittyClaw.Core", "Automation", "AgentRun.cs"));

    private static string StreamPumpSrc() =>
        LoadFile(Path.Combine("KittyClaw.Core", "Automation", "ClaudeStreamPump.cs"));

    private static string ClaudeRunnerSrc() =>
        LoadFile(Path.Combine("KittyClaw.Core", "Automation", "ClaudeRunner.cs"));

    private static string AppCss() =>
        LoadFile(Path.Combine("KittyClaw.Web", "wwwroot", "app.css"));

    // Case A: ClaudeStreamPump sets run.IsAwaitingUserAnswer = true when emitting ask_user_question
    [Fact]
    public void StreamPump_SetsIsAwaitingUserAnswer_True_OnAskUserQuestion()
    {
        var pump = StreamPumpSrc();
        // The flag must be set in AgentRun
        Assert.Contains("IsAwaitingUserAnswer", AgentRunSrc());
        // StreamPump must assign it to true
        Assert.Matches(new Regex(@"IsAwaitingUserAnswer\s*=\s*true", RegexOptions.Multiline), pump);
    }

    // Case B: ClaudeRunner reads answer from SteeringQueue, resets flag, adds to PendingSteerMessages
    [Fact]
    public void ClaudeRunner_ResetsIsAwaitingUserAnswer_AfterReadingSteeringQueue()
    {
        var runner = ClaudeRunnerSrc();
        // Runner must wait on SteeringQueue when IsAwaitingUserAnswer is true
        Assert.Matches(new Regex(@"IsAwaitingUserAnswer", RegexOptions.Multiline), runner);
        // After reading, flag must be reset to false
        Assert.Matches(new Regex(@"IsAwaitingUserAnswer\s*=\s*false", RegexOptions.Multiline), runner);
        // And answer must be queued for replay
        Assert.Contains("AddPendingSteerMessage", runner);
    }

    // Case C: ClaudeRunner wait has a timeout to avoid deadlock if user never answers
    [Fact]
    public void ClaudeRunner_WaitForAnswer_HasTimeout()
    {
        var runner = ClaudeRunnerSrc();
        // Must use a cancellation token or explicit TimeSpan timeout near the SteeringQueue read
        Assert.Matches(new Regex(
            @"IsAwaitingUserAnswer[\s\S]{0,800}(TimeSpan|CancellationToken|timeout|Timeout|WaitAsync|TryRead|ReadAsync)",
            RegexOptions.Multiline | RegexOptions.IgnoreCase), runner);
    }

    // Case D: .chat-ask-user .chat-bubble must occupy full width
    [Fact]
    public void Css_ChatAskUserBubble_HasFullWidth()
    {
        var css = AppCss();
        // Find the .chat-ask-user .chat-bubble block and verify it contains width:100% and max-width:100%
        Assert.Matches(new Regex(
            @"\.chat-ask-user\s+\.chat-bubble\s*\{[^}]*width\s*:\s*100%",
            RegexOptions.Multiline), css);
        Assert.Matches(new Regex(
            @"\.chat-ask-user\s+\.chat-bubble\s*\{[^}]*max-width\s*:\s*100%",
            RegexOptions.Multiline), css);
    }

    // Case E: .chat-ask-* rules must use CSS variables (var(--...)) not hardcoded hex colours
    [Fact]
    public void Css_ChatAskUserWidget_UsesCssVariables_NotHardcodedHex()
    {
        var css = AppCss();
        // The background of .chat-ask-user .chat-bubble must reference a CSS variable
        Assert.Matches(new Regex(
            @"\.chat-ask-user\s+\.chat-bubble\s*\{[^}]*background\s*:[^}]*var\(--",
            RegexOptions.Multiline), css);
        // The background of .chat-ask-option must reference a CSS variable
        Assert.Matches(new Regex(
            @"\.chat-ask-option\s*\{[^}]*background\s*:[^}]*var\(--",
            RegexOptions.Multiline), css);
        // The background of .chat-ask-submit must reference a CSS variable
        Assert.Matches(new Regex(
            @"\.chat-ask-submit\s*\{[^}]*background\s*:[^}]*var\(--",
            RegexOptions.Multiline), css);
    }
}
