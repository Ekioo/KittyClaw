# Instance access modes

Settings has one native radio group: Local only, Local network, Internet. The same
`AccessSetupView` renders the controls in `/settings` and `/access-setup`.
`AccessSetupMiddleware` handles local-only antiforgery-protected POSTs; Blazor
circuit events cannot change access. Old GET bookmarks `/network-setup` and
`/remote-setup` redirect here. Old POST endpoints return 410 and do not mutate state.

| Mode | Localhost | Direct LAN HTTPS | Managed public tunnel |
| --- | --- | --- | --- |
| Local only | Available | Denied | Paused |
| Local network | Available | Account sign-in | Paused |
| Internet | Available | Denied | Account sign-in |

In Internet mode, devices on the local network use the same public HTTPS address.
Both remote modes use the single [account factor](./account-association.md).
Their transport boundaries are documented in [LAN access](./network-access.md)
and [public instance access](./public-instance-access.md).

`AccessModeService` serializes mode changes and derives the current preference
from the existing protected opt-in stores; no second settings file can drift out
of sync. Startup normalizes older dual opt-ins to Internet, keeping that tunnel
and disabling redundant direct LAN access. Selecting Local only works without the
account service. Remote modes require current associated-account MFA. Internet
also requires Cloudflare transit consent before first activation or resumption.
Keeping an already-active Internet mode is idempotent and needs no new consent.

Tunnel provisioning is completed before an existing LAN mode is disabled. If it
fails, LAN access and its sessions remain in place. Leaving Internet pauses its
connector and closes the origin gate; its hostname remains reserved for resume.
LAN activation can require one restart to bind HTTPS, and first tunnel activation
can require one restart for its dedicated loopback listener. Access closes
immediately when disabled even if removing a bound listener itself needs restart.

Internet addresses and DNS are automatic. The provisioning broker reserves a
chosen or generated subdomain, reconciles its Cloudflare tunnel, and creates a
proxied CNAME to `<tunnel-id>.cfargotunnel.com`. The client requests this through
its installation credential and never receives the zone-wide DNS credential.
The UI shows the public URL and status, while keeping IP rules and certificate
information in the single setup page. No new DNS resources are created by viewing
the settings or changing the radio selection before submission.

`AccessModeTests` covers exclusivity, migration, concurrency, consent, missing MFA,
failure preservation, DNS/address reuse and offline local-only recovery.
The production middleware tests also cover CSRF, aliases, invalid selections,
cross-origin requests and unchanged account authentication boundaries.
