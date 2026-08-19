namespace KittyClaw.Core.Models;

/// <summary>One defect or unresolved collision reported while analyzing or installing a kit.</summary>
public sealed record PipelineImportIssue(string Category, string Path, string Message);

/// <summary>
/// Structured, write-free preview of an untrusted ".kittyclaw-pipeline" kit against a target
/// project: inventory with verified hashes, creation diff, collisions, missing parameters,
/// secrets and prerequisites, and the owner approvals required before installation.
/// </summary>
public sealed class PipelineImportPreview
{
    /// <summary>False while <see cref="Blockages"/> is non-empty; conflicts do not block analysis.</summary>
    public bool Installable { get; set; }
    public int FormatVersion { get; set; }
    public string PipelineName { get; set; } = "";
    public string PipelineSlug { get; set; } = "";
    /// <summary>An existing pipeline already uses this name: rename at install time or cancel.</summary>
    public bool PipelineNameConflict { get; set; }
    public List<PipelineImportIssue> Blockages { get; set; } = [];
    public List<PipelineImportFile> Files { get; set; } = [];
    public List<PipelineImportColumn> Columns { get; set; } = [];
    public List<PipelineImportSkill> Skills { get; set; } = [];
    public List<PipelineImportPlaceholder> Parameters { get; set; } = [];
    public List<PipelineImportSecret> Secrets { get; set; } = [];
    public List<PipelineImportRequirement> Models { get; set; } = [];
    public List<PipelineImportRequirement> Agents { get; set; } = [];
    /// <summary>Approval keys the owner must supply at install time (never implied).</summary>
    public List<string> RequiredApprovals { get; set; } = [];
    /// <summary>Embedded script files covered by the "embeddedScripts" approval.</summary>
    public List<string> EmbeddedScripts { get; set; } = [];
    public PipelineImportCreationPlan Creation { get; set; } = new();
}

public sealed record PipelineImportFile(string Path, long Bytes, string Sha256, bool Verified);

public sealed record PipelineImportColumn(
    string Key, string Name, ColumnRole Role,
    string? ProcessorName, string? Model, bool ProcessorEnabled, List<string> ActionTypes);

public enum PipelineImportSkillResolution
{
    /// <summary>No project skill uses this slug: the folder will be created.</summary>
    Create,
    /// <summary>A project skill with the same slug and fingerprint exists: it is reused, never rewritten.</summary>
    ReuseIdentical,
    /// <summary>Same slug, different content: install requires an explicit rename (never an overwrite).</summary>
    RenameRequired,
}

public sealed record PipelineImportSkill(
    string Slug, string Name, bool Embedded, string? Fingerprint, int FileCount,
    PipelineImportSkillResolution? Resolution, bool Available);

public sealed record PipelineImportPlaceholder(string Name, int Occurrences);

public sealed record PipelineImportSecret(string Name, int Occurrences, bool VaultSecretExists);

public sealed record PipelineImportRequirement(string Name, bool Available);

/// <summary>Creation diff: everything the installation would add. Nothing is ever overwritten.</summary>
public sealed class PipelineImportCreationPlan
{
    public int ColumnsToCreate { get; set; }
    public int ProcessorsToCreate { get; set; }
    public List<string> SkillsToCreate { get; set; } = [];
    public List<string> SkillsToReuse { get; set; } = [];
}

/// <summary>Owner-supplied decisions accompanying an installation request.</summary>
public sealed class PipelineImportConfirmation
{
    /// <summary>Overrides the kit's pipeline name; required when the original name collides.</summary>
    public string? PipelineName { get; set; }
    /// <summary>Kit skill slug → new slug, required for same-slug/different-content collisions.</summary>
    public Dictionary<string, string> SkillRenames { get; set; } = new(StringComparer.Ordinal);
    /// <summary>{{input.NAME}} values substituted into the installed pipeline and skills.</summary>
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Kit secret name → project vault secret name. Unbound names default to themselves.</summary>
    public Dictionary<string, string> SecretBindings { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Explicit owner approvals, e.g. "executePowerShell", "httpRequest", "embeddedScripts".</summary>
    public List<string> Approvals { get; set; } = [];
}

public sealed class PipelineImportResult
{
    public int PipelineId { get; set; }
    public string PipelineName { get; set; } = "";
    public string PipelineSlug { get; set; } = "";
    /// <summary>False when a prerequisite, parameter, secret or approval is missing: the
    /// pipeline is installed with every processor disabled and the reasons listed.</summary>
    public bool Enabled { get; set; }
    public List<string> DisabledReasons { get; set; } = [];
    public List<PipelineImportInstalledColumn> Columns { get; set; } = [];
    public List<string> SkillsCreated { get; set; } = [];
    public List<string> SkillsReused { get; set; } = [];
}

public sealed record PipelineImportInstalledColumn(string Key, int ColumnId, string Name);

/// <summary>Raised before any write when the archive is invalid, hostile or tampered with.</summary>
public sealed class PipelineImportRejectedException(IReadOnlyList<PipelineImportIssue> issues)
    : Exception("Le kit de pipeline est rejeté : l’archive est invalide, hostile ou altérée.")
{
    public IReadOnlyList<PipelineImportIssue> Issues { get; } = issues;
}

/// <summary>Raised before any write when name or slug collisions remain unresolved.</summary>
public sealed class PipelineImportConflictException(IReadOnlyList<PipelineImportIssue> issues)
    : Exception("L’installation du kit est refusée : des collisions doivent être résolues par renommage ou annulation, jamais par écrasement.")
{
    public IReadOnlyList<PipelineImportIssue> Issues { get; } = issues;
}
