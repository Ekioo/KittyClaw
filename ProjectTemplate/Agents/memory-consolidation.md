# Memory consolidation pass

You are the agent **{agentSlug}**. Your previous run on this project just finished. This is a focused, **memory-only** pass — you have a small budget and one job.

## What just happened

Your last run on this project produced the events below. Lessons live here. The user prompt that follows this file contains a compact summary of those events — read it before doing anything else.

## Your task

Open `.agents/{agentSlug}/memory.md`, then:

1. **Extract concrete lessons from this run.** Look for:
   - Surprises — something behaved differently than your memory expected.
   - Mistakes you fixed mid-run (a wrong selector, a wrong endpoint shape, a misread spec).
   - Patterns that worked the first time and are worth remembering.
   - Owner preferences expressed in comments or commit messages.

   Skip anything that's just a restatement of your skill or generic best practice.

2. **Update the relevance counters on existing lines.**
   - A lesson that helped this run: `[N]` → `[N+1]`.
   - A lesson that contradicted what just happened, or never came into play across many runs: `[N]` → `[N-1]`.
   - `[0]` → **delete the line**, the lesson is no longer pulling its weight.
   - `[5]+` → leave a comment like `# promote? this lesson has earned its keep` and stop there; promotion to SKILL.md is a separate human decision.

3. **Add new lessons from this run** at `[+1]`, in the section that fits (`Lessons learned`, `Success patterns`, `Anti-patterns`, `Owner preferences`, `Known gotchas`, etc.). One line per lesson, imperative or declarative — never narrative.

4. **Dedup and consolidate.** If two lines say the same thing, merge them and keep the higher counter. If a line is a sub-case of another, fold it in.

5. **Stay under 100 lines total.** If you're over, drop the lowest-counter entries first. Memory is a curated index, not a journal.

## Style

- Each lesson begins with `[N]`.
- Imperative or declarative. **No** stories, no "I tried X then Y", no `because of run #143`.
- Cite a path / endpoint / selector / value when it makes the lesson actionable. Vague lessons (`be careful with state`) are worth `[0]` immediately.
- English only.

## Output rules

- **Do exactly one `Edit` on `.agents/{agentSlug}/memory.md`** and exit.
- Do NOT post comments on tickets. Do NOT call the API. Do NOT touch any other file.
- Do NOT print a summary of what you did — silent edit only. The git commit that follows is your audit trail.

## If there is nothing to learn

Some runs are uneventful. If, after honestly reading the events, you find no new lesson worth `[+1]` AND no existing counter to bump or decrement, **make no edit at all and exit**. An untouched memory file is a valid outcome.
