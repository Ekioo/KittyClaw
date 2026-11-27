# Account association

## Purpose and boundaries

An installation can be associated with a central KittyClaw account at
`https://my.kittyclaw.dev`. GitHub and Google identify the owner. The portal stores
the provider's stable subject, profile name and verified email when supplied.
It stores installation names, OS, version and last contact; projects, prompts,
files and run logs stay on the installation.

Association does not activate a tunnel or open a network listener. Once the owner
enables account two-factor authentication on the portal, the associated account
can authenticate a browser to explicitly enabled LAN and managed-tunnel listeners.
The same account authenticator protects both; installations never enroll a second
factor. See [local network access](./network-access.md) and
[public instance access](./public-instance-access.md) for tunnel activation.

## Local enrollment

Settings links to `/account-setup`, served by `AccountSetupMiddleware` before the
network gate. This page requires a loopback peer and loopback Host, rejects
cross-origin browser requests, and requires antiforgery validation for mutations.
The dedicated tunnel boundary runs first, so a tunneled request cannot gain local
trust.

1. Local POST `start` persists a new installation identity before calling the
   central service. It is independent of the legacy remote-access identity.
2. The service returns an HTTPS association URL valid for ten minutes. It can
   propose an account but does not grant ownership or expose the installation
   credential. The local POST immediately redirects to this URL in the same tab,
   adding a return URL for its local `/account-setup` page. There is no intermediate
   continuation link or new tab.
3. The owner signs in centrally and approves the named installation. OAuth returns
   to the central HTTPS callback, never to localhost. GitHub uses PKCE and the
   `user:email` scope; Google uses PKCE, OIDC and `openid profile email`.
4. The portal's CSRF-protected association approval redirects back to the local
   setup page. Only HTTP(S) loopback origins with the exact `/account-setup` path,
   no credentials, query or fragment are permitted. No account or installation
   credential is returned in that URL. The local page displays the proposed profile and verified email, then requires
   a separate antiforgery-protected confirmation. The installation-authenticated
   confirmation atomically consumes the unexpired proposal and enforces one active
   installation per account. Concurrent proposals cannot bypass that limit. The
   portal refuses further associations with `account_instance_limit` and explains
   how to revoke the existing installation; local confirmation displays the same
   localized guidance. Revoked installations do not occupy the account slot.

This first delivery assumes access to the local interface. Integration with a
bot's authorization popup and enrollment without a local browser are separate work.

## Runtime

`AccountAssociationService` is a singleton and hosted background service. It stores
its identity and cached account in `<dataDir>/account-association.json` protected by
the existing network Data Protection provider, purpose
`KittyClaw.AccountAssociation.v1`. Unreadable protected state fails startup.
Its fixed HTTPS client refuses redirects, uses no cookies, bounds response size,
and never sends credentials to a provider or a configurable third-party origin.

After association it checks revocation and sends a heartbeat every sixty seconds.
An instance is online when its last authenticated contact is less than three
minutes old; this is not a tunnel health check. There are no background account
requests before enrollment. An unavailable service does not sign an owner out
locally or interrupt project processing. Central revocation is detected on the
next successful check and stops heartbeats.

The heartbeat also reports the managed tunnel URL and actual connector readiness.
The portal displays Open instance only for a recently seen, ready tunnel. Older
clients do not advertise this capability. The URL is never treated as proof of
installation ownership: access is bound to the independent account installation
identity and its protected credential.

## Account-protected tunnel access

Owners enroll one authenticator from `https://my.kittyclaw.dev/security`. GitHub
and Google remain the first factor; a code or one-use recovery code verifies the
central account session. Every associated installation uses that same factor.
The authenticator secret is never distributed to installations. The Front
repository's account Worker README owns enrollment, encrypted storage and the
central HTTP contracts.

An Open instance navigation starts at the managed public origin. The local
`AccountPortalAccessService` creates a five-minute protected, host-only challenge
cookie and an S256 PKCE challenge. The portal verifies account ownership and MFA,
then redirects to the installation's registered HTTPS origin with a sixty-second,
one-use authorization code. Redemption requires the installation credential and
the cookie's verifier. A code or challenge from another browser, installation or
account cannot authenticate the recipient.

The installation issues a protected, Secure/HttpOnly host-only session cookie.
It contains a scoped opaque proof, not a central browser cookie or authenticator
key. The proof remains bound to the source account session, its MFA state and its
expiry. Checks are cached for at most fifteen seconds. Long-lived remote requests
are checked periodically and aborted on revocation or failed verification. Local
account/MFA changes invalidate the cache generation and abort active requests.

`AccountPortalAccessMiddleware` runs after the dedicated tunnel boundary and local
setup boundaries, before the ordinary network gate. Its authenticated feature is
accepted only on the opted-in HTTPS tunnel or exact LAN listener. API/MCP/OpenAPI restrictions, upload
Origin checks, local-only setup, direct-LAN rules and browser CSRF checks remain
in force. Local listener requests and forwarded headers cannot issue SSO cookies.
Signing out from an instance clears and revokes its scoped proof; signing out from
the account invalidates every proof tied to that account session.

Legacy local-authenticator cookies cannot bypass the account gate. The local
network setup page shows the shared account status and an explicit listener opt-in,
without a second QR code. Activation migrates old network state while preserving
IP rules, removes the local authenticator and rotates the access generation.
Both listeners require an associated account whose central 2FA is enabled.
First allocation can still require
the existing restart to bind the dedicated listener; no listener is exposed merely
by enabling central 2FA.

LAN sign-in creates a five-minute, credential-authenticated authorization request
on the account service. Only HTTPS private IPs, ULA IPv6 addresses and local machine
names are allowed as destinations; the service never fetches those addresses.
The callback origin, state, S256 challenge, installation and account are fixed
before browser navigation. Account login and the existing MFA verification then
produce a sixty-second grant. LAN grants and sessions have a distinct access kind
and origin and cannot be redeemed or reused as tunnel sessions. A local access
generation also binds cookies and challenges so pause/reset cannot resurrect them.

The portal can rename and revoke instances, list and revoke sessions, connect a
second provider, and delete an account. Equal email addresses never merge accounts.
Adding a second provider requires a current session and rejects identities already
owned by a different account. The account service lives in the Front repository's
`account-worker/`; its README owns server deployment, schema and API contracts.

## Verification

`AccountAssociationTests` covers encrypted persistence, local confirmation,
credential stability, rejection of foreign URLs, revocation, loopback/Host checks,
cross-origin denial and antiforgery validation with an isolated TestServer.
The Front Worker tests exercise the actual SQL on isolated SQLite and test OAuth
token validation against synthetic provider responses and signing keys. Production
provider journeys require configured OAuth clients and explicit owner sign-in.

`AccountPortalAccessTests` exercises the protected browser challenge, PKCE
redemption, cookie tampering, expiry, account/MFA revocation, live-request abortion,
activation without local authenticator enrollment, API/setup boundaries and
forwarded-header denial. The regular suite uses synthetic accounts and fake broker
responses, without allocating public tunnels or changing a user's authenticator.
