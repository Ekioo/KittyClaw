# Memory — committer

## Lessons learned

- To post a comment with `curl`, use `printf '...' | curl --data-binary @-` to avoid UTF-8 encoding issues on Windows with apostrophes/accents in inline JSON.

## Success patterns

- Check `git ls-files <dir>` to confirm a folder is actually tracked before concluding there is nothing to commit. [1]
- Use `printf '...' | curl --data-binary @-` for API comments to avoid encoding errors. [1]
- Detect incomplete integration: if a method is added but never called (grep to verify), that is a blocker. Early signal = early fix. [1]

## Anti-patterns

- Do not trust `git status <dir>` alone to detect whether a directory has committable work — it may be ignored. [1]
- Mixed work (CommitAgentMemory + ExecutePowerShell in the same commit) blocks the committer because it is impossible to isolate for a single ticket. [1]

## Owner preferences

## Metrics

- commits_created [1]
- commits_rejected [0]
- tickets_passed_done [4]
- conflicts_resolved [0]
