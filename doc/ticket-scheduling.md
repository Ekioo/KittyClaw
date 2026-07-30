# Ticket scheduling

## Purpose

Ticket scheduling parks work until a future time, then promotes it to a chosen column. It is useful for dated releases, reminders, gates, and other work that is not currently blocked.

Promotion raises the same ticket-status signal as a manual move, so existing status-based automations can react normally.

## Behavior

- New and existing boards have a **Scheduled** column between **Blocked** and **Review** by default. Columns remain user-reorderable.
- In the ticket panel, select **Schedule…**, then choose a local date/time and a target column.
- KittyClaw stores the instant in UTC and checks due tickets every 30 seconds.
- Due tickets in active projects move to their target column. The default target is **Todo**.
- Paused projects are skipped until they are resumed.
- Moving a scheduled ticket to another column manually clears its fire time and target.
- Cards in **Scheduled** show a `J`/`J-N` day badge and sort by fire time before manual order.

## API

Schedule a ticket with:

```http
PATCH /api/projects/{slug}/tickets/{id}/schedule
Content-Type: application/json

{
  "fireAt": "2026-08-01T09:00:00Z",
  "targetStatus": "Review",
  "author": "owner"
}
```

`fireAt` and `author` are required. `targetStatus` defaults to `Todo`, a blank target becomes `Todo`, and any other value must name an existing column.

To cancel a schedule, move the ticket out of **Scheduled** through the regular status update. This clears both scheduling fields.

## Key components

- `KittyClaw.Core/Models/Ticket.cs` — persists `FireAt` and `ScheduleTarget`.
- `KittyClaw.Core/Services/TicketService.cs` — schedules, lists due tickets, promotes them, and clears stale schedule data on manual moves.
- `KittyClaw.Core/Services/ColumnService.cs` — seeds and backfills the **Scheduled** column.
- `KittyClaw.Core/Services/ScheduledPromotionService.cs` — polls active projects and promotes due tickets.
- `KittyClaw.Web/Components/TicketPanel.razor` — schedule editor in the ticket panel.
- `KittyClaw.Web/Components/Pages/Board.razor` — countdown badge and scheduled-column ordering.

## Related features

- [Kanban UI](./kanban-ui.md) — ticket editing and card rendering.
- [Storage](./storage.md) — per-project ticket persistence.
- [Automation engine](./automation-engine.md) — reacts to the status signal raised after promotion.
