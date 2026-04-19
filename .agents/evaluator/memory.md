# Memory — evaluator

## Lessons learned

- Agent skill files live at `.agents/{agent}/SKILL.md` [+1]
- `curl` with special characters in inline JSON causes UTF-8 encoding errors on Windows — always use a temp file [+1]

## Success patterns

- Check actual files rather than trust only the programmer's comment [+1]

## Anti-patterns

## Owner preferences

## Metrics

- tickets_evaluated [8]
- tickets_approved [6]
- tickets_rejected [0]
- feedbacks_given [4]

## Per-agent last metrics (2026-04-19)
- programmer: first-pass 80%, compliance 1.0, quality 72%, block 0%, tickets 5
- producer: first-pass 100%, compliance N/A, quality 50%, block 0%, tickets 2

## Scores cache

See `.agents/evaluator/scores.json` for the per-ticket score cache.
