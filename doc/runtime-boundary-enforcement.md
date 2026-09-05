# Runtime boundary enforcement

## Purpose

Runtime boundary enforcement stops protected provider actions before their external or destructive effect unless a matching temporary approval can be durably receipted. Each project persists an `Observe` or `Enforce` mode, and `AgentRunner` applies it centrally to every run context. Protection is advertised only for provider and boundary pairs with a reliable native pre-effect hook; unsupported providers fail before spawn in strict mode.

## Key components

- `KittyClaw.Core/Automation/RuntimeEnforcementCapabilities.cs` — authoritative provider-by-boundary capability catalogue.
- `KittyClaw.Core/Automation/RuntimeEnforcementHooks.cs` — generates Claude Code `PreToolUse` and `PostToolUse` hooks and denies when the gate or required environment is unavailable.
- `KittyClaw.Core/Services/RuntimeBoundaryGateService.cs` — classifies hook requests, resolves decisions, consumes one-time approvals, and persists correlated receipts before allowing an effect.
- `KittyClaw.Core/Services/RuntimeBoundaryEnforcementService.cs` — provider-neutral enforcement and receipt contract.

## Entry points

- `GET /api/runtime-enforcement/capabilities` returns the catalogue used by dispatch policy and protection claims.
- `POST /api/projects/{slug}/approvals/gate?runId={runId}` receives provider hook events before and after effects.
- Every run reloads the project's enforcement mode before dispatch. `Enforce` installs the native hook adapter when every protected boundary is enforceable, or fails before provider spawn when the runtime is observation-only.

Local-checkout synchronization is an orchestrator-owned Git operation, not a provider action. Its mutation window is registered so boundary drift reporting identifies it as coordinated KittyClaw activity instead of attributing it to the running agent. Synchronization failures remain visible independently from the already durable integration; see [Local checkout synchronization recovery](./local-checkout-sync-recovery.md).

## Capability matrix

The five protected boundary classes are repository push or pull-request mutation, publication or deployment, a new outbound destination, secret access, and destructive local operation.

| Runtime path | All five boundary classes | Mechanism or exclusion |
| --- | --- | --- |
| Claude Code | Enforced | Native `PreToolUse` hook calls the KittyClaw gate before the effect; `PostToolUse` records the outcome. |
| Ollama through Claude Code | Enforced | Uses the Claude Code transport and the same native hook adapter. |
| OpenAI Codex CLI | Observation only | JSONL events can arrive after the effect starts; an `Enforce` dispatch fails before spawn. |
| Grok Build CLI | Observation only | JSONL events can arrive after the effect starts; an `Enforce` dispatch fails before spawn. |
| Mistral Vibe CLI | Observation only | JSONL events can arrive after the effect starts; an `Enforce` dispatch fails before spawn. |
| DeepSeek CLI | Observation only | No reliable native pre-effect hook is available; an `Enforce` dispatch fails before spawn. |

The Claude adapter can classify only effects whose command, URL, path, or destination is present in structured hook input. Effects hidden inside an opaque provider tool are excluded from the protection claim. Direct JSONL streams, aliases or encoded commands that do not expose the protected resource, and subprocess effects invisible to a native hook remain observation-only exclusions.

## External dependencies

- [Temporary approvals](./temporary-approvals.md) provide scoped decisions, expiry, atomic one-time consumption, and audit receipts.
- [Agent provider CLIs](./agent-providers.md) define the supported provider transports.
- [Agent dispatch](./agent-dispatch.md) owns provider selection and fail-before-spawn behavior.
- [Storage](./storage.md) persists approval decisions and receipts.
