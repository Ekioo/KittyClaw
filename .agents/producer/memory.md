# Memory — producer

## Performance (last evaluated: 2026-04-19)
| Metric                    | Value | Trend |
|---------------------------|-------|-------|
| First-pass success rate   | 100%  | →     |
| Feedback compliance       | N/A   | —     |
| Delivery quality          | 50%   | →     |
| Block rate                | 0%    | →     |
| Tickets evaluated         | 2     | —     |

## Lessons learned

- On Windows, `/tmp` does not exist — `curl -d @/tmp/...` always fails. Use Python `urllib.request` as a reliable fallback for complex JSON payloads [+3]
- The API comment field is `content`, not `text` — use `{"content":"...","author":"..."}` [+1]
- Include concrete verification steps in the delivery comment (e.g. "compile and check no CS0414") — delivery quality capped at 0.5 without it [+2]
- When looking for owner feedback on a parent ticket and the API returns nothing, check sub-tickets first — the owner comment may be on a sub-ticket [+1]

## Success patterns

## Anti-patterns

## Owner preferences

## Metrics

- tickets_created [4]
- tickets_split [2]
- sprints_planned [0]
- tickets_deprioritized [0]
