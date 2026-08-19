# Project secrets vault

## Purpose

Per-project, write-only secret storage. Owners register named secrets (environment-variable
names) through the API/UI; KittyClaw injects the values only into agent and automation
subprocesses that belong to the same project, and redacts them from run streams and logs.
Saved values are never returned by the API or UI.

## Key components

| Component | Location | Role |
|---|---|---|
| `ProjectSecretVault` | `KittyClaw.Core/Services/ProjectSecretVault.cs` | Vault files, envelope format, per-project locking, atomic writes |
| `IProjectSecretProtector` + implementations | `KittyClaw.Core/Services/ProjectSecretProtection.cs` | Platform-native encryption of the vault payload |
| `ProjectSecretProtectors.CreateForCurrentPlatform()` | same file | Selects the native protector at startup |
| `MacOsKeychainKeyStore` / `LinuxSecretServiceKeyStore` | same file | Master-key storage in the native secret store |
| `SecretRedactor` | `KittyClaw.Core/Services/ProjectSecretVault.cs` | Strips secret values from streamed output |
| `Endpoints.ProjectSecrets` | `KittyClaw.Web/Api/Endpoints.ProjectSecrets.cs` | Write-only REST surface (`GET` list metadata, `PUT`, `DELETE`) |

Injection call sites: `AgentRunner` (agent subprocesses), `ColumnActionExecutor` and
`NetworkActionHandler` (automation/PowerShell subprocesses). The KittyClaw server process
environment is never mutated.

## Storage and on-disk format

Vault files live outside every workspace at `<data-dir>/secrets/<slug>.vault`
(see [Storage](./storage.md) for `<data-dir>`).

Since #283 each file is a versioned envelope: `KCSV` magic (4 bytes), format version (`2`),
protector id (1 byte), then the protector's ciphertext payload.

| Protector id | Platform | Mechanism |
|---|---|---|
| 1 | Windows | DPAPI, `CurrentUser` scope — the whole payload is a DPAPI blob |
| 2 | macOS | AES-256-GCM; master key in the login Keychain |
| 3 | Linux | AES-256-GCM; master key in the freedesktop Secret Service |

Vaults written by the #278 release are headerless raw DPAPI blobs. They remain readable on
Windows and are upgraded to the enveloped format on the next write. There is no downgrade
path: files written by #283 are not readable by #278. Vault files are bound to the machine
and account that wrote them and are not portable across machines or platforms; an envelope
whose protector id does not match the current platform fails closed with a message naming
both mechanisms.

## Platform dependencies

- **Windows** — DPAPI, no external dependency.
- **macOS** — the system `/usr/bin/security` CLI and the login Keychain. The master key is a
  generic password (service `KittyClaw project secrets`, account `kittyclaw`), written via
  `security -i` on stdin so it never appears in a process argument list.
- **Linux** — `secret-tool` from libsecret (package `libsecret-tools` on Debian/Ubuntu,
  `libsecret` on Fedora/Arch), a D-Bus session bus, and an unlocked keyring daemon
  (gnome-keyring, KWallet). Attributes: `service=kittyclaw.project-secrets`,
  `account=kittyclaw`. On headless hosts, unlock or start the keyring before using secrets.

## Fail-closed behavior

There is no plaintext fallback under any circumstance. When the native mechanism is missing,
locked, or mismatched, operations throw `ProjectSecretProtectionUnavailableException` (or
`PlatformNotSupportedException` on unsupported platforms); the REST endpoints translate both
to `503` with the actionable message and never echo secret values. A failed write leaves no
file behind; a failed read leaves the vault file untouched. Tampered payloads fail with a
`CryptographicException` (AES-GCM authentication or DPAPI integrity) and surface as the
generic `503` "vault could not be unlocked" response.
