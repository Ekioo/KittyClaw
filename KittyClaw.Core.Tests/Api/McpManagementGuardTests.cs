using System.Reflection;
using System.Text.Json;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Web.Api;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace KittyClaw.Core.Tests.Api;

public sealed class McpManagementGuardTests
{
    [Fact]
    public async Task EveryProjectTool_RejectsUnknownSlug_BeforeResolvingServicesOrCreatingDatabases()
    {
        using var directory = new TempDir();
        using var services = new ServiceCollection().BuildServiceProvider();
        var tools = new McpManagementTools(services, new ProjectService(directory.Path));
        var projectTools = typeof(McpManagementTools).GetMethods()
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null
                && m.GetParameters().Any(p => p.Name == "project"));

        foreach (var method in projectTools)
        {
            var args = method.GetParameters().Select(p => p.Name == "project"
                ? (object)"unknown-project"
                : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
            var error = await Assert.ThrowsAsync<McpException>(async () =>
                await (Task<JsonElement>)method.Invoke(tools, args)!);
            Assert.Contains("list_projects", error.Message);
        }

        Assert.False(File.Exists(Path.Combine(directory.Path, "projects", "unknown-project.db")));
    }
}
