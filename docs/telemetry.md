# Telemetry

## Purpose
Answers "is KittyClaw actually used?" with one anonymous heartbeat event per instance per 24h, sent to Umami Cloud. Deliberately minimal: it counts alive instances and their versions, nothing more. The README "Telemetry" section is the public disclosure of what is sent.

## Key components
- `KittyClaw.Web/Services/TelemetryService.cs` — hosted `BackgroundService` that wakes hourly and POSTs an Umami `heartbeat` event when the last send is older than 24h. Endpoint and website id are hardcoded constants. Sends a browser-like `User-Agent` (matched to the real OS family) because Umami discards events from bot-looking UAs. 3s timeout; failures log at Debug, never throw, and are retried on the next hourly tick (last-sent is only persisted on HTTP success).
- `KittyClaw.Core/Services/AppSettingsService.cs` — persists `TelemetryInstanceId` (random GUID generated on first read) and `TelemetryLastSent` (24h throttle) in `%APPDATA%/KittyClaw/settings.json`. There is deliberately no opt-out flag (product decision): the heartbeat is anonymous and always on outside Development.

## Payload
`POST https://cloud.umami.is/api/send` with an `event` named `heartbeat` on the synthetic hostname `app.kittyclaw.local`, carrying event data: `instanceId`, `version` (from `AssemblyInformationalVersion` via `VersionFormatter`), `os` (`Windows`/`macOS`/`Linux`/`Other`). The payload's top-level `id` field is set to the instance id (Umami "distinct id"), so each instance is a stable identified visitor — exact unique-instance counts, independent of Umami's default IP+UA session hashing. Country comes for free: Umami geolocates the sending server's IP.

## Entry points
- Registered in `Program.cs` via `AddHostedService<TelemetryService>()` **only when the environment is not Development** — this excludes `dotnet watch` sessions and QaRunner test instances (which launch with `ASPNETCORE_ENVIRONMENT=Development`).

## External dependencies
- Umami Cloud (`cloud.umami.is`) — unauthenticated collect endpoint; the website id is public by design (same model as any web-analytics site id). Instance uniqueness comes from the distinct `id` field, not Umami's IP+UA session hashing.
- [Storage](./storage.md) — `AppSettingsService` owns the three persisted fields.
- [Update check](./update-check.md) — same background-service pattern (hourly wake, 24h persisted throttle, silent failures); kept as a separate service so telemetry can be opted out independently.
