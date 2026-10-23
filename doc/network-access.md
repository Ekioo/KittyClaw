# Local network access

KittyClaw remains loopback-only by default. Global settings links to
`/network-setup`, a server-rendered setup page accessible only through a loopback
connection with a loopback Host header. Association and reset require antiforgery
validation. Scan the locally generated QR code or add the displayed setup key to a
TOTP authenticator (SHA-1, six digits,
30 seconds), then confirm a code. No password or external identity service is used;
this is authenticator-only authentication, not two independent factors.

## Listener and host validation

After association, restart KittyClaw. The original listener remains available for
local UI, agent APIs and MCP. An additional HTTPS listener binds all interfaces on
port 5233 (`NetworkAccess:HttpsPort` overrides it; use a distinct port for isolated
instances). The setup page lists current interface URLs. Only explicit loopback
hosts, the machine name and interface IP addresses pass host filtering; the default
`appsettings.json` allowlist stays restricted. Restart after an interface/address
change to update this list.

The HTTPS certificate is self-signed, generated on this PC and reused across
restarts. Verify its SHA-256 fingerprint on the setup page before trusting it on
another device. Allow the HTTPS port through the OS firewall for the private
network if needed; KittyClaw does not modify firewall rules. This feature supports
direct LAN connections. The dedicated origin for managed public access is documented in [public instance access](./public-instance-access.md). Do not proxy
remote requests through localhost: loopback access is intentionally trusted for
local automation.

## Request protection

`NetworkAccessMiddleware` runs before application endpoints and uploaded files.
All non-loopback requests require enabled access, HTTPS and a valid session,
including REST APIs, MCP and Blazor/SignalR handshakes. Only the login form is
anonymous. APIs receive 401 rather than HTML redirects. Cross-origin browser
requests are rejected. Remote HTTP requests are rejected, never used for login.

Login uses RFC 6238 TOTP, accepting a one-step clock drift. Accepted time steps are
persisted to prevent reuse across restarts. Five failed attempts cause a global
one-minute cooldown. Authenticated sessions expire after 12 hours and use a
protected `__Host-` cookie with Secure, HttpOnly and SameSite=Strict. There are no
anonymous exceptions for agent APIs or MCP; local integrations continue to use
localhost.

Disabling access from localhost immediately revokes sessions and closes active
remote transports, including Blazor WebSockets. It also removes the authenticator
association. Re-enroll locally if the phone is lost. Restart to remove the HTTPS
listener itself. The local instance is never restarted automatically during setup.

## Storage and verification

`NetworkAccessService` stores protected state in `<dataDir>/network-access.json`,
the protected certificate in `network-access.json.certificate`, and Data Protection
keys under `network-keys/`. Web antiforgery and Blazor protection keys are isolated
under `web-keys/` in the same data directory. Windows protects the keys with current-user DPAPI;
other platforms rely on data-directory permissions. Preserve these files together
when moving the installation. Unreadable protected state fails startup rather than
silently dropping authentication.

`NetworkAccessTests` verifies official RFC vectors, enrollment, replay protection,
throttling, persistence, expiry, reset, secure cookies, CSRF, origin validation and
middleware protection of APIs, uploads and MCP using an isolated TestServer. A
full-host test also verifies saved configuration, real settings rendering and
host filtering with all hosted services removed. Neither test dispatches agents.
