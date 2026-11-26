# Experimental macOS distribution

## Purpose and status

Build KittyClaw for Apple Silicon and Intel without an Apple Developer subscription.
This is an experimental browser-based desktop distribution, with no native tray menu.
The `.app` receives an ad-hoc signature (`codesign -s -`), not a Developer ID signature;
the `.pkg` installer is unsigned and neither artifact is notarized. This does not
establish publisher identity or satisfy Gatekeeper's normal distribution policy.
No signing certificate, Apple account, notarization profile or signing secret is used.

GitHub runners validate native execution and actual bundle replacement. They do not
validate the GUI installer, quarantine, Gatekeeper exceptions, browser launch from
Finder, Keychain prompts, or privileged updates to `/Applications`. A first Mac tester
must still confirm these paths before the distribution can be called stable.

## Packaging

`tools/build-macos-installer.ps1` runs on macOS with the SDK in `global.json`:

```powershell
./tools/build-macos-installer.ps1 -Version 0.20.0-macos.1 -Runtime osx-arm64
./tools/build-macos-installer.ps1 -Version 0.20.0-macos.1 -Runtime osx-x64
```

The script restores the same pinned `vpk` tool as Windows, publishes a self-contained
`net10.0` host with `KittyClawMacOSInstaller=true`, generates an `.icns` from the existing
Posture 07 pictogram using macOS tools, then packs the installer, portable ZIP, full
`.nupkg` and JSON feed. Outputs default to `publish-macos/<version>/<runtime>/`.
Stages are unique and no user-supplied directory is recursively deleted. Packaging
does not publish externally. Runtime security patches require a new application build.

Before packing, the script creates a bundle and moves managed assemblies, configuration
and web assets into `Contents/Resources/app`, with relative symlinks from `Contents/MacOS`.
Only native Mach-O binaries stay directly in `MacOS`, with reverse file symlinks in
the resources directory to keep .NET native-library probing valid. This is required for updates:
`codesign` otherwise stores signatures of managed DLLs in extended attributes, which
portable ZIPs/installers preserve but `.nupkg` replacement does not. Resource sealing
keeps the signature valid after replacement without re-signing on the user's machine.

The local signature is applied by Velopack after inserting its updater and metadata,
before creating the archives. `tools/macos/KittyClaw.entitlements` permits .NET JIT execution
and loading the bundled ad-hoc signed native libraries. No App Sandbox is enabled.
The installer embeds `tools/macos/INSTALL.txt`, also included with CI artifacts.

Separate package IDs and channels prevent cross-architecture package/cache collisions:

| Runtime | Package ID | Feed |
| --- | --- | --- |
| `osx-arm64` | `Ekioo.KittyClaw.osx-arm64.Experimental` | `releases.osx-arm64-experimental.json` |
| `osx-x64` | `Ekioo.KittyClaw.osx-x64.Experimental` | `releases.osx-x64-experimental.json` |

Delta generation is disabled for this first experimental distribution. Full downloads
still receive Velopack's size/hash validation. Windows packaging is unchanged.

## Application lifetime

Only macOS installer builds compile the `MACOS_INSTALLER` startup path. Content roots
resolve from `AppContext.BaseDirectory`, so Finder's working directory cannot hide
configuration/static assets. `MacDesktopLifetime` holds an OS file lock in the data
directory; another bundle launch opens the browser instead of starting another host.
The lock file remains on disk after exit; its existence alone is not a running lock.
`KITTYCLAW_DESKTOP=false` disables the lock/browser shell for isolated tests.

After the HTTP host starts, `/usr/bin/open` opens a loopback HTTP(S) listener in the
default browser. Closing the browser leaves the server running. Reopen the application
to reopen the browser; use Activity Monitor's **Quit** action on `KittyClaw.Web` to stop
it. The Windows tray and embedded WebView2 window are not part of this distribution.

Application data uses the existing `Environment.SpecialFolder.ApplicationData` /
`KittyClaw` location, normally `~/Library/Application Support/KittyClaw`, outside the
bundle. Stop an existing source installation first: it uses the same port and data,
and does not participate in the bundle's desktop lock. Back up data before testing.
Provider CLIs remain external; Finder does not load a terminal's shell profiles.
See [agent providers](agent-providers.md) for executable detection and overrides.

## Updates and publication

`VelopackUpdateClient` now supports macOS and allows GitHub prereleases on that platform.
The installed manifest selects its own channel; no override can accidentally select
the Windows feed. Source installations remain unsupported by the installed updater
and keep the existing release-link behavior. Windows excludes prereleases as before.
The background download, explicit **Update and restart**, dispatch gate and graceful
host shutdown reuse the [installed update workflow](update-check.md#installed-update-workflow).

The normal [release process](../RELEASING.md) includes both macOS architectures.
Pushing a stable `vX.Y.Z` tag starts the native workflow with that exact version.
`tools/publish-release.ps1`, still run on Windows, calls `Get-MacOSReleaseArtifacts.ps1`
before creating or changing the release. This helper waits up to 60 minutes for the
successful tag-push run matching both the tag and commit, downloads both artifacts,
and verifies the exact package IDs, versions, filenames, sizes and SHA-256 hashes.
It returns no publishable assets if either architecture fails or any validation fails.

The publisher attaches each `.pkg`, portable ZIP, full `.nupkg` and experimental JSON
feed to the same release as Windows, along with `KittyClaw-macOS-INSTALL.txt`.
Experimental channel names and ad-hoc signing remain unchanged on stable releases.
New releases stay drafts until all uploads finish; existing drafts can be resumed,
but published releases cannot be overwritten. Retry the publisher if native CI fails
or times out, after correcting/re-running the failed job. No Apple credentials are needed.
The native workflow has read-only repository permissions and never publishes a release;
the existing authenticated publisher remains the only release writer.

Ordinary branch CI artifacts are downloads, not an update endpoint, and cannot pass
the publisher's tag/version checks. Manual prereleases can still use increasing SemVer
versions, with matching packages and feeds attached to a draft before publication.
Never rename a package referenced by a feed. Each architecture has unique asset names.
`tools/Test-MacOSReleaseArtifacts.ps1` exercises publication gates without a network
request or a real release, including provenance, pending jobs, corrupt packages,
wrong versions/architectures and incomplete uploads.

Installing to `~/Applications` avoids requiring system-wide write access during updates.
Gatekeeper can require approval of a downloaded installer/application, and can prompt
again after updating. Only use **System Settings → Privacy & Security → Open Anyway**
when the download is trusted; do not disable Gatekeeper globally. Managed devices can
forbid exceptions. Signature/notarization-free updates without prompts are not promised.

## GitHub Actions verification

`.github/workflows/macos-installer.yml` runs on pushes and PRs to `main`/`dev`, stable
version tag pushes, or via
**Run workflow** with an optional target SemVer. It uses native `macos-26` ARM64 and
`macos-15-intel` x64 runners and creates two versions per job:

1. Run the installed-update shutdown tests and desktop lifetime/URL tests.
2. Build baseline and target self-contained packages without Apple credentials.
3. Extract the baseline ZIP with `ditto`, preserving permissions and symlinks.
4. Verify the local signature, embedded frameworks/security minimum and native updater.
   Expand the target `.pkg` with `pkgutil` without installing it or running its
   postinstall script, and validate its application signature.
5. Start the bundled host from an unrelated working directory on port 5232, with a fresh
   registry, blocked provider executables and no external .NET runtime dependency.
   External HTTP requests fail fast through an unreachable proxy; loopback stays local.
   Verify the home/footer version, empty projects API, CSS and Blazor bootstrap.
6. Stop gracefully, persist a language preference and a SQLite sentinel, and run
   `tools/macos/VerifyUpdate.cs`. It reads the real bundle manifest with
   `OsxVelopackLocator`, downloads/validates the target full package and launches the
   actual `UpdateMac` helper against the disposable bundle. A custom process adapter
   tells the locator which copied bundle to inspect; updater execution is real.
7. Wait for replacement, recheck the signature and compare the updated assembly to
   the target installer's payload. Start the target server and verify its version,
   saved language and SQLite data. Native replacement is tested;
   automatic relaunch by the updater is not (the harness starts the target explicitly).
8. Upload target packages only after success, and diagnostic host logs even on failure.

The probe refuses to update bundles outside its unique scratch directory. It overrides
the package cache into that directory and never installs into `/Applications`, uses
main ports 5230/5231, accesses existing project databases or dispatches real agents.

To reproduce on a native Mac after building two versions into separate directories:

```powershell
./tools/Test-MacOSInstaller.ps1 -BaselineDir publish-macos/baseline -TargetDir publish-macos/target -BaselineVersion 0.20.0-macos.1 -TargetVersion 0.20.0-macos.2 -Runtime osx-arm64
```

## External dependencies

- [Velopack macOS packaging](https://docs.velopack.io/packaging/operating-systems/macos)
  and [channels](https://docs.velopack.io/packaging/channels).
- macOS `sips`, `iconutil`, `codesign`, `ditto`, `pkgbuild`, `productbuild`, `sqlite3`.
- [.NET macOS publishing](https://learn.microsoft.com/en-us/dotnet/core/deploying/macos).
- [GitHub-hosted runner architectures](https://docs.github.com/en/actions/reference/runners/github-hosted-runners).
- [Apple's manual approval guidance](https://support.apple.com/en-us/102445).
