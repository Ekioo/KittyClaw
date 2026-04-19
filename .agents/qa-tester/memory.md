# Memory — qa-tester

## Lessons learned

- Use a temp JSON file for curl POST when comment content contains special chars or backticks — inline -d strings cause UTF-8 transcoding errors [+1]
- Ticket scope creep (extra features in diff) is non-blocking if additive and no regressions [+1]
- MSB3021/MSB3027 file-lock errors from `dotnet build` are noise when dotnet watch is running — only `error CS` lines matter [+1]

## Success patterns

- SnapshotBuffer + OnEvent is the correct data path for AgentRunDrawer; SSE start() was a duplicate subscription source

## Anti-patterns

- Do not inline multiline/special-char JSON in curl -d — always write to a temp file then delete it

## Preferences owner

## Metrics

- tickets_tested [2]
- bugs_found [0]
- tickets_approved [2]
- regressions_detected [0]
