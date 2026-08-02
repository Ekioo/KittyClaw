# Boundary observation mode

KittyClaw observes normalized `tool_use` events without blocking provider execution. The detector
records only a SHA-256 hash of arguments plus a short classified resource label; raw arguments are
not persisted. It covers repository push and pull-request mutations, publication and deployment,
new explicit HTTP destinations, secret-file access, and destructive local commands.
HTTP destinations are persisted per project: localhost, loopback, and private-network hosts are
part of the local baseline, the first use of an external host is observed, and subsequent uses of
that same host are treated as known rather than repeatedly counted as potential requests.

Use `GET /api/projects/{slug}/boundary-observations/metrics?runWindow=20` to inspect the latest
ordinary-run window. A reviewer can label an observation through
`PUT /api/projects/{slug}/boundary-observations/{id}/review`; the metrics report both potential
requests per twenty runs and the reviewed false-positive rate.

## Provider blind spots

- Claude Code: structured `tool_use` input is available, but effects performed inside an opaque
  custom tool cannot be classified unless its command, URL, or path is present in the input.
  Structured `tool_result` events correlate success or failure by tool-use identifier.
- Codex: command executions, file changes, MCP calls, and web searches are normalized. Completed
  items correlate their reported success, failure, or cancellation by item identifier.
- Grok Build: streaming tool events are accepted through tolerant field aliases. Grok's JSONL
  schema is not publicly stable, so unknown future event shapes pass through without blocking and
  may not be observed. Known tool-result aliases correlate outcomes when an identifier is present.
- Other/future providers: any backend producing the normalized `tool_use` event is covered. Plain
  assistant text, subprocesses hidden behind provider internals, shell aliases, encoded commands,
  indirect scripts, and network access without an explicit HTTP URL remain blind spots. When a
  provider omits tool-result identifiers, the most recent pending observation is used; a terminal
  run result resolves any remaining observations, while ambiguous termination remains `unknown`.
