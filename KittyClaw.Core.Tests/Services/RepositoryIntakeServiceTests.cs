using System.Text.Json;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Tests.Services;

public sealed class RepositoryIntakeServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"kittyclaw-intake-{Guid.NewGuid():N}");

    [Fact]
    public async Task ValidRepository_PersistsStableStateAndCorrelatedEvents()
    {
        var repo = await CreateGitRepositoryAsync();
        var service = new RepositoryIntakeService(Path.Combine(_root, "data"));

        var state = await service.SelectAndValidateAsync(null, repo, "Ship the first result");

        Assert.True(state.IsValidated);
        Assert.Equal("Ship the first result", state.Objective);
        Assert.NotNull(state.ValidatedAt);
        Assert.Equal(state, await service.LoadAsync(state.JourneyId));
        var events = await ReadEventsAsync();
        Assert.Equal(["repository_selected", "repository_validated"], events.Select(e => e.Name));
        Assert.All(events, e => Assert.Equal(state.JourneyId, e.JourneyId));
    }

    [Fact]
    public async Task InvalidPathAndNonGitFolder_AreActionableAndKeepObjectiveForRetry()
    {
        var service = new RepositoryIntakeService(Path.Combine(_root, "data"));
        var missing = Path.Combine(_root, "missing");
        var missingError = await Assert.ThrowsAsync<RepositoryIntakeException>(
            () => service.SelectAndValidateAsync(null, missing, "Keep me"));
        Assert.Equal("RepositoryPathMissing", missingError.ErrorCode);

        var folder = Path.Combine(_root, "plain");
        Directory.CreateDirectory(folder);
        var nonGitError = await Assert.ThrowsAsync<RepositoryIntakeException>(
            () => service.SelectAndValidateAsync(null, folder, "Keep me too"));
        Assert.Equal("RepositoryNotGit", nonGitError.ErrorCode);

        var failures = (await ReadEventsAsync()).Where(e => e.Name == "repository_intake_failed").ToList();
        Assert.Equal(2, failures.Count);
        Assert.Contains(failures, e => e.ErrorCode == "RepositoryPathMissing");
        Assert.Contains(failures, e => e.ErrorCode == "RepositoryNotGit");
    }

    [Fact]
    public async Task FailedJourney_CanResumeSuccessfullyWithSameCorrelationId()
    {
        var service = new RepositoryIntakeService(Path.Combine(_root, "data"));
        var repo = Path.Combine(_root, "retry");
        Directory.CreateDirectory(repo);
        var error = await Assert.ThrowsAsync<RepositoryIntakeException>(
            () => service.SelectAndValidateAsync("journey-1", repo, "Retained objective"));
        Assert.Equal("RepositoryNotGit", error.ErrorCode);
        await RunGitAsync(repo, "init");

        var state = await service.SelectAndValidateAsync("journey-1", repo, "Retained objective");

        Assert.True(state.IsValidated);
        Assert.Equal("journey-1", state.JourneyId);
        Assert.Equal("Retained objective", state.Objective);
        Assert.Equal(4, (await ReadEventsAsync()).Count(e => e.JourneyId == "journey-1"));
    }

    private async Task<string> CreateGitRepositoryAsync()
    {
        var path = Path.Combine(_root, "repo");
        Directory.CreateDirectory(path);
        await RunGitAsync(path, "init");
        return path;
    }

    private static async Task RunGitAsync(string path, string arguments)
    {
        var result = await ProcessRunner.RunAsync("git", arguments, path, TimeSpan.FromSeconds(10));
        Assert.True(result.Success, result.Stderr);
    }

    private async Task<List<RepositoryIntakeEvent>> ReadEventsAsync()
    {
        var lines = await File.ReadAllLinesAsync(Path.Combine(_root, "data", "activation", "repository-intake-events.jsonl"));
        return lines.Select(line => JsonSerializer.Deserialize<RepositoryIntakeEvent>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToList();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
