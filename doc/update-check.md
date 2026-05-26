# Update check

## Purpose
Detects when a newer KittyClaw release is published on GitHub and surfaces a dismissible banner above the app shell so the user knows an update is available.

## Key components
- `KittyClaw.Web/Services/UpdateCheckService.cs` — hosted `BackgroundService` that polls the GitHub Releases API (`/repos/Ekioo/KittyClaw/releases/latest`) at most once every 24h, caches the latest tag in memory, and exposes `CurrentVersion`, `LatestVersion`, `HasUpdate`, and an `OnChange` event. Also offers `Dismiss()`, `SimulateUpdate(version)`, and `ResetSimulation()`.
- `KittyClaw.Core/Services/VersionCompare.cs` — `IsNewer(current, latest)` parses `Major.Minor.Build`, tolerates a leading `v`/`V`, and returns `false` for unparseable inputs.
- `KittyClaw.Core/Services/AppSettingsService.cs` — persists `UpdateDismissedVersion` and `UpdateCheckLastRun` in `%APPDATA%/KittyClaw/settings.json` so dismissals and throttling survive restarts.
- `KittyClaw.Web/Components/Layout/UpdateBanner.razor` — `@rendermode InteractiveServer` component that subscribes to `OnChange`, renders the banner when `HasUpdate` is true, and calls `Dismiss()` on the ✕ button. Embedded in `MainLayout.razor` via `<UpdateBanner />`.
- `KittyClaw.Web/wwwroot/app.css` — `.update-banner`, `.update-banner-icon`, `.update-banner-text`, `.update-banner-dismiss` styles.

## Entry points
- Background poll: started by `AddHostedService(UpdateCheckService)` in `Program.cs`; runs once on startup, then every hour, but only hits GitHub when the last run is older than 24h.
- UI: the `<UpdateBanner />` in `MainLayout.razor` renders above every page when an update is available and not dismissed for that tag.
- Dev-only HTTP endpoints (registered when `app.Environment.IsDevelopment()`):
  - `POST /api/dev/update-check/simulate?version={tag}` — forces a fake latest version so the banner shows without waiting on GitHub.
  - `POST /api/dev/update-check/reset` — clears the simulated state, dismissal, and last-run timestamp.

## External dependencies
- GitHub Releases API (`api.github.com`) — unauthenticated `GET` with a `KittyClaw-UpdateCheck` user-agent. Failures are logged at Debug and never throw to the UI.
- [Storage](./storage.md) — `AppSettingsService` reads/writes `settings.json` for the dismissed-version and last-run fields.
- The `MainLayout` must remain interactive enough for the dismiss button to fire; the banner is itself `InteractiveServer` so this works even when the layout stays SSR.
