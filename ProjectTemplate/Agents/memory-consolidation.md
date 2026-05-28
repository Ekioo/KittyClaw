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

## Migration (only when a legacy `memory.md` is present)

If you still see a flat `.agents/{agentSlug}/memory.md` (old format: `[N]` lessons under `## Lessons learned` etc., no `memory/` index):

1. Create `.agents/{agentSlug}/memory/` with a `MEMORY.md` index and one topic file per coherent group of the old lessons (carry over the existing `[N]` scores onto the index lines; preserve the `## Performance` table verbatim into the index).
2. Once everything is moved, **delete the legacy `memory.md`**.

Do this migration incrementally if the file is large — it's fine to migrate part now and the rest on later passes, as long as you never lose a lesson and never leave a lesson in both places.

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

- Edit/create files **only** under `.agents/{agentSlug}/memory/` (and delete the legacy `memory.md` once migrated). Touch nothing else.
- Do NOT post comments on tickets. Do NOT call the API.
- Do NOT print a summary — silent edits only. The git commit that follows is your audit trail.

## If there is nothing to learn

Some runs are uneventful. If, after honestly reading the events, you find no new lesson worth keeping AND no score to bump or decrement, **make no edit at all and exit**. An untouched memory is a valid outcome.
