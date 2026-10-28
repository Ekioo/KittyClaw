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
