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

- cleanups_done [18]
- files_deleted [0]
- dependencies_cleaned [1]
- tickets_created [5]
- total_runs [75]
- verification_runs [64] (runs 12–37: 46 files; 38: 47; 39: 46; 40: 52; 41–42: 54; 43–49: 55 stable; 50–71: 55 stable; 72: 56 new; 73–75: 56 stable)
- note: Expansion phase continued. 9 new files added (46→55) across runs 40–43. 1 new file added in Run 72 (55→56). All clean: 0 TODOs, 0 catches, 0 warnings. Run 47: documented 4 undocumented catches in ClaudeRunner.cs. Runs 48–75: verification passes, all stable.

## Final state (Run 75)

**Project maintains 98% cleanliness despite growth.** Run 75: file count stable at 56, all clean.

Final metric confirmed over 64 verification runs (12–75):
- `.cs` files analyzed: 56 ✓ (10 new files added since run 39; all clean)
- TODOs / HACKs detected: 0 ✓
- CS warnings: 0 ✓
- Undocumented catch blocks: 0 ✓ (4 fixed in Run 47)
- Files > 300 lines: 4 (steady: AutomationEngine 628, Endpoints 523, TicketService 512, OpenApiMarkdownGenerator 429) ✓
- Cleanliness score: 98% (unchanged)

Remaining tickets (design-level, outside code-janitor scope):
- #50: `ExecuteAutomationAsync` (~240 lines) — requires multi-method refactor
- #63: `EvaluateSingleConditionAsync` (~133 lines) — requires multi-method refactor

**Conclusion**: all zero-risk cleanups applied. The codebase has reached a sustainable state. Future runs should continue verification to prevent drift.
