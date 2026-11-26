# Local network access

KittyClaw remains loopback-only by default. Associate the installation with a
KittyClaw account and enable its two-factor authentication on my.kittyclaw.dev.
This is the only authenticator: both LAN and managed-tunnel sessions use the
same GitHub/Google identity and account MFA. The common session protocol is
documented in [account association](./account-association.md).

## Owner opt-in

The [access mode selector](./access-modes.md) in global settings and `/access-setup`
replaces `/network-setup`. `AccessSetupMiddleware` handles its local document before
the network gate. It requires a loopback peer and
loopback Host and rejects cross-origin requests. Mutations require antiforgery
validation. The page refreshes the linked account, shows whether its factor is
enabled, and links to central security and account association. It never creates
an enrollment key, QR code or local code-entry form.

Enabling network access is a separate explicit opt-in; associating the account or
enabling MFA alone opens no listener. Enable requires current confirmation that
the associated account has MFA. The protected state records account-backed access,
removes a previous local authenticator and rotates the access generation while
preserving IP rules. Legacy local-authenticator state is readable for migration,
but legacy login cookies and POSTs cannot bypass the production account gate.

Restart KittyClaw once after opt-in. The original loopback listener remains trusted
for local UI, agent APIs and MCP. The additional HTTPS listener binds interfaces on
port 5233 (`NetworkAccess:HttpsPort` overrides it). Setup lists interface URLs and
the self-signed certificate's SHA-256 fingerprint. Verify the fingerprint before
trusting the certificate on another device. Firewall rules remain the owner's
choice. Restart after interface changes to update the concrete Host allowlist.
Managed public access uses its own loopback origin, described in
[public instance access](./public-instance-access.md).

## Authentication and boundaries

`AccountPortalAccessMiddleware` requires an opted-in account-backed LAN listener,
HTTPS, the actual configured local port, and an exact machine/interface Host.
Forwarding headers do not grant trust. Private IPv4, ULA IPv6 and local machine
names are supported for browser return addresses. Public IPs should use the
managed tunnel. The account service never fetches a LAN URL.

The browser goes to my.kittyclaw.dev for account sign-in and its existing MFA,
then returns to the same local HTTPS origin with a one-use, S256 PKCE-bound code.
Only the installation's credential and the protected browser verifier can redeem
it. Host-only Secure/HttpOnly/SameSite=Lax cookies bind the resulting session to
the account, installation, LAN origin, access kind, local generation and central
session expiry. LAN and tunnel sessions cannot substitute for one another.

Direct remote requests, including REST APIs, MCP, uploads and Blazor, require this
session. Anonymous API clients receive 401 rather than an HTML login page. Existing
same-origin/fetch-metadata checks remain in force. Local setup endpoints are denied
to remote users even after login. The managed tunnel retains its narrower API and
setup restrictions. Localhost remains available for configuration and recovery.

## Network rules and revocation

The setup document preserves the optional IP/CIDR allowlist (up to 256 entries).
An enabled empty list denies all direct remote peers. Validation rejects an entire
invalid update. Rules use the real socket peer, normalize IPv4-mapped IPv6, and
apply before authentication and code redemption; the managed tunnel is excluded.
Changing rules aborts active sessions whose peers cease to be allowed.

Account checks are cached for at most 15 seconds. Long-running requests revalidate
centrally every 15 seconds without reusing positive cache entries; cached local
revocations remain denied even if central sign-out fails. Sign-out revokes the scoped proof. Disabling the central factor,
revoking association, or signing out the source portal session invalidates access.
Local account changes abort requests immediately after refresh. Disabling LAN
access revokes active transports and rotates the local generation. Re-enabling
cannot resurrect an old cookie. The separate tunnel opt-in is preserved.

## Storage and verification

`network-access.json` holds protected listener opt-in, IP rules and session
generation. Its protected certificate is `network-access.json.certificate`; Data
Protection keys are in `network-keys/`. Web antiforgery keys are in `web-keys/`.
Windows uses current-user DPAPI. Preserve identity and keys together; unreadable
state fails startup rather than silently weakening authentication. Authenticator
secrets stay in the central service; they are not copied to installations.

`AccountPortalAccessTests` covers the full production middleware order, account
setup without local enrollment, CSRF opt-in, LAN SSO, kind/origin separation,
legacy-cookie rejection, IP rules, expiry and live stream revocation.
`NetworkAccessTests` also covers state/certificate persistence and the isolated
full-host listener configuration. Legacy cryptographic tests remain for migration
compatibility, not as an exposed production sign-in flow. Tests never dispatch
real providers or allocate public tunnels.
