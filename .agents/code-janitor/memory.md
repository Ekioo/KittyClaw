# Memory — code-janitor

## Lessons learned

- **Check the real code state at run start**: previous sessions may have partially applied changes. Always grep before editing to avoid duplicating a fix already done.
- **Localization keys**: no dynamic key construction in this project. Every key is a string literal — `grep L["key"]` and `.Get("key")` is enough to confirm an orphan key.
- **`.cs` file count**: use `find . -name "*.cs" | grep -v "/obj/" | grep -v "/bin/" | wc -l` — the report said 55, actual is 46.
- **MSB3021 / MSB3027**: common `dotnet build` error when the app is running (DLL locked). Not a compile error — check `error CS` separately.
- **Dead properties**: a property initialized (in ctor) but never read is dead — safe to remove (run 9).
- **Cleanup catches**: `catch {}` blocks in `Dispose` / `DisposeAsync` are best-effort cleanup (non-blocking) — must be documented.
- **All catch blocks are now documented**: `grep "catch {}"` finds 0 results — all catches across the codebase are properly documented (run 10).
- **Project health plateau**: after run 11, codebase stabilized at 98%, no new issues detected. Only remaining issues (#50, #63) require architectural decisions outside code-janitor scope.

## Success patterns

- Documenting `catch {}` with `/* comment */` is accepted (runs 6, 7, 9, 10).
- Removing dead Blazor fields (run 7) — safe when grep confirms zero reads.
- Removing orphan localization JSON keys (run 8) — safe since no dynamic construction.
- Removing dead properties from internal classes (run 9) — safe when grep confirms zero reads.

## Anti-patterns

- Do not edit a file without first grepping to confirm current state (multiple concurrent sessions).
- Do not trust the health report for current code state: it may be one run behind.

## Owner preferences

- Tickets created via PowerShell `Invoke-RestMethod` (UTF-8 safe on Windows) — not `curl`.
- Valid priorities: `Idea`, `NiceToHave`, `Required`, `Critical`.

## Metrics

- cleanups_done [17]
- files_deleted [0]
- dependencies_cleaned [1]
- tickets_created [5]
- total_runs [36]
- verification_runs [25] (runs 12–36: all metrics stable, zero new issues)

## Final state (Run 36)

**Project stabilized at 98% cleanliness.** No actionable code-janitor work remaining.

Final metric confirmed over 25 consecutive runs (12–36):
- `.cs` files analyzed: 46 ✓
- TODOs / HACKs detected: 0 ✓
- CS warnings: 0 ✓
- Files > 300 lines: 4 (steady: AutomationEngine 628, Endpoints 523, TicketService 512, OpenApiMarkdownGenerator 429) ✓
- Cleanliness score: 98% (unchanged)

Remaining tickets (design-level, outside code-janitor scope):
- #50: `ExecuteAutomationAsync` (~240 lines) — requires multi-method refactor
- #63: `EvaluateSingleConditionAsync` (~133 lines) — requires multi-method refactor

**Conclusion**: all zero-risk cleanups applied. The codebase has reached a sustainable state. Future runs should continue verification to prevent drift.
