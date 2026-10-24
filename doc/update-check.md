# Windows installation and updates

## Purpose
Ship a per-user Windows x64 installer with .NET included, check for updates on GitHub,
download them automatically, and let the owner confirm installation without interrupting active agents.
Source installations retain the dismissible release-link banner described below.

## Packaging and desktop lifetime

- `tools/build-windows-installer.ps1` publishes `KittyClaw.Web` with
  `KittyClawWindowsInstaller=true`, `win-x64`, and `--self-contained true`, then calls
  the pinned `vpk pack` tool. It produces the installer, full package, portable ZIP,
  and `releases.win.json`. It does not publish externally.
- Only installer builds target `net10.0-windows`, use WinForms and WebView2 for the desktop window and notification
  icon, and hide the console. Normal source builds still target `net10.0`.
- `branding/KittyClaw.ico` contains transparent 16–256 px versions of the Posture 07 KittyClaw
  pictogram. `ApplicationIcon` embeds it in the Windows executable and `vpk --icon`
  applies it to Setup; the notification icon is extracted from the executable.
- `Program.cs` calls `VelopackApp.Build().SetAutoApplyOnStartup(false).Run()` before
  creating the host. Installer hooks exit before touching application data.
  Startup auto-apply is disabled so a second launch cannot replace a running server.
- `WindowsDesktopLifetime` owns a per-data-directory named mutex, opens a borderless WebView2 window
  after startup, and provides Open, Check for updates, and Quit actions in the notification area.
  Menu actions and update notifications use the selected application language and refresh
  immediately when it changes. New settings use the operating-system UI language when
  supported (Portuguese maps to Brazilian Portuguese; `zh-CN`, `zh-SG` and `zh-Hans`
  locales map to Simplified Chinese `zh-CN`), otherwise English. Traditional Chinese
  locales fall back to English. Saved choices take precedence.
  Window controls are integrated into the existing page header without an extra title bar.
  `WindowsDesktopShell.js` applies one fixed-height header style from `windows-desktop.css`
  to every view, with the KittyClaw home link on the left and window controls on the right.
  A matching fallback keeps the shell visible during loading and on error/not-found pages.
  Browser views retain their regular layout; viewport-sized boards account for the fixed header.
  Shared conversation drawers and their overlays start below it and use the remaining viewport height,
  keeping the conversation header and composer visible even in compact windows.
  Long toolbars scroll between the fixed logo and window controls, using arrow buttons or
  the mouse wheel. Search help opens below the header so the scrolling strip cannot clip it.
  Free header space supports native dragging; interactive controls remain clickable.
  Closing hides the window while agents continue running. Relaunching the shortcut signals
  the existing instance to restore its window. External HTTP(S) and mail links open externally.
  WebView2 profile data is isolated per data directory under `%LOCALAPPDATA%/Ekioo.KittyClaw/WebView2`.
  Setup installs the Evergreen WebView2 runtime if missing; portable users must install it separately.
  The manual check downloads available updates in the background and reports the result
  in a notification. Clicking the notification opens KittyClaw, where installation and
  restart still require owner confirmation. The menu action is disabled during its check.
  The content
  root is the executable directory, independent of the shortcut's working directory.
  `KITTYCLAW_DESKTOP=false` disables the desktop shell for isolated tests.
- Setup creates desktop and Start-menu shortcuts. Its first-run hook confirms that
  installation completed before launching the server. Desktop startup failures show
  an error dialog and write `%LOCALAPPDATA%/Ekioo.KittyClaw/logs/startup-error.log`;
  an occupied port explicitly points the owner to another running installation.
- Binaries live in `%LOCALAPPDATA%/Ekioo.KittyClaw`; application data remains in
  `%APPDATA%/KittyClaw`. Updates replace binaries, not databases or project workspaces.
- .NET, ASP.NET Core, and Windows Desktop runtimes are embedded. Runtime security
  patches require a new application release. Provider CLIs remain external.
- .NET 10's framework-assets target excludes `WinExe`. `IncludeWindowsBlazorBootstrap`
  links the matching SDK package's browser scripts into the normal static-assets pipeline
  for the tray build. Packaging fails if `wwwroot/_framework/blazor.web.js` is absent;
  the isolated smoke test also requests that script and verifies the rendered footer version.

## Installed update workflow

- `VelopackUpdateClient` wraps `UpdateManager` with a public `GithubSource` pointing
  at `https://github.com/Ekioo/KittyClaw`, excluding prereleases. No token is embedded.
  Only deployments recognized by Velopack can download or apply packages.
- `DownloadUpdatesAsync` selects available deltas using the cached installed full package,
  reconstructs the target, and validates it. Velopack falls back to a full download if a
  required baseline/delta is missing or reconstruction fails. No delta-disabling option is set.
- `InstalledUpdateService` serializes checks and downloads. Each installed update check
  automatically downloads an available package in the background, including while agents
  are running. Failed checks or downloads retry on the next scheduled check.
  `UpdateBanner.razor` shows download progress and errors, then offers only
  **Update and restart** for explicit owner confirmation. Prepared downloads remain
  available after restart, even offline, without downloading them again.
- Installed builds check on startup and hourly, independently of the legacy API throttle.
- Downloading does not stop the host. Installation closes the registry dispatch gate even
  with active agents, then initiates graceful host shutdown. Provider processes are stopped,
  tracked execution leases drain through finalization, and SQLite state is preserved.
  Interrupted chats are marked for automatic startup recovery; column executions retain
  their durable checkpoint and queued automations remain retryable after restart.
- If starting the updater fails, the dispatch gate is released and the application keeps running.
- `WaitExitThenApplyUpdates` starts the updater, then `StopApplication()` stops the host
  gracefully. Velopack waits for process exit, applies the package, and restarts the app.
  Its wait is limited to 60 seconds; the ordinary host shutdown timeout is shorter.
  No immediate `Environment.Exit` is used. The desktop host reopens the desktop window after restart.

## Publication and verification

- `tools/publish-release.ps1` builds Windows artifacts before creating a release and
  uploads the installer, `.nupkg`, and feed alongside the existing ZIP. New releases
  remain drafts until all assets upload. See [RELEASING.md](../RELEASING.md).
- `Get-WindowsReleaseBaseline.ps1` seeds staging with the newest older stable Windows
  full package. Packaging explicitly enables deltas, and publication fails if the delta
  is missing despite a baseline. First-time releases use a full package. Only the new
  version's full/delta packages and feed entries are uploaded; older releases retain
  their assets for clients skipping versions. Network/authentication failures abort release.
- `Test-WindowsReleaseBaseline.ps1` tests selection and failure handling without network
  access. `Test-WindowsDeltaUpdate.ps1` verifies reconstruction with no target full package
  on the test server. Windows CI builds two versions and runs both tests.
- `.github/workflows/windows-installer.yml` packages CI artifacts without publishing.
- `tools/Test-WindowsInstaller.ps1` verifies the self-contained host and static assets
  on an isolated port, then reads the actual feed and validates the downloaded package
  through `TestVelopackLocator`. It does not install over the developer's instance.
- `InstalledUpdateServiceTests` cover busy agents, finalization, dispatch/update races,
  offline pending packages, failures, retry, concurrent clicks, and source installations.
  The `IInstalledUpdateClient` boundary avoids real agents and developer-instance restarts.
- The build script accepts an optional `-SignTemplate`. Signing credentials are not stored
  in the repository.

## Legacy release-link components
- `KittyClaw.Web/Services/UpdateCheckService.cs` — hosted `BackgroundService` that polls the GitHub Releases API (`/repos/Ekioo/KittyClaw/releases/latest`) at most once every 24h, caches the latest tag in memory, and exposes `CurrentVersion`, `LatestVersion`, `HasUpdate`, and an `OnChange` event. Also offers `Dismiss()`, `SimulateUpdate(version)`, and `ResetSimulation()`.
- `KittyClaw.Core/Services/VersionCompare.cs` — `IsNewer(current, latest)` parses `Major.Minor.Build`, tolerates a leading `v`/`V`, and returns `false` for unparseable inputs.
- `KittyClaw.Core/Services/AppSettingsService.cs` — persists `UpdateDismissedVersion` and `UpdateCheckLastRun` in `%APPDATA%/KittyClaw/settings.json` so dismissals and throttling survive restarts.
- `KittyClaw.Web/Components/Layout/UpdateBanner.razor` — `@rendermode InteractiveServer` component that subscribes to `OnChange`, renders the banner when `HasUpdate` is true, and calls `Dismiss()` on the ✕ button. Embedded in `MainLayout.razor` via `<UpdateBanner />`.
- `KittyClaw.Web/wwwroot/css/dashboard.css` — `.update-banner`, `.update-banner-icon`, `.update-banner-text`, `.update-banner-dismiss` styles.

## Entry points
- Background poll: started by `AddHostedService(UpdateCheckService)` in `Program.cs`; source builds run once on startup, then every hour, but only hit the Releases API when the last run is older than 24h.
- UI: the `<UpdateBanner />` in `MainLayout.razor` renders above every page when an update is available and not dismissed for that tag.
- Dev-only HTTP endpoints (registered when `app.Environment.IsDevelopment()`):
  - `POST /api/dev/update-check/simulate?version={tag}` — forces a fake latest version so the banner shows without waiting on GitHub.
  - `POST /api/dev/update-check/reset` — clears the simulated state, dismissal, and last-run timestamp.

## External dependencies
- [Velopack](https://docs.velopack.io/integrating/overview) SDK and pinned packaging tool; GitHub release packages and feed.
- GitHub Releases API (`api.github.com`) — unauthenticated `GET` with a `KittyClaw-UpdateCheck` user-agent. Failures are logged at Debug and never throw to the UI.
- [Storage](./storage.md) — `AppSettingsService` reads/writes `settings.json` for the dismissed-version and last-run fields.
- The `MainLayout` must remain interactive enough for the dismiss button to fire; the banner is itself `InteractiveServer` so this works even when the layout stays SSR.
