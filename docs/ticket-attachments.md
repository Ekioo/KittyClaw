# Ticket attachments

The REST API can attach any nonempty file up to 100 MiB to an existing ticket.
Files are stored as opaque downloads, including HTML and SVG; they are never
served inline or from the public static uploads directory.

## API

`POST /api/projects/{slug}/tickets/{id}/attachments` accepts
`multipart/form-data` with exactly one file field named `file` and a required
text field `author` (up to 120 characters). For example, from an authorized agent:

```sh
curl --fail-with-body \
  -H "Authorization: Bearer $KITTYCLAW_TOKEN" \
  -H "Idempotency-Key: unique-upload-operation-id" \
  -F "author=my-agent" \
  -F "file=@./report.pdf" \
  "https://ekioo.kittyclaw.dev/api/projects/my-project/tickets/42/attachments"
```

Keep tokens in private client state, never URLs or chat. Do not follow redirects
when uploading files or sending bearer tokens. Local trusted API clients can omit
the Authorization header and use their loopback instance address.

Success returns HTTP 201 with a Location header and
`{ id, ticketId, fileName, size, author, createdAt, url, sha256 }`. Size is the actual byte
count. A generated Markdown comment adds the download link to the ticket
discussion, and connected boards are notified. This informational attachment
comment does not emit the user-comment workflow trigger.

Attachments also appear directly below the ticket description, one clickable
filename/size row per file, without previews, on desktop and mobile. On the local
instance, right-click opens a menu with **Open file location**, which reveals the
stored file in the host operating system's file manager. The reveal endpoint is
local-only; remote browsers and API grants cannot launch host applications.

For safe retries, send the same `Idempotency-Key` header (1-128 ASCII letters,
digits, dots, underscores, colons or hyphens) with the same normalized filename,
author and bytes. A replay returns HTTP 200, the same attachment ID and
`Idempotency-Replayed: true`; it does not add a comment or duplicate bytes. The
key is scoped to the ticket and stored only as a hash. Different content or
metadata with the same key returns 409. After deletion, replay returns 410 and
cannot restore the removed file. Keys move with the ticket on project transfer.
Without the header each POST creates a new attachment, for backward compatibility.

- `GET /api/projects/{slug}/tickets/{id}/attachments` lists attachment metadata
  with download URLs, or returns 404 if the project/ticket does not exist.
- `GET /api/projects/{slug}/tickets/{id}/attachments/{attachmentId}` downloads
  the original bytes, with `Content-Disposition: attachment`,
  `application/octet-stream`, `nosniff`, sandbox CSP and `Cache-Control: no-store`.
- `DELETE /api/projects/{slug}/tickets/{id}/attachments/{attachmentId}` accepts
  JSON `{ "author": "my-agent" }` and returns 204. Repeated deletion also returns
  204. It hides the attachment immediately, removes its generated discussion link,
  records an audit event and unlinks its bytes. If a file is locked, cleanup retries
  deletion later. A small metadata tombstone preserves idempotency until ticket deletion.
- Empty files, invalid names/authors, missing/multiple files and malformed forms
  return 400. Oversized files or request bodies return 413. Unknown tickets,
  projects or attachment IDs return 404.

## Access

[Agent API grants](./agent-api-access.md) require `tickets:write` on the selected
project for upload/deletion and `project:read` for listing/download. Grants for another
project cannot access the file. Authenticated tunnel browser sessions may download
files from their ticket discussion; anonymous requests still require sign-in.
Browser upload over the managed tunnel is not enabled by this API addition.

## Storage and lifecycle

`TicketAttachmentService` creates the per-project `TicketAttachments` table with
an inline SQLite migration, compatible with existing project databases. It writes
bytes under `<dataDir>/attachments/<random-id>` outside all static roots. Client
filenames are metadata only; path prefixes are discarded, invalid/control
characters are rejected and filenames are escaped in generated Markdown links.
The stored metadata and discussion comment commit together after the bounded file
write. Rejected/interrupted uploads remove their partial blob. A filesystem lease
serializes upload finalization, deletion, transfer and cleanup, including across
processes sharing the data directory. An uncertain database commit retains bytes
for later reconciliation instead of risking deletion of a committed file.

Transfers move attachment metadata in the same cross-database transaction as the
ticket and rewrite discussion download links to the destination project. Blob IDs
and bytes remain stable. Ticket deletion removes metadata through the database
foreign key. `TicketAttachmentCleanupService` scans 30 seconds after startup and
hourly thereafter. Unreferenced blobs and crash-leftover `.part` files are removed
after a 24-hour safety window. Explicitly deleted attachments can be retried
immediately. The scan reads every registered project's database before deletion;
any missing/unreadable database aborts the sweep. Referenced live files, recent
uploads, reparse points and unrelated files are preserved. No live credentials or
payload contents are logged.

## Real transport acceptance

`TicketAttachmentCloudflareLiveTests` is opt-in with
`KITTYCLAW_ATTACHMENT_CLOUDFLARE_LIVE=1` and `KITTYCLAW_CLOUDFLARED_BIN` pointing to
the pinned Windows executable. It creates a temporary Cloudflare Quick Tunnel
and isolated loopback Kestrel host, uploads 99,000,000 synthetic random bytes,
replays the operation, checks the downloaded SHA-256, denies anonymous access
and deletes the file. The production attachment routes, origin boundary and agent
capability checks are used, with a synthetic portal approval response. This tests
real Cloudflare transport, not production portal sign-in or the user's managed
hostname. No user projects or account associations are modified. The connector
is stopped in `finally`; a passing result is saved under ignored
`.artifacts/attachment-cloudflare-live/<run>/result.json` without credentials.

`TicketAttachmentHttpTests` exercises real multipart routes and downloads using an
isolated TestServer and no hosted agent engines. Transfer and account access tests
cover preservation and the agent permission boundary.

On 2026-11-28 the live fixture passed with 99,000,000 random bytes: upload HTTP 201
in 20.24 seconds, retry HTTP 200 with the same ID, matching SHA-256 on download,
anonymous access denied, and DELETE HTTP 204 with bytes removed. The temporary
connector was stopped. This acceptance used an isolated Quick Tunnel and synthetic
authorization, not the production account or `ekioo.kittyclaw.dev`.
