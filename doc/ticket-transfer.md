# Lossless cross-project ticket transfer

## Purpose

Moves a ticket from one KittyClaw project to another without recreating it or losing its history. If the selected ticket has sub-tickets, the complete tree moves as one unit.

## API

```http
POST /api/projects/{sourceSlug}/tickets/{ticketId}/transfer
Content-Type: application/json

{
  "targetProject": "target-slug",
  "actor": "owner"
}
```

The response includes the preserved root ticket ID, the number of transferred tickets, both project slugs, and the transfer time.

## Fidelity guarantees

The operation preserves ticket and history identifiers, immutable timestamps, fields, status, priority, assignment, scheduling, comments, activities, labels, parent/child relationships, run metadata, token/cost metadata, and audit data. It adds one provenance activity to the transferred root ticket identifying the source project, target project, actor, and transfer time.

The source is removed only as part of the same successful operation that writes the target. A failed target write rolls both databases back, so the source remains untouched and no partial or duplicate copy is left behind.

Dependency edges whose two tickets belong to the transferred tree move in that same
transaction, retaining their direction and original creation timestamp. Dependency row
identifiers are local to each project database and are allocated in the destination.
The source edges are removed only with the successful move.

## Preflight validation

Before writing either database, KittyClaw rejects the transfer when:

- the target project does not exist or is the source project;
- a ticket, comment, or activity identifier would collide in the target;
- a required status or scheduled target column is missing;
- an assignee is not available in the target;
- labels cannot be mapped by name without ambiguity;
- the selected ticket has a parent outside the transferred tree (transfer the parent instead).
- a dependency crosses the transferred tree boundary in either direction, including a
  dependency of a child or a source ticket blocked by a transferred ticket. Transfer a
  tree containing both ends or explicitly remove the link first. Cross-project dependency
  references are not supported, so matching numeric IDs in the destination do not make
  an external source dependency transferable.

Dependencies are read and validated again inside the transfer transaction, before any
ticket/history writes, so concurrent preflight edits cannot silently split an edge.

These are deliberately strict checks: unsupported or lossy transfers fail with a clear error rather than silently changing data.

## Key components

- `KittyClaw.Core/Services/TicketTransferService.cs` — preflight, cross-database transaction, fidelity-preserving copies, source removal, and audit event.
- `KittyClaw.Web/Api/Endpoints.Tickets.cs` — transfer endpoint and validation responses.
- `KittyClaw.Core.Tests/Services/TicketTransferServiceTests.cs` — fidelity, collision, mapping, rollback, and sequential-transfer coverage.

## Related documentation

- [REST API](./rest-api.md)
- [Storage](./storage.md)
- [Kanban UI](./kanban-ui.md)
