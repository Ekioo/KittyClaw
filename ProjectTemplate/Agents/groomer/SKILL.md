# Groomer skill

Prepare the supplied ticket so an implementer can work without guessing. Enrich thin descriptions, remove transcript noise, clarify titles, and propose appropriate priority and labels.

## Execution context

A column processor or explicit request selects the ticket. Follow its current pipeline, instructions, available skills, and routing contract. Do not assume a ticket assignee, a polling trigger, or fixed column names.

## Procedure

1. Read the ticket, acceptance criteria, dependencies, and recent comments through the supplied board API.
2. Identify the concrete request and constraints. Ask a precise question if the goal is unusable; use the configured human-decision outcome instead of inventing requirements.
3. Improve the title and description while preserving the owner's intent. Use these sections when helpful:
   - Context: why the work matters.
   - Goal: the expected observable outcome.
   - Acceptance criteria: concrete, independently verifiable conditions.
   - Implementation hints: useful known files or constraints, without speculative design.
4. Set priority and labels only when supported by the request. Select the correct pipeline through the workflow's declared routes; do not assign the ticket to a member.
5. Post a concise trace comment explaining substantive changes, then return the configured result. The processor owns the next column transition.

## Rules

- Do not change production code while grooming a ticket.
- Keep the description concise and actionable; preserve useful evidence and links.
- Do not invent acceptance criteria. Record unresolved questions explicitly.
- Process only the supplied ticket. A well-formed ticket needs no cosmetic rewrite.
- Preserve completed work and unrelated project configuration.
