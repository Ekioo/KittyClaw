using System.IO;
using System.Text.RegularExpressions;

namespace KittyClaw.Core.Tests.Web;

// Source-text tests for [PLAN-REVISED] Cases F-J: real CLI schema support in ParseAskUserQuestionDetail.
// All assertions are RED on dev because the parser does not yet handle the real schema.

public class AskUserQuestionSchemaTests
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
        LoadFile(Path.Combine("KittyClaw.Web", "Components", "ChatDrawer.razor"));

    private static string MockNdjson() =>
        LoadFile(Path.Combine("KittyClaw.ClaudeMock", "scenarios", "ask-user-question.ndjson"));

    // Case F: ParseAskUserQuestionDetail reads questions[0].question for the real CLI schema
    [Fact]
    public void Parser_ExtractsPrompt_FromQuestionsArray_RealSchema()
    {
        var src = ChatDrawer();
        // Must branch on "questions" property and read "question" field from first element
        Assert.Matches(new Regex(
            @"ParseAskUserQuestionDetail[\s\S]{0,2000}TryGetProperty\s*\(\s*""questions""",
            RegexOptions.Multiline), src);
        Assert.Matches(new Regex(
            @"TryGetProperty\s*\(\s*""questions""[\s\S]{0,800}TryGetProperty\s*\(\s*""question""",
            RegexOptions.Multiline), src);
    }

    // Case G: When real schema is used, prompt is non-empty (question field extracted, not fallback text)
    [Fact]
    public void Parser_ExtractsRealPrompt_NotFallback_WhenQuestionsPresent()
    {
        var src = ChatDrawer();
        // The "questions" branch must come BEFORE the fallback "prompt" branch so real schema takes priority
        var idxQuestions = src.IndexOf("\"questions\"", StringComparison.Ordinal);
        var idxFallbackPrompt = src.LastIndexOf("\"prompt\"", StringComparison.Ordinal);
        Assert.True(idxQuestions >= 0, "\"questions\" not found in ChatDrawer.razor");
        Assert.True(idxFallbackPrompt >= 0, "\"prompt\" not found in ChatDrawer.razor");
        Assert.True(idxQuestions < idxFallbackPrompt,
            "\"questions\" branch must appear before fallback \"prompt\" branch in ParseAskUserQuestionDetail");
    }

    // Case H: Parser extracts options from options[*].label (object array, not string array)
    [Fact]
    public void Parser_ExtractsOptions_FromLabelField_NotRawStrings()
    {
        var src = ChatDrawer();
        // Must read a "label" property from each option object
        Assert.Matches(new Regex(
            @"TryGetProperty\s*\(\s*""questions""[\s\S]{0,1200}TryGetProperty\s*\(\s*""label""",
            RegexOptions.Multiline), src);
    }

    // Case I: Fallback for old schema {prompt:"P", options:["A","B"]} still present (backward compat)
    [Fact]
    public void Parser_HasFallback_ForOldPromptOptionsSchema()
    {
        var src = ChatDrawer();
        // Both the new "questions" path and the old "prompt" fallback must coexist in the same method
        Assert.Matches(new Regex(
            @"TryGetProperty\s*\(\s*""questions""[\s\S]{0,2000}TryGetProperty\s*\(\s*""prompt""",
            RegexOptions.Multiline), src);
    }

    // Case J: Mock scenario uses real CLI schema with "questions" array, not legacy "prompt" root field
    [Fact]
    public void Mock_AskUserQuestion_UsesRealSchema_WithQuestionsArray()
    {
        var ndjson = MockNdjson();
        // Must contain "questions" in the tool input
        Assert.Contains("\"questions\"", ndjson);
        // Must NOT have "prompt" as a top-level key in the AskUserQuestion input
        // (the real schema nests it under questions[0].question)
        Assert.DoesNotMatch(new Regex(
            @"""name""\s*:\s*""AskUserQuestion""[\s\S]{0,400}""prompt""\s*:",
            RegexOptions.Multiline), ndjson);
    }
}
