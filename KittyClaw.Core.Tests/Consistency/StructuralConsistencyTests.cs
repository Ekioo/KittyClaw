using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Tests.Consistency;

/// <summary>
/// CI invariants: every visible agent, action, provider, model, and localization
/// entry is fully wired. Each test names the missing or inconsistent counterpart
/// and the source location so failures are immediately actionable.
///
/// These checks run deterministically without network access or external providers.
/// They are pure test/CI infrastructure — no user-facing concept is introduced.
/// </summary>
public class StructuralConsistencyTests
{
    // ── Repo navigation helpers ────────────────────────────────────────────────

    private static string RepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "KittyClaw.slnx"))
                               && !File.Exists(Path.Combine(dir, "KittyClaw.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static string SourceText(string repoRelativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), repoRelativePath));

    private static IReadOnlyList<JsonDerivedTypeAttribute> JsonDerivedTypes<TBase>() where TBase : class =>
        typeof(TBase)
            .GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
            .Cast<JsonDerivedTypeAttribute>()
            .ToList();

    // ── 1. Action ↔ UI editor ─────────────────────────────────────────────────

    /// <summary>
    /// Every ActionSpec discriminator (the runtime type identity) must have a matching
    /// &lt;option value="..."&gt; in ActionEditor.razor so the user can select and configure it.
    /// Adding a JsonDerivedType without wiring the option makes the action silently
    /// unreachable from the UI.
    /// </summary>
    [Fact]
    public void EveryActionSpec_HasAnOptionInActionEditorRazor()
    {
        var razor = SourceText("KittyClaw.Web/Components/ActionEditor.razor");
        var missing = JsonDerivedTypes<ActionSpec>()
            .Where(a => !razor.Contains($"value=\"{a.TypeDiscriminator}\"", StringComparison.Ordinal))
            .Select(a => $"  '{a.TypeDiscriminator}' ({a.DerivedType.Name}) — add <option value=\"{a.TypeDiscriminator}\"> to ActionEditor.razor")
            .ToList();

        if (missing.Count > 0)
            Assert.Fail("ActionSpec types not offered in ActionEditor.razor:\n" + string.Join("\n", missing));
    }

    // ── 2. Localization ↔ UI component ───────────────────────────────────────

    /// <summary>
    /// Every L["Key"] reference in ActionEditor.razor must be backed by a key in every
    /// supported language. The LocalizationService merges the bundles for each language,
    /// but a key absent from one language falls back to another language or the raw key.
    /// </summary>
    [Fact]
    public void ActionEditor_AllLocalizationKeyRefs_ExistInEverySupportedLanguage()
    {
        var locDir = Path.Combine(RepoRoot(), "KittyClaw.Core", "Localization");
        var razor = SourceText("KittyClaw.Web/Components/ActionEditor.razor");
        var refs = Regex.Matches(razor, @"L\[""([^""]+)""\]")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        var languageFiles = Directory.GetFiles(locDir, "*.json")
            .GroupBy(f => Path.GetFileNameWithoutExtension(f).Split('.').Last(), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(languageFiles);

        var missing = languageFiles.SelectMany(language =>
        {
            var keys = language
                .SelectMany(f => JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(f))!.Keys)
                .ToHashSet(StringComparer.Ordinal);
            return refs.Where(key => !keys.Contains(key))
                .Select(key => $"  language '{language.Key}' is missing L[\"{key}\"] — add it to a KittyClaw.Core/Localization/*.{language.Key}.json bundle");
        }).ToList();

        if (missing.Count > 0)
            Assert.Fail(
                "ActionEditor.razor localization keys are incomplete across supported languages:\n" +
                string.Join("\n", missing));
    }

    [Fact]
    public void EveryActionSpec_IsListedInGeneratedApiDocumentation()
    {
        var generator = SourceText("KittyClaw.Web/Api/OpenApiMarkdownGenerator.cs");
        var missing = JsonDerivedTypes<ActionSpec>()
            .Where(action => !generator.Contains($"| `{action.TypeDiscriminator}` |", StringComparison.Ordinal))
            .Select(action => $"  '{action.TypeDiscriminator}' ({action.DerivedType.Name}) — document it in the 'Available actions' table in KittyClaw.Web/Api/OpenApiMarkdownGenerator.cs")
            .ToList();

        if (missing.Count > 0)
            Assert.Fail("ActionSpec types missing from generated /api/docs:\n" + string.Join("\n", missing));
    }

    // ── 3. Provider ↔ CLI backend ─────────────────────────────────────────────

    /// <summary>
    /// AgentCliBackend.For() must have an explicit arm for every CliProvider value
    /// that is not the default (Claude). A new CliProvider without an arm silently
    /// falls through to ClaudeBackend — wrong provider, wrong binary, wrong arguments.
    /// </summary>
    [Fact]
    public void EveryCliProvider_ExceptDefault_HasExplicitArmInAgentCliBackendFor()
    {
        var src = SourceText("KittyClaw.Core/Automation/AgentCliBackend.cs");

        // Locate the For() method body — it is a switch expression.
        const string methodSig = "static AgentCliBackend For(CliProvider provider)";
        var methodStart = src.IndexOf(methodSig, StringComparison.Ordinal);
        Assert.True(methodStart >= 0,
            "AgentCliBackend.For(CliProvider provider) must be present in AgentCliBackend.cs.");

        // Collect the expression up to the semicolon that closes the switch.
        var bodyStart = src.IndexOf("=>", methodStart, StringComparison.Ordinal);
        var bodyEnd   = src.IndexOf(';', bodyStart);
        var switchBody = src[bodyStart..bodyEnd];

        // Claude is the implicit default arm (_) — every other value needs an explicit case.
        var missing = Enum.GetValues<CliProvider>()
            .Where(p => p != CliProvider.Claude)
            .Where(p => !switchBody.Contains($"CliProvider.{p}", StringComparison.Ordinal))
            .Select(p => $"  CliProvider.{p} — add an explicit arm in AgentCliBackend.For()")
            .ToList();

        if (missing.Count > 0)
            Assert.Fail(
                "CliProvider values not handled by AgentCliBackend.For() " +
                "(they fall silently to the Claude default):\n" +
                string.Join("\n", missing));
    }

    // ── 4. Provider ↔ model routing ───────────────────────────────────────────

    /// <summary>
    /// Each non-Claude provider must be reachable through at least one deterministic
    /// model-prefix rule in ModelRouting.Resolve. The catalog model is the probe: if
    /// the prefix recognizer doesn't recognise it, ModelRouting routes it to the wrong
    /// provider (or to the local-Ollama path, which is a silent misconfiguration).
    /// When the CLI is not installed the resolution returns a provider-named error —
    /// the test verifies the error names the right provider so the user can act on it.
    /// </summary>
    [Theory]
    [InlineData(CliProvider.Grok,    "grok-4.5")]
    [InlineData(CliProvider.Codex,   "codex:gpt-5.6-sol")]
    [InlineData(CliProvider.Mistral, "mistral:mistral-medium-3.5")]
    public void NonDefaultCliProvider_IsReachableByModelPrefix(CliProvider expected, string probeModel)
    {
        var resolution = ModelRouting.Resolve(probeModel, localModelBaseUrl: null);

        if (resolution.Error is null)
        {
            // CLI is installed — the provider must be exactly right.
            Assert.True(resolution.Provider == expected,
                $"ModelRouting.Resolve(\"{probeModel}\") routed to {resolution.Provider} but expected {expected}.");
        }
        else
        {
            // CLI not installed — the error must name the provider so the user can install it.
            Assert.True(
                (resolution.Error ?? "").Contains(expected.ToString(), StringComparison.OrdinalIgnoreCase),
                $"ModelRouting.Resolve(\"{probeModel}\") produced an error but it does not name " +
                $"provider {expected}. Error: {resolution.Error}");
        }
    }

    // ── 5. Claude model catalog ───────────────────────────────────────────────

    /// <summary>
    /// ClaudeModelCatalog.DefaultModel must be a member of ClaudeModelCatalog.Models.
    /// A default model not in the selectable list would be silently invisible in the UI
    /// and could produce a confusing "model unknown" error at dispatch time.
    /// </summary>
    [Fact]
    public void ClaudeModelCatalog_DefaultModel_IsMemberOfModels()
    {
        Assert.True(
            ClaudeModelCatalog.Models.Contains(ClaudeModelCatalog.DefaultModel, StringComparer.Ordinal),
            $"ClaudeModelCatalog.DefaultModel (\"{ClaudeModelCatalog.DefaultModel}\") " +
            "is not in ClaudeModelCatalog.Models — the default model must be selectable in the UI.");
    }

    /// <summary>
    /// Every model in ClaudeModelCatalog.Models must route cleanly to CliProvider.Claude
    /// with no error. A model accidentally matching the grok- or codex: prefix would be
    /// dispatched to the wrong CLI and fail at runtime.
    /// </summary>
    [Fact]
    public void ClaudeModelCatalog_AllModels_RouteToClaudeProviderWithNoError()
    {
        var mismatches = ClaudeModelCatalog.Models
            .Select(m => (Model: m, Resolution: ModelRouting.Resolve(m, localModelBaseUrl: null)))
            .Where(r => r.Resolution.Provider != CliProvider.Claude || r.Resolution.Error is not null)
            .Select(r => $"  \"{r.Model}\" → Provider={r.Resolution.Provider}, Error={r.Resolution.Error}")
            .ToList();

        if (mismatches.Count > 0)
            Assert.Fail(
                "ClaudeModelCatalog.Models entries do not route to Claude:\n" +
                string.Join("\n", mismatches));
    }

    // ── 6. Agent template ↔ embedded assets ──────────────────────────────────

    /// <summary>
    /// Every agent slug discovered by AgentsTemplateService must have a corresponding
    /// SKILL.md embedded in KittyClaw.Core.dll. An agent without a SKILL.md is
    /// silently non-functional: the dispatch reads the file at startup and skips the
    /// agent when it is absent.
    /// </summary>
    [Fact]
    public void EveryEmbeddedAgent_HasSkillMdInCore()
    {
        var svc = new AgentsTemplateService();
        var paths = svc.RelativePaths().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var agentsRoot = Path.Combine(RepoRoot(), "ProjectTemplate", "Agents");
        var slugs = Directory.GetDirectories(agentsRoot)
            .Select(Path.GetFileName)
            .Where(slug => !string.IsNullOrWhiteSpace(slug))
            .Select(slug => slug!)
            .OrderBy(slug => slug, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(slugs); // sanity: the assembly must embed at least one agent

        var missing = slugs
            .Where(slug => !paths.Contains($"{slug}/SKILL.md"))
            .Select(slug => $"  agent directory 'ProjectTemplate/Agents/{slug}' has no embedded '{slug}/SKILL.md' — add the file and include it in KittyClaw.Core.csproj")
            .ToList();

        if (missing.Count > 0)
            Assert.Fail("Embedded agents with no SKILL.md:\n" + string.Join("\n", missing));
    }

    /// <summary>
    /// Every embedded agent must have a memory/MEMORY.md stub so the memory-consolidation
    /// pass has a valid index to write into. Without it the consolidation action fails
    /// silently on first run and memory is never accumulated.
    /// </summary>
    [Fact]
    public void EveryEmbeddedAgent_HasMemoryIndexInCore()
    {
        var svc = new AgentsTemplateService();
        var paths = svc.RelativePaths();
        var slugs = svc.AgentSlugs();

        var missing = slugs
            .Where(slug => !paths.Any(p => p.StartsWith($"{slug}/memory/", StringComparison.OrdinalIgnoreCase)))
            .Select(slug => $"  agent '{slug}' — add ProjectTemplate/Agents/{slug}/memory/MEMORY.md")
            .ToList();

        if (missing.Count > 0)
            Assert.Fail("Embedded agents with no memory/ directory:\n" + string.Join("\n", missing));
    }

    // ── 7. Automation template ↔ agent existence ──────────────────────────────

    /// <summary>
    /// Every literal agent slug (non-placeholder) in ProjectTemplate/Agents/automations.json
    /// must match an agent directory that has a SKILL.md. A typo or orphan reference
    /// produces a dispatch error at runtime: the agent file is missing so the run is
    /// aborted immediately after being queued.
    /// </summary>
    [Fact]
    public void TemplateAutomations_RunAgentActions_ReferenceKnownAgents()
    {
        var svc = new AgentsTemplateService();
        var knownSlugs = new HashSet<string>(svc.AgentSlugs(), StringComparer.OrdinalIgnoreCase);

        var automationsPath = Path.Combine(RepoRoot(), "ProjectTemplate", "Agents", "automations.json");
        Assert.True(File.Exists(automationsPath),
            $"ProjectTemplate/Agents/automations.json must exist (looked in: {automationsPath}).");

        var config = JsonSerializer.Deserialize<AutomationConfig>(
            File.ReadAllText(automationsPath), AutomationStore.JsonOptions)!;

        var violations = new List<string>();
        foreach (var auto in config.Automations)
        {
            foreach (var action in auto.Actions)
            {
                var agentSlug = action switch
                {
                    RunAgentActionSpec a           => a.Agent,
                    CommitAgentMemoryActionSpec a   => a.Agent,
                    ConsolidateAgentMemoryActionSpec a => a.Agent,
                    _                              => null,
                };

                if (agentSlug is null) continue;
                // Skip runtime placeholders such as {assignee}.
                if (agentSlug.StartsWith('{') && agentSlug.EndsWith('}')) continue;

                if (!knownSlugs.Contains(agentSlug))
                    violations.Add(
                        $"  automation '{auto.Id}' action '{action.UiTypeKey}' references unknown agent '{agentSlug}' " +
                        $"— add ProjectTemplate/Agents/{agentSlug}/SKILL.md or correct the slug");
            }
        }

        if (violations.Count > 0)
            Assert.Fail("Template automations reference agents that do not exist:\n" + string.Join("\n", violations));
    }
}
