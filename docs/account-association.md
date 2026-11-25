# Account association

## Purpose and boundaries

An installation can be associated with a central KittyClaw account at
`https://my.kittyclaw.dev`. GitHub and Google identify the owner. The portal stores
the provider's stable subject, profile name and verified email when supplied.
It stores installation names, OS, version and last contact; projects, prompts,
files and run logs stay on the installation.

Association does not activate a tunnel, bypass the existing authenticator or
authenticate a browser to a local instance. Account sessions and installation
credentials belong only to this new control plane. See
[public instance access](./public-instance-access.md) for the separate tunnel system.

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
   confirmation atomically consumes the unexpired proposal.

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
