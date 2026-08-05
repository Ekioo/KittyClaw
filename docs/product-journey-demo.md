# The five-minute KittyClaw product journey

KittyClaw is a local control plane for software work performed by AI agents.

This guided demonstration tells one story through exactly three product proofs: a live board, a readable run, and human validation before external release. It avoids orchestration terminology so a prospect can repeat the journey without learning the implementation model first.

## Before the clock starts

Use a disposable checkout of a small web application. Start KittyClaw, create a project named `Checkout demo`, choose that checkout as its workspace, and initialize the project. Confirm that at least one supported agent backend is available.

Use this realistic ticket:

> **Prevent duplicate checkout submissions**
>
> Disable the Place order button while checkout is being submitted. Restore it if the request fails, keep it disabled after success, and add regression coverage for double-clicking and failed requests.

For a deterministic rehearsal or screenshot refresh, run the isolated scenario described in [Reproduce the evidence](#reproduce-the-evidence). It uses a mock provider and does not modify a real project.

## 0:00–1:00 — Submit work on the live board

1. Open the `Checkout demo` board and create the ticket in `Todo`.
2. Assign it to `programmer`.
3. Point out that the ticket is the shared contract: outcome, failure behavior, and tests are visible before work begins.
4. Let the configured automation move it to `InProgress` and start the run.

**Proof 1 — live board:** the prospect sees the ticket leave `Todo`, enter `InProgress`, and show that work is active.

Say: “The board is the control surface: it shows what is waiting, what an agent is changing, and what still needs a person.”

## 1:00–3:00 — Read the run

1. Open the run from the ticket card.
2. Show the request, files inspected, edits made, test command, and final summary.
3. Explain failures in plain terms if a tool or test fails; do not hide or skip them.
4. Return to the ticket when the run finishes.

**Proof 2 — readable run:** the prospect can answer “what did it do?” without opening a terminal or reading raw provider protocol.

Say: “The run is the evidence trail. You can inspect the work while it happens and keep the result with the ticket.”

## 3:00–4:20 — Inspect verification evidence

Open the implementation evidence comment and check:

- the isolated worktree or workspace used;
- the changed behavior and files;
- the exact test commands and observed results;
- the screenshot or browser assertion for the checkout state;
- known limitations.

Move the ticket to `Review` only after the evidence is present. The demo is not complete if the run merely claims success.

## 4:20–5:00 — Keep the release decision human

1. Review the evidence and the visible behavior.
2. Ask the prospect to choose: request changes, block the work, or approve it.
3. Only after explicit approval, move the ticket to `Done` or continue the external release workflow.

**Proof 3 — human validation before external release:** an agent may prepare the change, but a person retains the final decision.

Say: “KittyClaw accelerates the work without taking away release authority.”

End with: “KittyClaw is a local control plane for software work performed by AI agents: the board shows progress, the run explains the work, and you decide what ships.”

## Reproduce the evidence

The repository includes an isolated QaRunner scenario at `KittyClaw.QaRunner/Scenarios/ticket-157/run.ps1`. It creates disposable state, runs the same checkout ticket through the mock provider, asserts the three proof states, and captures screenshots.

From the repository root:

```powershell
dotnet build KittyClaw.ClaudeMock/KittyClaw.ClaudeMock.csproj
dotnet build KittyClaw.Web/KittyClaw.Web.csproj
dotnet build KittyClaw.QaRunner/KittyClaw.QaRunner.csproj
./KittyClaw.QaRunner/Scenarios/ticket-157/run.ps1
```

Expected result: `PASS`, with screenshots named `01-live-board`, `02-readable-run`, and `03-human-validation`. The isolated instance is deleted when the runner exits.

## Presenter check

After the demo, ask one question without prompting: “What is KittyClaw for?” Record the answer verbatim. A successful narrative lets the prospect say, in substance, that KittyClaw locally controls agent-performed software work while keeping progress, evidence, and release approval visible.
