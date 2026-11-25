# API, uploaded-file and secret-handling security audit

Scope: HTTP endpoint implementation, uploaded images, chat image persistence,
pipeline archive intake, raw dashboard files and the project-secret vault.
Tests use synthetic data. The main instance on port 5230 was not used for
verification, and no real provider was dispatched.

## Confirmed findings and corrections

| Finding | Impact | Correction |
| --- | --- | --- |
| Raw dashboard SVG lacked a restrictive document policy | An agent-generated SVG opened directly could run script in the application origin | Raw outputs receive `default-src 'none'; style-src 'unsafe-inline'; sandbox`, retaining SVG image rendering |
| Chat image persistence trusted MIME and size metadata and decoded before bounding encoded length | Non-image bytes could be forwarded as images, with unnecessary allocations on oversized input | Match PNG/JPEG/GIF/WebP signatures to the allowed MIME, bound encoded length before decoding, and keep the decoded-size cap |
| Failed chat-image batches left earlier files behind | Repeated rejected batches could retain private attachments and consume disk space | Remove batch files on rejection or write failure; create directories only after validation |
| Pipeline upload adapters buffered the entire body before the importer's 8 MiB check | Missing or misleading Content-Length could cause excess allocation before validation | Read at most 8 MiB plus one sentinel byte, including streams without declared length; reject oversized multipart files before copying |
| Secret identifier regexes used `$`, which accepts a final newline | Invalid environment-variable names and vault slugs were accepted | Use absolute `\A` / `\z` anchors; reject NUL values before they can be truncated in process environments |
| Secret redaction recognized only the default .NET JSON encoder | Relaxed JSON quoting and escaped slashes could leave secret representations in provider logs | Redact literal, default JSON, relaxed JSON and escaped-slash forms, longest first |

The standalone image-upload endpoint already generated server-side filenames,
enforced a 10 MiB file cap and identified raster formats from bytes. Its GIF
signature now requires GIF87a or GIF89a. Static uploads already use `nosniff`
and a sandboxed CSP. The API and UI secret surface remains write-only; native
encryption failure still fails closed without plaintext fallback.

## Verification

35 targeted tests passed across `ApiFileSecurityTests`, `ProjectSecretVaultTests`,
`ProjectSecretsHttpTests`, `ProjectSecretInjectionTests` and
`PipelineExportHttpTests`. The tests include direct HTTP SVG and image-upload
responses, partial-file cleanup, forged size/MIME metadata, stream bounds,
invalid identifiers, native-protection failure, project separation and encoded
secret redaction.

The preceding browser-header and trusted documentation-client hardening was
committed separately as `05b98e8`; its targeted verification passed 31 tests.

## Boundaries

Loopback automation is trusted by design; authenticated remote clients share the
owner's API capabilities. This change does not introduce per-project or per-agent
authorization. Signature checks identify supported image formats; they do not
fully decode images or scan them for malware. Redaction covers the listed literal
and JSON forms, not arbitrary transformations such as base64 or deliberately
split values. This was a scoped source audit and regression run, not a complete
penetration test or dependency-vulnerability assessment.

## Follow-up: native secret processes and SSE subscribers

Three additional findings were confirmed and corrected:

- Native secret-store commands performed synchronous pipe reads before their
  timeout check. A blocked read or stdin write could therefore bypass the
  nominal 30-second timeout, and sequential stdout/stderr reads could deadlock.
  The deadline now covers concurrent pipe drains, stdin and process exit.
  Output is capped at 64 Ki characters per stream. Failed calls terminate the
  running process tree on a best-effort basis, with a Windows job boundary.
- Keychain and Secret Service failures appended raw native stderr to exceptions
  returned by the API. A command diagnostic could echo master-key input.
  Errors now contain only fixed guidance, the store identity and the exit code;
  tests inject key material into both native output streams to verify exclusion.
- REST SSE connections used an unbounded event channel. A slow subscriber could
  retain an increasing number of events. Each subscriber now has a bounded
  256-event queue and is disconnected on overflow, without blocking the shared
  producer or silently discarding events on an open connection.

Final verification: 32 tests passed across `SecretToolProcessSecurityTests`,
`ProjectSecretProtectionTests`, `ProjectSecretsHttpTests` and
`RunEventStreamBufferTests`. Actual subprocess tests exercised concurrent pipe
drains, blocked stdin, hung commands, excessive output and successful stdin
delivery. Native platform key stores were simulated; Windows ran the subprocess
tests through PowerShell. SSE queue tests covered saturation, ordering and
subscriber isolation; a real slow-network browser test was not performed.

## 2026-10-25 nightly audit: executable Markdown URLs

Confirmed a stored-XSS path in the shared Markdown pipeline. Raw HTML and generic
attributes were already disabled, but `[audit link](javascript:...)` still emitted
an executable `href`. An attacker-controlled ticket, comment, chat response or
dashboard Markdown could therefore execute JavaScript in the application origin
when the owner clicked the link. This requires a click; no automatic execution
was claimed.

Before changing production code, three Chromium tests rendered the actual chat,
comment and dashboard HTML and clicked the injected link. All three failed
because the JavaScript marker executed. The shared pipeline now rewrites unsafe
URI schemes for links and images to `#`, including reference links and autolinks.
Relative destinations and `http`, `https`, `mailto` and `tel` links remain
supported. The check handles entity-encoded scheme characters and rejects
control characters; `data`, `file`, `vbscript`, `javascript` and custom handlers
are not permitted in rendered Markdown. No dependency version was changed.

Final verification: 86 tests passed in two targeted runs (64 and 22), including
54 new URL-policy/browser cases and existing generic-attribute, ticket-reference,
Markdown-formatting and dashboard-renderer regressions. All three browser
exploit cases that failed before the fix now pass. Tests rendered synthetic
content in an isolated Chromium page; they did not use the live UI on :5230,
real project data, real secrets or provider dispatches. The full test suite and
a live Blazor navigation test were not run. Other sessions' local changes were
preserved and excluded from the security commit.


## 2026-10-26 nightly audit: Windows junction command injection

Confirmed command injection in the Windows fallback that exposes local
`node_modules` dependencies in maintenance worktrees. When symbolic-link
creation failed, `CreateDirectoryLink` invoked `cmd.exe /c mklink /J` with
filesystem paths in `ArgumentList`. That API quotes ordinary process arguments,
but does not escape the command interpreter's metacharacters. A crafted source
path containing ampersands could execute another command as the app account.
The issue requires Windows, the junction fallback and a crafted dependency path;
it is not an anonymous remote HTTP exploit.

A disposable fixture replayed the exact old invocation with a synthetic probe
batch file on the child PATH. The probe executed, confirming the injection;
no real project, secret or provider was used. The replacement executes fixed
PowerShell code, passes paths exclusively through child environment variables,
and escapes the source for PowerShell wildcard resolution. It uses the existing
bounded process runner and refuses to overwrite an existing destination.

Final verification: 37 targeted tests passed, including six native Windows
junction cases plus `DurableWriteRouterTests`, `ProcessRunnerTests` and
`ProcessRunnerLifecycleTests`. Coverage includes ampersands, percent variables,
backticks, brackets, whitespace, quotes and existing destinations. The first run
exposed a wildcard-resolution failure for bracketed source paths; that was
corrected and the assertions were retained. The complete selected set passed
on the final run. The full suite and live UI were not run.

A NuGet vulnerability query of the resolved Web project graph, including
transitive packages and using only nuget.org, completed without listing known
vulnerable packages. No versions were changed; provider binaries and CDN assets
were outside this dependency query. The scheduled audit's documentation entry
was updated from `doc/index.md` to `docs/index.md` after the repository migration.

Next scoped review: remaining synchronous Git helpers in `ProjectService`,
`DurableWriteRouter` and `WorktreeMergeQueueService`, particularly sequential
pipe draining and whether timeouts cover binary output transfer. These were
identified for focused availability reproductions and were not refactored in
this junction-specific correction.


## 2026-10-27 nightly audit: Git pipe deadlocks and transfer deadlines

Completed the previous follow-up on synchronous Git helpers. Project repository
validation, maintenance writes and worktree integration drained stdout before
stderr and checked exit timeouts only after those reads. A child filling stderr
could block while the parent waited for stdout EOF, preventing the timeout from
ever being reached and holding workflow locks indefinitely. The conflict-file
export path likewise copied raw stdout synchronously before checking its timer.
This is an availability defect in local Git execution, not an anonymous remote
command-execution vulnerability.

A synthetic native child first signaled readiness, then filled stderr while
leaving stdout open. Replaying the former drain order demonstrated that stdout
remained blocked before the exit-timeout check. Only that disposable child was
started and stopped; no live project, secret or provider was involved.

The three Git consumers now use `ProcessRunner` through a `ProcessStartInfo`
overload, preserving literal `ArgumentList` entries. Both streams drain
concurrently. The existing 10-second validation and 30-second integration timers
remain active through pipe completion and binary transfers; cancellation kills
the process tree on a best-effort basis and uses a bounded final cleanup drain.
Raw output is copied as bytes, and failed or incomplete transfers cannot be
reported as success. The string-based runner delegates to the same path, so its
final pipe drain also no longer silently reports success after a transfer error.

Final verification: 130 tests passed in two final runs: 13 process-security and
lifecycle tests, plus 117 project-validation, durable-routing, integration-queue
and Windows-junction regressions. Native tests cover stderr saturation, literal
arguments, all 256 byte values in a 64 KiB transfer, hung processes, a blocked
transfer after parent exit, write failures, exit codes and cancellation. An
initially timing-sensitive fixture was replaced with a minimal child while
retaining its start and timeout assertions, then the complete process set was
rerun successfully. The integration tests include append-only memory conflict
recovery through the raw-output export path.

The full suite, live UI and non-Windows execution were not tested. No dependency
versions were changed. Git hooks are not sandboxed by this change, and text
output remains captured in memory. Next scoped review: synchronous availability
probes, especially their final EOF waits, and suitable output-volume limits for
non-agent command capture. The :5230 instance was not used for verification;
all execution used synthetic processes or fixture-owned temporary repositories.


## 2026-10-28 nightly audit: synchronous CLI probe EOF waits

Confirmed the synchronous-probe follow-up from the previous batch. Codex and
Vibe executable discovery runs `ProcessRunner.ProbeSync` from cached Lazy
initializers. Its timed process-exit wait was followed by a parameterless
`WaitForExit`, which also waits for event-based stream readers to reach EOF.
When the parent exited successfully but a descendant inherited its pipes, the
probe blocked before disposing the Windows job that would terminate that
same descendant. This could hold a discovery initializer indefinitely despite
its nominal five-second timeout. The defect requires a local executable or
wrapper with this behavior; no anonymous remote exploit was established.

Before changing production code, a synthetic PowerShell parent spawned a
sleeping child with inherited streams and exited. The regression called the
actual probe with a three-second timeout; it still had not returned after
eight seconds and failed. Fixture cleanup killed only its own child. The
corrected version of the same test passes, including proof that the child
was spawned. Fixture cleanup checks both PID and process start time to avoid
targeting a recycled PID.

The synchronous wrapper now uses the existing bounded runner. Its concurrent
raw-byte transfers discard both streams into Stream.Null, avoiding line
accumulation even for newline-free output. The runner closes its Windows job
before awaiting final pipe completion, keeps the original timer active, and
uses the existing bounded five-second cleanup drain after timeout. Every
internal await avoids the caller's synchronization context; a regression uses
a context that rejects posted callbacks to check the synchronous wrapper.
The new optional raw stderr destination shares stdout's deadline and failure
handling; errors or incomplete transfers cannot produce success.

Final verification: 57 tests passed in two runs: 21 across
`CliProbeSecurityTests`, `GitProcessSecurityTests`, `ProcessRunnerTests` and
`ProcessRunnerLifecycleTests`, then 36 across `CliAvailabilityProbeTests`,
`AgentCliReadinessServiceTests` and `CliVersionProbeTests`. Eight cases were
added, covering inherited pipes, hung probes, exit failures, 8 MiB of
newline-free output on each stream, synchronization contexts, discarded
streams, blocked stderr transfer and stderr write failure. Existing literal
arguments, raw binary output, cancellation and descendant cleanup regressions
also passed. Builds used `bin/SecurityProbeAudit/` and a temporary NuGet
configuration containing only nuget.org. No provider work was dispatched and
no real project, secret or instance on :5230 was used for attack testing.

The full suite, live UI and non-Windows execution were not tested. No
dependency versions were changed. Text-capturing commands outside these
synchronous discovery probes still need output-volume assessment. Next scoped
review: reproduce cancellation/inherited-pipe behavior in RTK's separate probe
and final-drain behavior in `TicketWorktreeService`, then select appropriate
limits for commands that genuinely need captured text. These separate process
lifecycles were located during source review and were not changed in this
probe-specific correction.


## 2026-10-29 nightly audit: RTK probing and ticket worktree Git processes

Completed the two process-lifecycle reproductions identified in the preceding
batch. Four baseline regressions failed against the original execution paths:

- RTK waited for process exit before draining either output pipe. A synthetic
  command writing 48,000 stderr characters blocked on the pipe and was reported
  unavailable after two seconds despite returning a valid version when drained.
- RTK's caller-cancellation path disposed its Process wrapper without killing
  the command. A fixture waited for a native child to publish its identity,
  cancelled the probe, and confirmed that the child remained alive after the
  cancellation had propagated.
- After the RTK parent exited, its final reads used only the caller token rather
  than the two-second deadline. An inherited-pipe child kept the actual probe
  blocked beyond the eight-second regression watchdog.
- Ticket worktree Git calls drained concurrently but awaited the final reads
  without a timer or Windows job boundary. A post-checkout hook in a disposable
  repository spawned an inherited-pipe child; worktree preparation remained
  blocked at the ten-second regression watchdog after the Git parent exited.
  Code inspection established that those final waits had no deadline.

These are local availability and process-cleanup defects, not evidence of an
anonymous remote command-execution path. All four original regressions pass
after the fixes. Tests start and stop only fixture-owned synthetic processes;
cleanup verifies both PID and process start time.

RTK now uses the shared ProcessRunner with its existing two-second timeout
active through concurrent pipe reads and process exit. Cancellation propagates
after cleanup; Windows jobs also terminate descendants holding inherited
pipes. RTK discards stderr as bytes without storing diagnostics and captures
at most 65,536 stdout characters. Excess stdout is rejected with a fixed
unavailable reason. The runner's new optional limit bounds each captured text
stream independently and stops an overproducing process before awaiting its
exit; raw destinations retain their existing streaming behavior. Exact-limit
output succeeds, and one extra character fails without returning partial text.
The limit is opt-in and is applied to RTK in this batch.

TicketWorktreeService now uses the same runner with its existing literal
ArgumentList entries, LC_ALL setting, exit handling and 30-second deadline.
The deadline covers final pipe reads, and the Windows job closes before EOF
waits. Timeout/cancellation cleanup retains the shared five-second bounded
final drain. Git hooks still execute as the application account.

Final verification: 67 distinct tests passed in two runs: 36 process/security
cases, then 31 RTK integration, isolated HTTP, ticket-worktree and project-setting
regressions (the already-tested background-hook case was excluded from the
second run). Fifteen cases were added. Coverage includes all four baseline
failures, stdout/stderr limits, immediate producer termination on overflow,
large discarded stderr, cancellation before spawn, fixed failure diagnostics,
version fallback, native binary transfer, literal arguments and descendant
cleanup. Worktree dispatch regressions use the mock CLI and temporary project
data; the HTTP test uses WebApplicationFactory's in-memory client and its own
data directory. Builds used bin/SecurityRtkAudit/ and a temporary NuGet
configuration containing only nuget.org. The main :5230 instance and real
project secrets were not used for verification; no paid provider was called.

The full suite, live UI and non-Windows execution were not tested. No dependency
versions were changed. Seven pre-existing modified files belonging to other
work were preserved byte-for-byte and excluded from this security batch.
Other text-capturing Git, version and evidence commands still need individual
output-volume assessment. Next scope: measure their legitimate output needs
and bound or stream the largest captures, then return to HTTP file/path review.


## 2026-10-30 nightly audit: fingerprint and version-output memory

Continued the output-volume follow-up with two confirmed local resource risks:

- Primary-checkout fingerprinting read each untracked file into a single byte
  array before computing SHA-256. A disposable 64 MiB untracked file caused
  67,353,800 managed bytes of allocation during a warmed capture. Repeated or
  concurrent captures could therefore multiply large temporary allocations.
  The regression failed its 32 MiB allocation budget before the correction.
- CLI version metadata probes used unrestricted text capture and accepted very
  large parsed version strings. Two injected-executor baseline cases supplied
  a synthetic numeric version over 128 KiB on stdout or stderr. Both were
  incorrectly marked detected, retaining the large version as run metadata.
  Source inspection also confirmed that the real subprocess path used
  ReadToEndAsync without an output-volume limit.

Fingerprinting now hashes untracked files through asynchronous FileStream
reads. The same 64 MiB fixture allocated 244,944 managed bytes after the change.
These are measured managed-allocation totals in the Windows test process, not
whole-system memory or Git-child RSS. Existing SHA-256 strings, fingerprint
format and changed-path attribution are preserved. Tests check exact hashes,
same-length edits, deterministic capture and cancellation without partial data.

CLI version probes now use the existing bounded ProcessRunner capture with a
65,536-character limit on each stream. Overflow terminates the producer and
records a fixed failure reason without returning a partial version; dispatch
retains its existing non-blocking warning behavior. Injected executor results
also receive a length check before concatenation and regex parsing. Native
synthetic process tests cover both stdout and stderr, including exact-limit
success and overflow while the producer would otherwise continue sleeping.
The three-second execution deadline is unchanged.

Final verification: 74 distinct tests passed in two runs: 47 across
RepositoryStateCaptureSecurityTests, CliVersionProbeTests,
PrimaryCheckoutChangeAttributionTests and BoundedProcessOutputTests; then 27
across TicketWorktreeServiceTests and AgentRunnerMockIntegrationTests. Nine
regression cases were added. All three baseline failures now pass. Integration
coverage includes primary-change attribution, coordinated synchronization,
canonical worktrees, run completion, approvals and mock-provider failures.
Builds used bin/SecurityCaptureAudit/ with a temporary NuGet configuration
containing only nuget.org. All data and processes used for attack/resource
reproductions belonged to disposable fixtures; no paid provider, live :5230
verification, real project or real secret was used.

The full suite, live UI and non-Windows execution were not tested. No dependency
versions were changed. Ten pre-existing modified/untracked files were preserved
byte-for-byte and excluded from this batch. Git status, tracked binary diffs,
path listings and evidence captures still need their own output-volume policy;
the entire repository fingerprint is not claimed to have bounded memory.
Next scope: stream or bound the tracked-diff/path-list captures while preserving
path attribution, then resume HTTP uploaded-file and path-boundary review.


## 2026-11-01 nightly audit: approval hook phase confusion

Confirmed a security boundary bypass in the Claude Code pre-effect hook adapter.
An untrusted agent controls tool arguments; those arguments must not control
whether KittyClaw performs a pre-effect approval check or only records an outcome.
The generated scripts instead inferred their phase by searching the entire raw
payload for PostToolUse. The actual root event could remain PreToolUse:

- The shell implementation could be switched by a valid Bash command such as
  `git push origin main # PostToolUse` when the event field preceded the input.
- PowerShell could be switched by a nested `hook_event_name: PostToolUse` field
  in structured tool arguments, such as metadata accepted by an MCP command
  tool. The nested field was mistaken for the provider's root event field.

The mistaken post-effect branch ignored a denied gate response (or a missing/
unavailable gate) and exited successfully without a permission verdict. Claude
Code's [official hook protocol](https://code.claude.com/docs/en/hooks) specifies
that exit 0 without output leaves the normal permission flow in control.
KittyClaw's Claude-compatible backend starts the provider with
`--dangerously-skip-permissions`, so that fallback does not enforce the missing
KittyClaw approval. This is an approval bypass by untrusted tool input, not an
anonymous HTTP exploit. It applies to the enforced Claude transport, including
Ollama through that transport.

Before the fix, a standalone replay of the actual shell script returned an
explicit deny for the control payload and empty stdout with exit 0 after adding
the harmless-looking comment. Twelve direct-hook regression cases then ran the
actual generated PowerShell and shell commands against disposable loopback gates.
Seven failed because a pre-effect hook returned empty stdout instead of an
explicit deny. The gate-denial fixtures also establish that the response was
ignored; the fake gate never authorized an effect. No actual push, publication,
secret access or other protected external effect was attempted.

Each registered command now carries its fixed PreToolUse or PostToolUse phase
as an argument. Both scripts use that trusted registration value, never tool
input text. A pre-effect invocation therefore returns its explicit verdict even
when arguments contain nested phase fields or comments. Genuine post-effect
hooks keep their silent outcome-reporting behavior. The default platform is
unchanged; tests can generate either script implementation on Windows.

Final verification: 88 distinct tests passed in two runs: 78 across
RuntimeHookPhaseSecurityTests, RuntimeBoundaryGateServiceTests and
RuntimeBoundaryEnforcementServiceTests, then 10 RuntimeEnforcementIntegrationTests.
All 12 new direct-hook cases pass, including all seven original failures, both
JSON property orders, missing environment and genuine post-effect calls. Existing
integration tests cover denied decisions, no decision, allow-once consumption,
Ollama transport and unsupported-provider refusal, using only the mock CLI and
isolated loopback Kestrel gates. Builds used bin/SecurityHookPhaseAudit/ and a
temporary NuGet configuration containing only nuget.org.

Both generated script implementations were executed on Windows (PowerShell and
Git Bash). Native Linux/macOS, the full test suite, live UI and a real paid
provider were not tested. The mock CLI is deliberately stricter than Claude
Code about empty pre-hook output, so the new direct-hook tests assert the actual
script verdict rather than relying on that mock to reproduce the native
permission fallback. That fallback assessment uses the official protocol and
the backend's explicit permission flag. The main :5230 instance, real projects
and real secrets were not used. No dependency versions were changed.
Previously generated invocation bundles are not rewritten by this patch.


## 2026-11-03 nightly audit: PowerShell action input injection

Confirmed code and parameter injection from ticket text and structured agent output
into deterministic PowerShell actions. `ColumnAgentDispatcher.TryParseResult` accepts
the model's summary as text; `ColumnActionExecutor` then substituted that text into
both native command-line syntax and inline executable source. An untrusted title or
summary could therefore change an owner-configured action's behavior. The action runs
as the app account and receives the project's injected secrets. This requires a
configured PowerShell action using the affected placeholders; no anonymous HTTP
exploit or sandbox escape is claimed.

Before correction, direct tests of the actual executor established two paths:

- A title or summary `ordinary" -Publish` broke the manually quoted argument string
  and enabled an extra script switch. The synthetic script only reported the switch;
  it did not publish anything. Quotes and trailing backslashes also corrupted data.
- Substituting `$()` text into a double-quoted inline script evaluated it as PowerShell
  code before execution. Five placeholder cases wrote a marker inside their own
  temporary fixture. `-EncodedCommand` only transported the already-injected source.

The initial 14-test executor suite had 12 failures and two passes. Those failures
include the five evaluated markers and both injected-switch cases; the other failures
cover argument corruption and the new environment contract, not additional distinct
vulnerabilities. Independent native replays also showed that a single literal
`-Publish` argument selects the switch without needing quote injection. U+2013,
U+2014 and U+2015 reproduced the same effect. This matches PowerShell's official
[argument collector](https://github.com/PowerShell/PowerShell/blob/master/src/Microsoft.PowerShell.ConsoleHost/host/msh/CommandLineParameterParser.cs)
and [parameter-marker definitions](https://github.com/PowerShell/PowerShell/blob/master/src/System.Management.Automation/engine/parser/CharTraits.cs).

The executor now passes the script path and each argument through `ArgumentList`.
Dynamic text arguments that would be parsed as script parameters fail before launch;
static configured switches remain available. Inline source no longer substitutes the
five ticket/model text placeholders. Such actions fail closed with guidance to read
the corresponding reserved environment variable instead. The variables carry the
actual context as data; quotes and `$()` in their values are not reevaluated. Numeric
ticket IDs and registered project slugs retain their existing substitution behavior.
The [PowerShell action input contract](../column-workflows.md#powershell-action-inputs)
documents the variable mapping and migration. Existing action definitions were not
rewritten: inline scripts using those text substitutions must migrate to the variables.
Explicitly configured script files and scripts that evaluate their own inputs remain
trusted executable code; this change does not add a process sandbox.

Final verification: 32 targeted tests passed in one final run across
`ColumnActionPowerShellSecurityTests`, `ColumnActionExecutorTests`,
`ProjectSecretInjectionTests` and `ColumnScheduledTaskEngineTests`. All 26 new cases
pass, including the five source-injection fixtures, quote injection, ASCII/Unicode
parameter markers, trailing backslashes, literal single dashes and leading whitespace,
static switches, environment transport and the Windows PowerShell fallback. Existing
checks verify stable execution keys, numeric placeholders, script failure reporting,
project-secret isolation/redaction and scheduled-action recovery. All original
failing cases now pass. An intermediate 24-test run also passed before adding the
Unicode marker and literal-boundary cases.

Tests ran on Windows with `pwsh` and the Windows PowerShell fallback, using temporary
projects, synthetic secrets and the mock provider. Builds used a detached worktree at
`f7ec39b` plus this patch, `bin/SecurityActionAudit/`, and a NuGet configuration containing
only nuget.org. The security source and tests were compared byte-for-byte with the main
checkout after the final build. Other work in progress was excluded from compilation
and remains untouched. The full suite, native Linux/macOS and live UI were not tested.
No real provider, real secret, real project attack or verification on :5230 was used.
No dependency versions were changed.


## 2026-11-08 nightly audit: default-proxy SSRF bypass

Confirmed a bypass of the HTTP action's `allowLocalTargets=false` boundary when a
configured default proxy was used. An untrusted action URL, including a ticket/model
placeholder value, could select a loopback destination. The guarded connection callback
validated the proxy's socket address instead of the final destination. A proxy with an
allowed endpoint could then forward the request to the prohibited service without the
explicit local-target opt-in. A state-changing request demonstrated an integrity impact;
no response-content disclosure or anonymous HTTP attack is claimed.

The [official .NET default-proxy contract](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclient.defaultproxy?view=net-10.0)
includes environment and platform proxy configuration. The
[.NET 10 connection-pool implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/ConnectionPool/HttpConnectionPool.cs)
connects to the proxy endpoint for proxied requests. `HttpActionClient` left proxy use
enabled, so its DNS/address check did not constrain the destination interpreted by that
proxy. The proxy endpoint must pass the existing address filter and accept the route;
loopback at a remote proxy denotes that proxy's host, not necessarily KittyClaw's host.

Before changing production code, a disposable console compiled the exact HTTP client
source and invoked its real factory. An owned proxy on a private interface forwarded
only to one owned loopback endpoint on an ephemeral port. Direct access was blocked;
the same POST through the proxy returned 200 and changed the fixture counter once.
No general-purpose open proxy, real service, user project or secret was used. Default
proxy overrides were process-local and restored; OS proxy settings were not changed.

The guarded handler now disables default-proxy routing, keeping its connection-time
DNS/address checks on the actual destination. Explicit `allowLocalTargets=true` retains
its existing proxy behavior. Redirects remain disabled in both modes. The
[HTTP action destination contract](../column-workflows.md#http-action-destinations)
documents direct routing, the opt-in and the existing private-LAN policy.

Replaying the original scenario after the fix returned no proxied response, forwarded
zero requests and left the target counter unchanged. A positive control with the proxy
still configured reached an allowed private-LAN fixture directly and returned 200.
The initial portable regression run had five failures and three passes: all five
protected cases consulted the default proxy. Those same eight tests pass after the fix.

Final verification: 13 distinct tests passed in the isolated repository build across
`HttpActionClientSecurityTests`, `ColumnActionExecutorTests` and
`ColumnScheduledTaskEngineTests`. The eight new cases cover HTTP/HTTPS loopback refusal,
localhost, IPv6 and IPv4-mapped loopback, absence of default-proxy delegation, explicit
local-target opt-in with and without a proxy, and redirect refusal. Existing checks
cover action errors, synthetic-secret injection/redaction, execution keys and scheduled
recovery. The main security source and test file matched the isolated build byte-for-byte.

The build used a detached worktree at `5adfb3e` plus this patch, output under
`bin/SecurityHttpProxyAudit/`, and a NuGet configuration containing only nuget.org.
Other work in progress was excluded and preserved. Native execution was on Windows
with .NET 10.0.14; the full suite, Linux/macOS, live UI and real external proxy services
were not tested. No verification used :5230 or :5231, no paid provider was dispatched,
and no dependency version was changed.

## 2026-11-12 nightly audit: ASP.NET Core multipart denial of service

Microsoft confirmed that multipart form parsing can exceed its header-count limit
and exhaust server resources. The Windows x64 ASP.NET Core runtime is affected
through 10.0.14 and patched in 10.0.15. KittyClaw's pipeline kit import passes
client-supplied multipart bodies to `ReadFormAsync` before checking the uploaded
file. For clients admitted to the enabled network login, ASP.NET Core's
antiforgery form-token lookup can also parse the request before KittyClaw checks
the TOTP code. [Microsoft advisory for CVE-2026-114149](https://github.com/dotnet/aspnetcore/security/advisories/GHSA-x33c-w48q-qrgc).
[ASP.NET Core 10.0.14 form-token lookup](https://github.com/dotnet/aspnetcore/blob/v10.0.14/src/Antiforgery/src/Internal/DefaultAntiforgeryTokenStore.cs).

A reference Windows x64 self-contained publish from `b21db08` with SDK 10.0.403
embedded ASP.NET Core 10.0.14. Both the runtime manifest and
`Microsoft.AspNetCore.WebUtilities.dll` reported that version. The SDK minimum in
`global.json` is now 10.0.404, which ships the patched runtime and also addresses
the local `dotnet watch` build-tampering vulnerability Microsoft documents for
Linux and macOS. [Microsoft .NET 10.0.15 release notes](https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.15/10.0.15.md).
[Microsoft advisory for CVE-2026-114155](https://github.com/dotnet/sdk/security/advisories/GHSA-q7j6-px74-p2vw).

Validation used the official Windows SDK ZIP with its SHA-512 verified in a
detached worktree. The installer smoke check refused a fixture containing the
10.0.14 reference manifest before process launch. The local Velopack build
produced a portable ZIP embedding ASP.NET Core, .NET and Windows Desktop
10.0.15, and its WebUtilities DLL reported 10.0.15. The smoke test passed on
:5232 with a fresh registry and provider executables disabled, including the
home page, static assets, and full-package download and verification from the
local update feed.

88 targeted tests passed across `PipelineExportHttpTests`,
`PipelineImportServiceTests`, `NetworkAccessTests`, `NetworkAllowlistTests`,
`McpEndpointTests` and `McpDisabledByDefaultTests`. These include ordinary
multipart import and authentication flows. The denial of service was established
from the advisory, the affected call sites and the shipped runtime version.
Validation exercised the package version guard and ordinary requests.

Scope was Windows x64 runtime packaging and targeted regression coverage. The
full suite and Linux/macOS were outside this run. The host SDK and running
instances were left in place. Source installations need a patched .NET runtime
and the new SDK for builds. Existing self-contained installations receive the
runtime fix through a rebuilt KittyClaw package. No artifacts were published or
installed.

## 2026-11-13 nightly audit: legacy dashboard migration path escape

The startup dashboard migration reads legacy tile sidecars from the project
workspace. A contributor who controls a sidecar in a registered checkout could
supply a `script` reference outside `.dashboard/`. The migration used the
resolved path as the source of `File.Move`, removing the file from its original
location when the application account had the required permissions. This breaks
the migration's filesystem boundary independently of whether the tile ever
refreshes. A directory junction or symbolic link can also cross that boundary
even when the reference appears to stay inside the dashboard.

`DashboardService.MigrateAsync` now checks the script path and each path
component before creating the destination or moving either legacy file. It
rejects rooted or escaping paths and reparse points. It also excludes linked
dashboard roots, sidecars, outputs and destination directories from migration.
Relative references to supported scripts in ordinary nested dashboard folders
continue to migrate.

47 tests passed in a detached Windows worktree at `43dcd7c` plus this patch with
SDK 10.0.404. Four cases verify rejection of absolute, relative-outside and
junction paths with synthetic files, and successful migration of a nested
in-dashboard script. The other checks covered existing migration, persistence,
script handling, refresh, dashboard worktree writes and file API behavior.
The finding was established from the source. Validation exercised the fixed
migration and did not execute scripts referenced by rejected fixtures.

This fix covers the legacy migration entry point. The checks are path-based
and do not provide isolation against concurrent filesystem replacement by a
process already able to modify the checkout. Linux/macOS and the full suite
were outside this run. No live instance, user workspace or real provider was
used, and dependency versions were unchanged.

## 2026-11-14 nightly audit: links in active dashboard tiles

The legacy migration fix left the usual dashboard operations on a separate
path. A project checkout can contain a symlink in a tile, and a filesystem
writer can also place a junction at the dashboard root or tile directory.
REST checked lexical containment only. The tile service could then read a
linked output or script, overwrite a linked sidecar, or modify files in a
linked directory using the application's filesystem permissions. Opening a
dashboard could also render the linked output in an authorized user's view.

Dashboard discovery and file operations now check the dashboard root, tile
directory and relevant files for reparse points. Tile names must be single
directory entries. The API and editor use the same directory check and script
creation uses the same write guard as sidecars and outputs. Output replacement
checks the existing output set before deleting files. Refresh can only select
ordinary script files. The configured workspace remains the trusted root.

53 targeted tests passed on Windows with SDK 10.0.404 in a detached worktree
at `d6ff310` plus this patch. The API fixtures verified that linked roots and
tiles are absent from discovery, raw reads and output updates return HTTP 400,
and a synthetic external output remains intact. Service tests checked linked
output, sidecar and script reads and writes, as well as single-component names.
Existing checks covered ordinary worktree writes, migration, refresh, script
handling, storage diagnostics and the file API. No rejected script was run.

These path checks cover existing reparse points. The full suite, Linux/macOS,
browser interaction and concurrent filesystem replacement were outside this
run. The HTTP tests used an in-process server. No live instance, real provider
or user workspace was involved.

## 2026-11-16 nightly audit: project secrets in escaped provider JSON

Provider output can include untrusted content from models and tool results.
`AgentStreamPump` applied the vault redactor to the raw line before parsing.
The filter recognized a fixed set of textual representations. A valid JSON
string with an equivalent escape spelling could pass that filter. The parser
then decoded the text, and event subscribers could receive and save the
unmasked value. `RunLogStore` persisted the same events in run snapshots.

The runner now decodes escaped string tokens in JSON objects and arrays for
redaction before either stream pump publishes events. It rewrites affected
tokens and preserves the surrounding JSON. Existing literal redaction still
applies to plain output. The fix targets KittyClaw's new run events and
snapshots. Previously saved logs and provider-owned transcripts are outside
its scope.

55 targeted tests passed under Windows with SDK 10.0.404 in a detached
worktree at `2910ca4`. A mock CLI fixture verified redaction in published
assistant events, terminal result details and reloaded snapshots. It also
verified that terminal status and token usage survive. A second regression
verified preservation of unrelated JSON tokens and formatting. Existing
coverage included vault isolation and REST, run persistence, quota signals
and stream handling. The affected path was established from the source, and
the fixture exercised the fixed version using disposable data. The full test
suite and Linux/macOS were outside this run.

## 2026-11-17 nightly audit: linked project skill paths

`ProjectSkillService` assumed that files under the project skill root stayed
in the configured workspace. A checkout containing a symbolic link could
make REST, MCP or processor skill loading read an external file with the
application account's permissions. A linked root or skill directory could
also redirect creation, updates or deletion. Pipeline kit confirmation used
the same directory layout independently, and its identical-content check read
existing skill trees recursively.

The management service now checks the `.agents` and `skills` directories, the
individual skill directory and the instruction/metadata files for reparse
points. Linked skills are unavailable to the shared REST/MCP handlers and
processor loader. Creation requires ordinary root directories. Kit analysis
and confirmation reject a linked root when the kit references project skills.
Collision checking refuses to fingerprint linked files or descend into linked
directories. Ordinary skills can still be reused by content or renamed.

83 targeted tests passed with SDK 10.0.404 in an isolated Windows worktree
at `76ad318` plus this patch. The fixtures covered linked directory refusal
through REST and MCP, link handling in skill file operations, preservation of
synthetic external targets, import refusal with an unchanged project, and
collision handling for linked descendants. The suite also covered normal skill
creation, processor configuration, MCP transport and kit import/export.
File-link cases require symbolic-link privileges on Windows. No live instance,
user project or real provider was used.

This batch covers project skill management and the skill paths used by kits.
The configured workspace is the trusted root. Checks cover existing links on
the validated paths. Concurrent filesystem replacement, legacy agent identity
paths, Linux/macOS and the full test suite were outside this run.

## 2026-11-19 nightly audit: security fixes absent from the development branch

The active checkout changed to `dev` at `c41bcb8`. The security changes recorded
from November 3 through November 17 remained on `codex/reliable-evidence-capture`
and were absent from this development history. Source inspection confirmed the
earlier unsafe PowerShell interpolation, default-proxy SSRF path, raw-only secret
redaction and linked dashboard/skill paths. The SDK minimum was still 10.0.403.

This batch ports the seven security commits `5adfb3e`, `b21db08`, `43dcd7c`,
`d6ff310`, `2910ca4`, `76ad318` and `d207161` onto the current development code.
The preceding dated sections preserve the original findings and their original
validation. The results below apply to this integration at `c41bcb8`.

264 targeted tests passed under Windows using the isolated SDK 10.0.404.
The run included PowerShell actions, proxy routing, secret injection and vault
APIs, dashboard file operations and migration, project skills, pipeline kits,
network access, MCP transport, Copilot support and the API endpoint inventory.
The TRX counters recorded 264 executed, 264 passed, zero failures and zero
unexecuted tests.

The Windows installer and portable/update packages were built locally as
`0.30.0-security.20261119`. `Test-WindowsInstaller.ps1` passed on :5232 with a
new data directory and disabled provider executables, including local package
download/hash verification. Its runtime manifest and WebUtilities DLL reported
10.0.15. A separate manifest-only 10.0.14 fixture was refused before launch.
The [Microsoft multipart advisory](https://github.com/dotnet/aspnetcore/security/advisories/GHSA-x33c-w48q-qrgc)
and [10.0.15 release notes](https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.15/10.0.15.md)
were checked again before porting the SDK minimum.

The full suite and Linux/macOS were outside this run. Existing path-check race
limits and the scope of log redaction remain as documented above. The system
SDK remains 10.0.403. Source builds require the new SDK, available in the
isolated audit SDK directory. Running source installations need an updated
runtime, and existing self-contained installations need a rebuilt package.
No release was pushed, published or deployed, and the main instance was not
used for validation.

## 2026-11-23 nightly audit: automatic context and processor memory paths

An untrusted checkout could redirect automatic context reads through links under
`.agents`. The host read preambles, legacy agent skills, memory indexes and flat
memory files before passing their contents to a provider. Consolidation also read
linked topic files and its instruction file. These reads happened outside the
provider's tool hooks, so `Enforce` did not protect the initial prompt. This could
disclose an external file readable by the application account without a tool
request or a boundary approval.

Processor memory migration could import external text through the same layout.
Native lesson capitalization could also rewrite an external `MEMORY.md` through
a linked memory directory. The input was the workspace-controlled link; the host
performed the read or write with its own filesystem permissions.

`AgentContextFiles` now requires paths beneath the configured workspace's `.agents`
directory and rejects existing reparse points at every descendant component.
Automatic prompt reads, chat background skills and consolidation instructions use
this check. Runner preparation failures stay visible on the registered run; chat
returns `invalid_agent_context`. Processor memory migration and capitalization
validate their canonical and legacy paths before reading or writing. Ordinary
optional context and memory migration remain supported.

A disposable Windows fixture against `4b0641e` first demonstrated that a linked
agent directory placed an external synthetic marker in an `Enforce` prompt.
Six regression cases were then executed against the three original runner and
processor-memory classes: all six failed as expected because the original code
followed the links instead of rejecting them. Capitalization returned `Succeeded`
through the external memory directory. The corrected source was restored after
that baseline run.

133 targeted tests passed with the isolated SDK 10.0.404, including 16 new cases.
The TRX counters recorded 133 executed, 133 passed, zero failures and zero
unexecuted tests. Coverage included file links and directory junctions, HTTP chat
rejection, preservation of synthetic external targets, ordinary prompt assembly,
legacy memory migration, capitalization, simulated dispatch and resume, secret
injection and native pre-effect hook enforcement. HTTP checks used disposable
in-process or random-loopback fixture servers. No real provider, main instance,
user project or real secret was used.

The configured workspace remains the trusted root. These are checks of existing
paths, not protection against concurrent filesystem replacement or a sandbox for
provider-owned tool reads. Other workspace configuration and runtime-state paths,
the full test suite, browser interaction and native Linux/macOS validation were
outside this batch. No dependency version was changed.

## 2026-11-25 nightly audit: template initialization writes through workspace links

`AgentsTemplateService.InitializeAsync` followed checkout-controlled links while
copying shared and root guidance. A linked `.agents` directory could create the
three shared template files outside the selected workspace even without overwrite.
With overwrite selected, a linked `AGENTS.md` could instead truncate an external
file and replace it with template content. The owner selected workspace-relative
conflicts; the checkout-controlled link chose the unrelated target. This was an
integrity violation by host file I/O, without provider involvement.

Two fixtures against `ccc48e3` reproduced both effects using synthetic external
files: ordinary initialization wrote all three shared files through a junction,
and overwrite replaced the external file behind the root guidance link.

`ValidateWriteDestinations` now checks the complete write plan before any asset is
copied or Git initialized. It rejects linked files and linked parent directories
for destinations that would be written. Project creation invokes the same
preflight before adding its registry entry, through its existing error handling.
Existing files selected for skipping remain untouched, including owner-managed
links. On Windows, `File.Exists` also recognizes a dangling file link; the existing
skip behavior is preserved, while a planned overwrite of that link is rejected.

49 targeted tests passed with the isolated SDK 10.0.404: 49 executed, 49 passed,
zero failures and zero unexecuted tests in the final TRX. The run included 21 new
regression cases for directory links, all five asset destinations, absent and
existing external targets, preservation before refusal, and skip behavior. Existing
tests covered initialization, retained roles/configuration, Git eligibility,
repository intake and server rendering of project creation. Tests used disposable
workspaces and an in-process HTTP fixture. No real provider, user project, real
secret or main instance was used.

A diagnostic copy through a junction explicitly selected as the workspace root
raised `DirectoryNotFoundException` both with and without the new preflight. This
pre-existing functional behavior was not changed; the root-link regression checks
only that preflight trusts the selected root. Copying through such a root needs a
separate functional reproduction before changing its behavior.

The checks cover existing descendants and do not protect against concurrent path
replacement. The full suite, interactive browser journeys and native Linux/macOS
validation were outside this batch. No dependency or template content was changed.
