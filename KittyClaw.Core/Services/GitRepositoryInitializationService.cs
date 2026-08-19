using System.Collections.Concurrent;
using System.Diagnostics;

namespace KittyClaw.Core.Services;

/// <summary>
/// Detects and safely initializes the repository attached to a project workspace.
/// Existing workspace files are never staged or committed.
/// </summary>
public sealed class GitRepositoryInitializationService(ProjectService projects)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public async Task<GitRepositoryStatus?> GetStatusAsync(string slug)
    {
        var project = await projects.GetProjectAsync(slug);
        if (project is null) return null;

        var workspace = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(projects.ResolveWorkspacePath(project)));
        var workspaceConfigured = !string.IsNullOrWhiteSpace(project.WorkspacePath);
        var workspaceExists = Directory.Exists(workspace);
        var gitAvailable = RunGit(null, ["--version"]).Success;
        if (!workspaceConfigured || !workspaceExists || !gitAvailable)
            return new(workspace, workspaceConfigured, workspaceExists, gitAvailable, false, false, null, null);

        var hasGitMetadata = HasGitMetadata(workspace);

        var topLevel = RunGit(workspace, ["rev-parse", "--show-toplevel"]);
        if (!topLevel.Success || string.IsNullOrWhiteSpace(topLevel.Output))
            return new(workspace, true, true, true, hasGitMetadata, false, null, null);

        var repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(topLevel.Output.Trim()));
        var branch = RunGit(workspace, ["branch", "--show-current"]);
        return new(workspace, true, true, true, hasGitMetadata, true, repositoryRoot,
            branch.Success ? NullIfEmpty(branch.Output) : null);
    }

    public async Task<GitRepositoryInitializationResult?> InitializeAsync(string slug)
    {
        var project = await projects.GetProjectAsync(slug);
        if (project is null) return null;
        if (string.IsNullOrWhiteSpace(project.WorkspacePath))
            throw new InvalidOperationException("Configurez d’abord le dossier de travail du projet.");

        var workspace = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(projects.ResolveWorkspacePath(project)));
        if (!Directory.Exists(workspace))
            throw new InvalidOperationException($"Le dossier de travail '{workspace}' n’existe pas.");
        if (!RunGit(null, ["--version"]).Success)
            throw new InvalidOperationException("Git n’est pas installé ou n’est pas disponible dans le PATH.");

        var gate = PathLocks.GetOrAdd(workspace, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (HasGitMetadata(workspace))
                throw new GitRepositoryAlreadyExistsException("Ce dossier de travail contient déjà des métadonnées Git. Aucun changement n’a été effectué.");
            var existing = RunGit(workspace, ["rev-parse", "--show-toplevel"]);
            if (existing.Success)
                throw new GitRepositoryAlreadyExistsException("Ce dossier de travail appartient déjà à un dépôt Git. Aucun changement n’a été effectué.");

            var branch = string.IsNullOrWhiteSpace(project.IntegrationBranch)
                ? "main"
                : project.IntegrationBranch.Trim();
            var validBranch = RunGit(workspace, ["check-ref-format", "--branch", branch]);
            if (!validBranch.Success)
                throw new InvalidOperationException($"La branche d’intégration '{branch}' est invalide.");

            var initialized = RunGit(workspace, ["init", "--initial-branch", branch]);
            if (!initialized.Success)
                throw new InvalidOperationException(GitFailure("Impossible d’initialiser le dépôt Git", initialized));

            return new(workspace, branch, "Le dépôt Git local est initialisé. Aucun fichier existant n’a été ajouté.");
        }
        finally
        {
            gate.Release();
        }
    }

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool HasGitMetadata(string workspace)
    {
        var metadata = Path.Combine(workspace, ".git");
        return Directory.Exists(metadata) || File.Exists(metadata);
    }

    private static string GitFailure(string prefix, GitResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        return string.IsNullOrWhiteSpace(detail) ? prefix + "." : $"{prefix} : {detail.Trim()}";
    }

    private static GitResult RunGit(string? workingDirectory, IReadOnlyList<string> arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process is null) return new(false, "", "Git n’a pas pu démarrer.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                return new(false, stdout.GetAwaiter().GetResult(), "La commande Git a expiré.");
            }
            return new(process.ExitCode == 0, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            return new(false, "", ex.Message);
        }
    }

    private sealed record GitResult(bool Success, string Output, string Error);
}

public sealed record GitRepositoryStatus(
    string WorkspacePath,
    bool WorkspaceConfigured,
    bool WorkspaceExists,
    bool GitAvailable,
    bool GitMetadataPresent,
    bool IsRepository,
    string? RepositoryRoot,
    string? CurrentBranch);

public sealed record GitRepositoryInitializationResult(
    string RepositoryRoot,
    string IntegrationBranch,
    string Message);

public sealed class GitRepositoryAlreadyExistsException(string message) : InvalidOperationException(message);
