using System.Text.Json;
using KittyClaw.Core.Automation;

namespace KittyClaw.Core.Tests.Automation;

public sealed class PrimaryCheckoutChangeAttributionTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "kittyclaw-primary-repo");

    private static string Fingerprint(string status = "", string diff = "", params string[] untrackedEntries) =>
        status + "\0" + diff + string.Concat(untrackedEntries.Select(entry => "\0" + entry));

    [Fact]
    public void ChangedPaths_DetectsUntrackedStatusAndDiffOnlyChanges()
    {
        var before = Fingerprint(
            status: " M same.txt\n M rebased.txt\n",
            diff: "diff --git a/rebased.txt b/rebased.txt\nindex 111..222 100644\n-old\n+new\n",
            "kept.txt:AAA", "edited.txt:AAA", "removed.txt:AAA");
        var after = Fingerprint(
            status: " M same.txt\n M rebased.txt\n?? appeared.txt\n",
            // Same porcelain status but a different HEAD-side blob: the fast-forward case.
            diff: "diff --git a/rebased.txt b/rebased.txt\nindex 333..222 100644\n-old\n+new\n",
            "kept.txt:AAA", "edited.txt:BBB", "appeared.txt:CCC");

        var changed = PrimaryCheckoutChangeAttribution.ChangedPaths(before, after);

        Assert.Equal(["appeared.txt", "edited.txt", "rebased.txt", "removed.txt"], changed);
    }

    [Fact]
    public void ChangedPaths_IdenticalFingerprints_ReportNothing() =>
        Assert.Empty(PrimaryCheckoutChangeAttribution.ChangedPaths(
            Fingerprint(" M a.txt\n", "diff --git a/a.txt b/a.txt\nindex 1..2\n", "u.txt:AAA"),
            Fingerprint(" M a.txt\n", "diff --git a/a.txt b/a.txt\nindex 1..2\n", "u.txt:AAA")));

    [Theory]
    [InlineData("Write", true)]
    [InlineData("Edit", true)]
    [InlineData("Read", false)]
    [InlineData("Glob", false)]
    public void WriteToolTargetingChangedPrimaryPath_IsEvidenceOnlyForWriteCapableTools(string tool, bool expected)
    {
        var detail = JsonSerializer.Serialize(new { file_path = Path.Combine(Root, "intrusion.txt"), content = "x" });

        Assert.Equal(expected, PrimaryCheckoutChangeAttribution.IsAgentWriteEvidence(
            tool, detail, Root, ["intrusion.txt"]));
    }

    [Fact]
    public void WriteTool_WithRelativeOrForeignPath_IsNotEvidence()
    {
        var relative = JsonSerializer.Serialize(new { file_path = "intrusion.txt", content = "x" });
        var foreign = JsonSerializer.Serialize(new
        {
            file_path = Path.Combine(Path.GetTempPath(), "elsewhere", "intrusion.txt"),
            content = "x",
        });

        Assert.False(PrimaryCheckoutChangeAttribution.IsAgentWriteEvidence("Write", relative, Root, ["intrusion.txt"]));
        Assert.False(PrimaryCheckoutChangeAttribution.IsAgentWriteEvidence("Write", foreign, Root, ["intrusion.txt"]));
    }

    [Fact]
    public void WriteTool_WhoseDeclaredTargetDidNotChange_IsNotEvidence()
    {
        // A denied or failed write must not absorb a drift that came from somewhere else.
        var detail = JsonSerializer.Serialize(new { file_path = Path.Combine(Root, "denied.txt"), content = "x" });

        Assert.False(PrimaryCheckoutChangeAttribution.IsAgentWriteEvidence(
            "Write", detail, Root, ["unrelated.txt"]));
    }

    [Fact]
    public void MutatingGitCommandReferencingPrimary_IsEvidence_WhileReadOnlyIsNot()
    {
        var slashRoot = Root.Replace('\\', '/');
        var mutating = JsonSerializer.Serialize(new { command = $"git -C {slashRoot} stash pop" });
        var readOnly = JsonSerializer.Serialize(new { command = $"git -C {slashRoot} log --oneline dev" });

        Assert.True(PrimaryCheckoutChangeAttribution.IsAgentWriteEvidence("Bash", mutating, Root, []));
        Assert.False(PrimaryCheckoutChangeAttribution.IsAgentWriteEvidence("Bash", readOnly, Root, []));
    }

    [Fact]
    public void CommandNamingAChangedPrimaryPath_IsEvidence_EvenWithEscapedBackslashes()
    {
        // Raw tool_use detail keeps JSON escaping, so Windows paths arrive with doubled backslashes.
        var detail = JsonSerializer.Serialize(new
        {
            command = $"echo intruded > {Path.Combine(Root, "intrusion.txt")}",
        });

        Assert.True(PrimaryCheckoutChangeAttribution.IsAgentWriteEvidence("Bash", detail, Root, ["intrusion.txt"]));
        Assert.True(PrimaryCheckoutChangeAttribution.MentionsPrimaryRepository(detail, Root));
    }

    [Fact]
    public void CommandNotReferencingPrimary_IsNotEvidenceAndNotCollected()
    {
        var detail = JsonSerializer.Serialize(new { command = "echo hello" });

        Assert.False(PrimaryCheckoutChangeAttribution.IsAgentWriteEvidence("Bash", detail, Root, ["intrusion.txt"]));
        Assert.False(PrimaryCheckoutChangeAttribution.MentionsPrimaryRepository(detail, Root));
    }
}
