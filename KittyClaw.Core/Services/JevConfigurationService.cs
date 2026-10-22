namespace KittyClaw.Core.Services;

public sealed record JevAvailability(bool Available, bool ConfiguredInKittyClaw, string? Source, string? Error = null);

/// <summary>Optional JEV credentials. Status detection is local and never calls TypeSafe.</summary>
public sealed class JevConfigurationService
{
    private const string GlobalScope = "typesafe";
    private readonly ProjectSecretVault _projects;
    private readonly ProjectSecretVault _global;
    private readonly Func<string?> _environmentKey;

    public JevConfigurationService(ProjectSecretVault projects, string dataDirectory,
        Func<string?>? environmentKey = null, IProjectSecretProtector? protector = null)
    {
        _projects = projects;
        // Separate namespace: global credentials cannot collide with project slugs or be injected into agents.
        _global = new ProjectSecretVault(Path.Combine(dataDirectory, "integrations"), protector);
        _environmentKey = environmentKey ?? ReadEnvironmentKey;
    }

    public async Task<JevAvailability> GetAsync(string? projectSlug = null, CancellationToken ct = default)
    {
        try
        {
            var configured = (await _global.ListAsync(GlobalScope, ct)).Any(s => s.Name == JevQualificationService.SecretName);
            var project = projectSlug is not null && (await _projects.ListAsync(projectSlug, ct))
                .Any(s => s.Name == JevQualificationService.SecretName);
            var source = project ? "project" : configured ? "kittyclaw" :
                !string.IsNullOrWhiteSpace(_environmentKey()) ? "environment" : null;
            return new(source is not null, configured, source);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return new(false, false, null, "The JEV secret vault could not be unlocked."); }
    }

    public Task SaveKeyAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("The TypeSafe API key cannot be empty.");
        return _global.SetAsync(GlobalScope, JevQualificationService.SecretName, key.Trim(), ct);
    }

    public async Task DeleteKeyAsync(CancellationToken ct = default) =>
        await _global.DeleteAsync(GlobalScope, JevQualificationService.SecretName, ct);

    internal async Task<string?> ResolveKeyAsync(string projectSlug, CancellationToken ct)
    {
        var project = await _projects.ReadForInjectionAsync(projectSlug, ct);
        if (project.TryGetValue(JevQualificationService.SecretName, out var projectKey) && !string.IsNullOrWhiteSpace(projectKey))
            return projectKey.Trim();
        var global = await _global.ReadForInjectionAsync(GlobalScope, ct);
        if (global.TryGetValue(JevQualificationService.SecretName, out var globalKey) && !string.IsNullOrWhiteSpace(globalKey))
            return globalKey.Trim();
        return _environmentKey()?.Trim();
    }

    private static string? ReadEnvironmentKey()
    {
        var key = Environment.GetEnvironmentVariable(JevQualificationService.SecretName);
        if (!string.IsNullOrWhiteSpace(key)) return key;
        if (!OperatingSystem.IsWindows()) return null;
        // A desktop app may predate a user environment change. Read it without mutating this process.
        key = Environment.GetEnvironmentVariable(JevQualificationService.SecretName, EnvironmentVariableTarget.User);
        return !string.IsNullOrWhiteSpace(key) ? key :
            Environment.GetEnvironmentVariable(JevQualificationService.SecretName, EnvironmentVariableTarget.Machine);
    }
}
