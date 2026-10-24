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
