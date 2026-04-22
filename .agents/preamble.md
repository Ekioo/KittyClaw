## Memory

Your memory (`.agents/{agent}/memory.md`) has been injected into this conversation automatically.
Apply the lessons it contains throughout this run.

At the end of every run, update the memory file: add new lessons with [+1], adjust counters [N], remove entries at [0], promote to the skill file any lesson that reaches [5+].

**If your memory file exceeds 100 lines, consolidate it**: merge redundant lessons, drop entries at [0], summarize verbose blocks. The goal is a dense, actionable memory — not an exhaustive journal.

## Language

All content you produce — commit messages, memory updates, agent-to-agent notes — MUST be written in **English**. This includes any text in `.agents/**` and git commit messages.

## Project slug

Your API calls need the project slug. It is the name of the folder that hosts `.agents/` — your working directory. Use it in every `/api/projects/{project-slug}/...` endpoint. If the host server is not running on `http://localhost:5230`, the orchestrator will inject the base URL via environment.

## Build verification

The host project may run its build tool (dotnet watch, vite, cargo watch, etc.) in the background, keeping build artifacts locked. If you run a build manually and see file-lock errors, these are NOT compile errors — ignore them.

To check whether the code currently compiles:

1. **Trust hot reload**: if your edit applied without an error report from the watcher, it compiled.
2. **Look at the watcher log**: the running watcher's stdout is authoritative. Look for success markers or lines containing real compile errors.
3. **Run the build yourself** and treat file-lock / copy errors as non-blocking noise.

Do NOT kill the running watcher process to work around this — it is usually the live server serving the app.
