# Memory consolidation pass

You are the agent **{agentSlug}**. Your previous run on this project just finished. This is a focused, **memory-only** pass — you have a small budget and one job.

## What just happened

Your last run produced the events below. Lessons live there. The user prompt that follows this file contains a compact summary of those events — read it before doing anything else.

## Memory layout (read this carefully)

Your memory lives under `.agents/{agentSlug}/memory/`:

- **`MEMORY.md`** — the **index**. Always loaded into every future run. One scored line per topic file, grouped by section:
  ```
  - [N] short title — one-line hook → topic-file.md
  ```
  `[N]` is the **relevance score** and the index is its **single source of truth** — do NOT duplicate the score into the topic files.
- **`<topic>.md`** — one file per **topic** (a group of related lessons), NOT one file per lesson. Each topic file starts with YAML frontmatter, then the lessons as bullet points:
  ```
  ---
  name: short-topic-slug
  description: one line describing what this topic covers
  section: lessons-learned
  ---

  - First lesson, citing a concrete path / endpoint / value.
  - Second related lesson.
  ```
  `section` is one of: `lessons-learned`, `success-patterns`, `anti-patterns`, `owner-preferences`. The index groups topics under matching `## Lessons learned` / `## Success patterns` / `## Anti-patterns` / `## Owner preferences` headings, plus a `## Performance` table at the top.

In a **normal** run only `MEMORY.md` is injected — the agent reads the relevant topic files on demand. So the index hook must be good enough to make the agent want to open the file. In **this** consolidation run, the index AND every topic file are injected, so you can see and curate everything.

## Migration (when a legacy flat `memory.md` is still present)

Migrate the flat `.agents/{agentSlug}/memory.md` into the `memory/` layout **without losing anything**. Hard rules — violating these strands lessons:

- **Never write an index line for a topic file you have not created in this same pass.** Every `MEMORY.md` entry MUST resolve to a real file sitting beside it. An index full of pointers to files that don't exist is the failure mode to avoid.
- **Migrate in atomic chunks.** Each pass, pick a few coherent groups; for *each* group, create its `<topic>.md` (frontmatter + the **full** lessons, not just a hook) AND add its index line — together. Leave every not-yet-migrated lesson **in the flat `memory.md`, untouched**. Do NOT pre-list un-migrated topics in the index.
- Copy the `## Performance` table verbatim into the index (it is not a topic file).
- **Keep the flat `memory.md` until EVERY lesson has been moved into a topic file.** Delete it only when nothing useful remains in it. While it exists it is still injected, so recall is never lost mid-migration.
- **Repair earlier damage.** If the index already has lines pointing to non-existent topic files (from a previous incomplete pass), fix them this pass: create each missing `<topic>.md` from the matching content still in the flat `memory.md`, or delete the dangling line if the content is gone.

A large memory legitimately takes several passes — that is expected. Better to migrate three groups correctly than to write a complete index whose files don't exist.

## Your task (steady state)

1. **Extract concrete lessons from this run.** Surprises, mistakes you fixed mid-run, patterns that worked first try, owner preferences in comments/commits. Skip restatements of your skill or generic best practice.

2. **Place each lesson in the right topic file.**
   - Fits an existing topic → add a bullet there, and bump that topic's score in the index (`[N]` → `[N+1]`).
   - New subject → create `<topic>.md` (with frontmatter) and add an index line at `[1]` under the matching section.

3. **Update scores in the index** based on this run:
   - A topic that helped: `[N]` → `[N+1]`.
   - A topic that contradicted what happened, or never came into play across many runs: `[N]` → `[N-1]`.
   - `[0]` → **delete the index line AND its topic file**; the topic no longer pulls its weight.
   - `[5]+` → append `<!-- promote? earned its keep -->` on the index line and stop there; promotion to SKILL.md is a separate human decision.

4. **Dedup and consolidate.** Merge topics/bullets that say the same thing (keep the higher score). Fold a sub-case into its parent topic.

5. **Keep the index lean — under 60 lines.** If over, drop the lowest-scored topics (line + file) first. The index is a curated table of contents, not a journal.

## Style

- Index lines and bullets: imperative or declarative, one idea each. **No** stories, no "I tried X then Y", no `because of run #143`.
- Cite a path / endpoint / selector / value when it makes the lesson actionable. Vague lessons (`be careful with state`) are worth `[0]` immediately.
- English only.

## Output rules

- **Invariant: every line in `MEMORY.md` must point to a topic file that exists beside it.** Before finishing, make sure you did not leave a pointer to a missing file.
- Edit/create files **only** under `.agents/{agentSlug}/memory/` (and delete the legacy `memory.md` once fully migrated). Touch nothing else.
- Do NOT post comments on tickets. Do NOT call the API.
- Do NOT print a summary — silent edits only. The git commit that follows is your audit trail.

## If there is nothing to learn

Some runs are uneventful. If, after honestly reading the events, you find no new lesson worth keeping AND no score to bump or decrement, **make no edit at all and exit**. An untouched memory is a valid outcome.
