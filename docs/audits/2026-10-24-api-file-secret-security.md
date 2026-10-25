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
