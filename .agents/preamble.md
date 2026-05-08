## Memory

Your memory (`.agents/{agent}/memory.md`) has been injected into this conversation automatically.
Apply the lessons it contains throughout this run.

At the end of every run, update the memory file: add new lessons with [+1], adjust counters [N], remove entries at [0], promote to the skill file any lesson that reaches [5+].

**If your memory file exceeds 100 lines, consolidate it**: merge redundant lessons, drop entries at [0], summarize verbose blocks. The goal is a dense, actionable memory — not an exhaustive journal.

## Language

All content you produce — commit messages, memory updates, agent-to-agent notes — MUST be written in **English**. This includes any text in `.agents/**` and git commit messages.

## Git commits — no attribution trailers

Never add `Co-Authored-By`, `Generated-By`, or any AI-attribution trailer to commit messages. Clean commits only.

## KittyClaw API

The full and up-to-date API documentation is available at:
${KITTYCLAW_API_URL:-http://localhost:5230}/api/docs

Consult it before interacting with the API.

**Always reference the API as `${KITTYCLAW_API_URL:-http://localhost:5230}`** in your bash invocations — never hardcode `http://localhost:5230`. The orchestrator injects `KITTYCLAW_API_URL` to point at the *current* host instance, which may not be on the default port (e.g. when running inside an isolated test instance spawned by a QA tool).

For convenience, define a local at the start of any block that does several calls:

```bash
api="${KITTYCLAW_API_URL:-http://localhost:5230}"
curl -s "$api/api/projects/{project-slug}/tickets/{id}"
```

## Cross-platform paths

Never use `/tmp` or other Linux-only filesystem paths — they do not exist on Windows. If you need a scratch file (patch, JSON body, …), write it in the current workspace (e.g. `body.json`, `full.patch`) and delete it once you are done.

## Project slug

Your API calls need the project slug. It is the name of the folder that hosts `.agents/` — your working directory. Use it in every `/api/projects/{project-slug}/...` endpoint.

## Build verification

The host project may run its build tool (dotnet watch, vite, cargo watch, etc.) in the background, keeping build artifacts locked. If you run a build manually and see file-lock errors, these are NOT compile errors — ignore them.

To check whether the code currently compiles:

1. **Trust hot reload**: if your edit applied without an error report from the watcher, it compiled.
2. **Look at the watcher log**: the running watcher's stdout is authoritative. Look for success markers or lines containing real compile errors.
3. **Run the build yourself** and treat file-lock / copy errors as non-blocking noise.

Do NOT kill the running watcher process to work around this — it is usually the live server serving the app.
