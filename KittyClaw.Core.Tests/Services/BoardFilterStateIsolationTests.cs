using Microsoft.Extensions.DependencyInjection;
using KittyClaw.Web.Services;

namespace KittyClaw.Core.Tests.Services;

// These tests encode the expected DI lifetime for BoardFilterState.
// The source-registration tests are RED with AddSingleton and GREEN after the AddScoped fix.
// The behaviour tests are always green — they document correct isolation behaviour.
public class BoardFilterStateIsolationTests
{
    // Case 1 & 3: The real Program.cs MUST register BoardFilterState as Scoped.
    // RED: AddSingleton is currently in Program.cs.
    // GREEN: once programmer changes to AddScoped.
    [Fact]
    public void Program_MustRegister_BoardFilterState_AsScoped()
    {
        var programCs = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "KittyClaw.Web", "Program.cs"));

        Assert.True(File.Exists(programCs), $"Program.cs not found at {programCs}");

        var content = File.ReadAllText(programCs);
        Assert.Contains(
            "AddScoped<KittyClaw.Web.Services.BoardFilterState>()",
            content);
    }

    // Behaviour: two independent scopes must receive different instances (cross-tab isolation).
    [Fact]
    public void TwoScopes_ReceiveDifferentInstances_WhenRegisteredAsScoped()
    {
        var services = new ServiceCollection();
        services.AddScoped<BoardFilterState>();
        using var provider = services.BuildServiceProvider();

        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();

        var a = scope1.ServiceProvider.GetRequiredService<BoardFilterState>();
        var b = scope2.ServiceProvider.GetRequiredService<BoardFilterState>();

        Assert.NotSame(a, b);
    }

    // Case 3: A new scope must start with an empty filter even if another scope set a value.
    [Fact]
    public void NewScope_StartsEmpty_WhenRegisteredAsScoped()
    {
        var services = new ServiceCollection();
        services.AddScoped<BoardFilterState>();
        using var provider = services.BuildServiceProvider();

        using var scope1 = provider.CreateScope();
        scope1.ServiceProvider.GetRequiredService<BoardFilterState>().FilterText = "search term";

        using var scope2 = provider.CreateScope();
        var state2 = scope2.ServiceProvider.GetRequiredService<BoardFilterState>();

        Assert.Equal("", state2.FilterText);
    }

    // Case 2: Filter text set in a scope persists within that same scope.
    [Fact]
    public void WithinScope_FilterText_PersistsBetweenResolves()
    {
        var services = new ServiceCollection();
        services.AddScoped<BoardFilterState>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<BoardFilterState>().FilterText = "todo";

        var state2 = scope.ServiceProvider.GetRequiredService<BoardFilterState>();
        Assert.Equal("todo", state2.FilterText);
    }

    // Edge case: Filter can be set and then cleared within one scope.
    [Fact]
    public void WithinScope_FilterText_CanBeSetAndCleared()
    {
        var services = new ServiceCollection();
        services.AddScoped<BoardFilterState>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var state = scope.ServiceProvider.GetRequiredService<BoardFilterState>();
        state.FilterText = "kanban";
        Assert.Equal("kanban", state.FilterText);

        state.FilterText = "";
        Assert.Equal("", state.FilterText);
    }
}
