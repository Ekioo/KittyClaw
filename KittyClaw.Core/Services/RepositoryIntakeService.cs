using System.Text.Json;

namespace KittyClaw.Core.Services;

public sealed class RepositoryIntakeService
{
    private readonly string _stateDirectory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public RepositoryIntakeService(string dataDirectory)
    {
        _stateDirectory = Path.Combine(dataDirectory, "activation");
    }

    public async Task<RepositoryIntakeState> SelectAndValidateAsync(
        string? journeyId, string path, string objective, CancellationToken ct = default)
    {
        journeyId = string.IsNullOrWhiteSpace(journeyId) ? Guid.NewGuid().ToString("N") : journeyId;
        var selectedAt = DateTimeOffset.UtcNow;
        var normalizedPath = path.Trim();
        await AppendEventAsync(new RepositoryIntakeEvent(journeyId, "repository_selected", selectedAt, normalizedPath), ct);

        try
        {
            ProjectService.ValidateWorkspacePath(normalizedPath);
            if (!Directory.Exists(normalizedPath))
                throw new RepositoryIntakeException(journeyId, "RepositoryPathMissing", "The selected folder does not exist.");

            var git = await ProcessRunner.RunAsync(
                "git", "rev-parse --is-inside-work-tree", normalizedPath, TimeSpan.FromSeconds(10), ct: ct);
            if (!git.Success || !git.Stdout.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
                throw new RepositoryIntakeException(journeyId, "RepositoryNotGit", "The selected folder is not a usable Git repository.");

            var probePath = Path.Combine(normalizedPath, $".kittyclaw-write-probe-{Guid.NewGuid():N}");
            try
            {
                await File.WriteAllTextAsync(probePath, string.Empty, ct);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                throw new RepositoryIntakeException(journeyId, "RepositoryNotWritable", "KittyClaw cannot write to the selected repository.", ex);
            }
            finally
            {
                if (File.Exists(probePath)) File.Delete(probePath);
            }

            var state = new RepositoryIntakeState(
                journeyId, normalizedPath, objective.Trim(), selectedAt, DateTimeOffset.UtcNow, true, null);
            await SaveStateAsync(state, ct);
            await AppendEventAsync(new RepositoryIntakeEvent(journeyId, "repository_validated", state.ValidatedAt!.Value, normalizedPath), ct);
            return state;
        }
        catch (RepositoryIntakeException ex)
        {
            var state = new RepositoryIntakeState(
                journeyId, normalizedPath, objective.Trim(), selectedAt, null, false, ex.ErrorCode);
            await SaveStateAsync(state, ct);
            await AppendEventAsync(new RepositoryIntakeEvent(journeyId, "repository_intake_failed", DateTimeOffset.UtcNow, normalizedPath, ex.ErrorCode), ct);
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            const string errorCode = "RepositoryPathInvalid";
            var state = new RepositoryIntakeState(
                journeyId, normalizedPath, objective.Trim(), selectedAt, null, false, errorCode);
            await SaveStateAsync(state, ct);
            await AppendEventAsync(new RepositoryIntakeEvent(journeyId, "repository_intake_failed", DateTimeOffset.UtcNow, normalizedPath, errorCode), ct);
            throw new RepositoryIntakeException(journeyId, errorCode, ex.Message, ex);
        }
    }

    public async Task<RepositoryIntakeState?> LoadAsync(string journeyId, CancellationToken ct = default)
    {
        var path = StatePath(journeyId);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<RepositoryIntakeState>(stream, JsonOptions, ct);
    }

    private async Task SaveStateAsync(RepositoryIntakeState state, CancellationToken ct)
    {
        Directory.CreateDirectory(_stateDirectory);
        var target = StatePath(state.JourneyId);
        var temp = target + ".tmp";
        await _writeLock.WaitAsync(ct);
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state, JsonOptions), ct);
            File.Move(temp, target, true);
        }
        finally { _writeLock.Release(); }
    }

    private async Task AppendEventAsync(RepositoryIntakeEvent intakeEvent, CancellationToken ct)
    {
        Directory.CreateDirectory(_stateDirectory);
        await _writeLock.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(
                Path.Combine(_stateDirectory, "repository-intake-events.jsonl"),
                JsonSerializer.Serialize(intakeEvent, JsonOptions) + Environment.NewLine, ct);
        }
        finally { _writeLock.Release(); }
    }

    private string StatePath(string journeyId) =>
        Path.Combine(_stateDirectory, $"repository-intake-{journeyId}.json");
}

public sealed record RepositoryIntakeState(
    string JourneyId,
    string RepositoryPath,
    string Objective,
    DateTimeOffset SelectedAt,
    DateTimeOffset? ValidatedAt,
    bool IsValidated,
    string? ErrorCode);

public sealed record RepositoryIntakeEvent(
    string JourneyId,
    string Name,
    DateTimeOffset OccurredAt,
    string RepositoryPath,
    string? ErrorCode = null);

public sealed class RepositoryIntakeException : Exception
{
    public RepositoryIntakeException(string journeyId, string errorCode, string message, Exception? inner = null) : base(message, inner)
    {
        JourneyId = journeyId;
        ErrorCode = errorCode;
    }

    public string JourneyId { get; }
    public string ErrorCode { get; }
}
