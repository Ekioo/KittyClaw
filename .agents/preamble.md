## Memory

Your memory (`.agents/{agent}/memory.md`) has been injected into this conversation automatically.
Apply the lessons it contains throughout this run.

At the end of every run, update the memory file: add new lessons with [+1], adjust counters [N], remove entries at [0], promote to the skill file any lesson that reaches [5+].

**If your memory file exceeds 100 lines, consolidate it**: merge redundant lessons, drop entries at [0], summarize verbose blocks. The goal is a dense, actionable memory — not an exhaustive journal.

## Language

All content you produce — commit messages, memory updates, agent-to-agent notes — MUST be written in **English**. This includes any text in `.agents/**` and git commit messages.

## Build verification

The Todo.Web server runs via `dotnet watch --non-interactive` in the background, which keeps `Todo.Core.dll` and `Todo.Web.dll` locked. If you run `dotnet build` yourself you may see MSB3027 / MSB3021 **file-lock** errors — these are NOT compile errors; ignore them.

To check whether the code currently compiles:

1. **Trust hot reload**: if your edit applied without an error report from `dotnet watch`, it compiled.
2. **Look at the watch log**: the `dotnet watch` stdout is authoritative. Look for `La génération a réussi` (success) or lines containing `error CS` (real compile errors).
3. **Run `dotnet build Todo.slnx` yourself** and treat MSB3027 / MSB3021 / MSB3492 as non-blocking noise. Only `error CS####` matters.

Do NOT kill the running `dotnet watch` process to work around this — it is the live server serving `http://localhost:5230`.
