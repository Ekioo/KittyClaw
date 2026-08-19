using KittyClaw.Core.Automation;

namespace KittyClaw.Core.Models;

/// <summary>
/// Constants of the portable ".kittyclaw-pipeline" kit: a ZIP container holding a versioned
/// manifest, a portable pipeline definition keyed by logical column keys, and the full folder
/// of every referenced project skill. Database identifiers never appear in a kit.
/// </summary>
public static class PipelineKitFormat
{
    public const string Format = "kittyclaw-pipeline";
    public const int Version = 1;
    public const string FileExtension = ".kittyclaw-pipeline";
    public const string ManifestEntryName = "manifest.json";
    public const string PipelineEntryName = "pipeline.json";
    public const string SkillsFolderName = "skills";
}

public sealed class PipelineKitManifest
{
    public string Format { get; set; } = PipelineKitFormat.Format;
    public int FormatVersion { get; set; } = PipelineKitFormat.Version;
    public PipelineKitMetadata Metadata { get; set; } = new();
    public PipelineKitProvenance Provenance { get; set; } = new();
    public PipelineKitCompatibility Compatibility { get; set; } = new();
    /// <summary>Project-specific values the importer must supply ({{input.NAME}} placeholders).</summary>
    public List<PipelineKitPlaceholder> Parameters { get; set; } = [];
    /// <summary>Vault references the importer must bind ({{secret.NAME}} placeholders). Values are never exported.</summary>
    public List<PipelineKitPlaceholder> Secrets { get; set; } = [];
    public PipelineKitRequirements Requirements { get; set; } = new();
    /// <summary>SHA-256 (lowercase hex) of every archive file except the manifest itself.</summary>
    public List<PipelineKitFileEntry> Files { get; set; } = [];
}

public sealed class PipelineKitMetadata
{
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
}

public sealed class PipelineKitProvenance
{
    public string Generator { get; set; } = "KittyClaw";
    public string GeneratorVersion { get; set; } = "";
    public string ProjectSlug { get; set; } = "";
    public string ExportedAt { get; set; } = "";
}

public sealed class PipelineKitCompatibility
{
    /// <summary>Oldest reader format version able to install this kit. Unknown versions must be refused.</summary>
    public int MinFormatVersion { get; set; } = PipelineKitFormat.Version;
}

public sealed class PipelineKitPlaceholder
{
    public string Name { get; set; } = "";
    public int Occurrences { get; set; }
}

public sealed class PipelineKitRequirements
{
    /// <summary>Model identifiers referenced by exported processors.</summary>
    public List<string> Models { get; set; } = [];
    /// <summary>Agent identities referenced by actions (their workspace skills are declared, never copied).</summary>
    public List<string> Agents { get; set; } = [];
    public List<PipelineKitSkillRequirement> Skills { get; set; } = [];
    /// <summary>Action types requiring explicit owner approval at import time.</summary>
    public List<string> RiskyActionTypes { get; set; } = [];
}

public sealed class PipelineKitSkillRequirement
{
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>True when the full skill folder ships inside the archive; false for a declared-only prerequisite.</summary>
    public bool Embedded { get; set; }
}

public sealed class PipelineKitFileEntry
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

/// <summary>Portable pipeline definition. Columns are identified by stable logical keys, never database ids.</summary>
public sealed class PortablePipeline
{
    public int FormatVersion { get; set; } = PipelineKitFormat.Version;
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    /// <summary>Column order is the array order.</summary>
    public List<PortableColumn> Columns { get; set; } = [];
}

public sealed class PortableColumn
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Color { get; set; } = "";
    public ColumnRole Role { get; set; } = ColumnRole.Normal;
    public string UserGuidance { get; set; } = "";
    public PortableProcessor? Processor { get; set; }
}

public sealed class PortableProcessor
{
    public string Name { get; set; } = "";
    public string Mission { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string? Model { get; set; }
    public bool Enabled { get; set; } = true;
    public int MaxTurns { get; set; } = 100;
    public TicketSelectionOrder SelectionOrder { get; set; } = TicketSelectionOrder.Position;
    public int MaxAttempts { get; set; } = 3;
    public int RetryBackoffSeconds { get; set; } = 60;
    public List<string> AvailableSkills { get; set; } = [];
    public List<string> RecommendedSkills { get; set; } = [];
    public List<string> RequiredSkills { get; set; } = [];
    public List<PortableProcessorAction> BeforeActions { get; set; } = [];
    public List<PortableProcessorAction> AfterActions { get; set; } = [];
    public PortableRouting Routing { get; set; } = new();
}

public sealed class PortableProcessorAction
{
    public string Id { get; set; } = "";
    public ActionSpec Action { get; set; } = new SetLabelsActionSpec();
    /// <summary>Logical key of the failure destination column, when declared.</summary>
    public string? OnFailure { get; set; }
}

public sealed class PortableRouting
{
    public string? Default { get; set; }
    public string? TechnicalFailure { get; set; }
    public List<PortableRoute> Routes { get; set; } = [];
}

public sealed class PortableRoute
{
    public string Outcome { get; set; } = "";
    public string Target { get; set; } = "";
}

/// <summary>
/// One blocking occurrence reported by the export scanner. Excerpts of sensitive categories are
/// masked so the report itself never reveals a value.
/// </summary>
public sealed record PipelineExportFinding(
    string Category, string Path, int Line, string Excerpt, string Guidance);

public sealed record PipelineExportResult(string FileName, byte[] Content);

/// <summary>Raised when sanitization findings remain. There is no override in format v1.</summary>
public sealed class PipelineExportBlockedException(IReadOnlyList<PipelineExportFinding> findings)
    : Exception("L’export du pipeline est bloqué : des contenus sensibles ou non portables doivent d’abord être convertis.")
{
    public IReadOnlyList<PipelineExportFinding> Findings { get; } = findings;
}
