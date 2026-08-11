using KittyClaw.Core.Automation;

namespace KittyClaw.Core.Tests.Automation;

public sealed class ProcessLifecycleWorktreeTests
{
    [Fact]
    public void ProcessUsesExecutionWorktree_WhileControlWorkspaceRemainsAvailable()
    {
        var context = new AgentRunContext
        {
            ProjectSlug = "project",
            WorkspacePath = Path.GetFullPath("control"),
            ExecutionWorkspacePath = Path.GetFullPath("worktree"),
            AgentName = "agent",
            SkillFile = "agent/SKILL.md",
        };

        var startInfo = ProcessLifecycleManager.BuildProcessStartInfo(context, [], "test-agent");

        Assert.Equal(context.ExecutionWorkspacePath, startInfo.WorkingDirectory);
        Assert.NotEqual(context.WorkspacePath, startInfo.WorkingDirectory);
    }
}
