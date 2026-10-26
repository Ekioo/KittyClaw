# Projects using KittyClaw

This page lists projects that use KittyClaw to coordinate real work, followed by forks of KittyClaw. Directory entries, article mentions, UI mockups, and unrelated projects with similar names are excluded.

Inclusion is informational, not an endorsement. Each project remains owned, maintained, and licensed by its respective authors.

Last updated: **2026-10-23**. Usage is supported by public workspace evidence or confirmed directly by the project owner. Source repositories may remain private.

## Verification labels

- **Verified workspace** — the public repository explicitly identifies KittyClaw as its orchestrator or contains KittyClaw-specific API configuration. A generic `.agents/` directory alone is not sufficient evidence.
- **Owner-confirmed usage** — the project owner directly confirms using KittyClaw. Publishing the source repository or orchestration configuration is not required.

## Projects using KittyClaw

### Confirmed projects

| Project | Description |
| --- | --- |
| [Bloomii](https://bloomii.eu) | A constructive-journalism outlet documenting concrete social and environmental initiatives. |
| [Kalceo](https://kalceo.fr) | Practical guides, regulatory resources, and tools for French building trades. |
| [Ekioo.com](https://ekioo.com) | An independent software studio website, project portfolio, and technical articles. |
| [Ekioo.ai](https://ekioo.ai) | An Ekioo project using KittyClaw to coordinate work. |
| [Ukinoo](https://ukinoo.com) | A creative video IP project with an agent-driven production and distribution workflow. |
| ShinyDeal | A project using KittyClaw to coordinate work; no public repository or website is linked here. |

### Public workspace evidence

### [KEA](https://github.com/mokbhai/KEA)

**Verified workspace**

A cross-platform AI utility for rewriting, dictation, meeting notes, and text-to-speech, built with Tauri, Rust, and React. Its public repository includes a KittyClaw `.agents` workspace and API preamble.

Evidence: [`CLAUDE.md`](https://github.com/mokbhai/KEA/blob/11e9180ff8497a36094664434e4439e9f300b26d/CLAUDE.md) explicitly identifies KittyClaw as the workspace orchestrator, and [`.agents/preamble.md`](https://github.com/mokbhai/KEA/blob/11e9180ff8497a36094664434e4439e9f300b26d/.agents/preamble.md) references its local API documentation.

## Forks

### [GigaClaw](https://github.com/FoodBreakPedro/GigaClaw)

A self-hosted fork of KittyClaw for managing software projects with teams of AI agents. A kanban board coordinates specialized roles such as planning, programming, testing, and review: moving a ticket, posting a comment, or reaching a scheduled interval can automatically launch an agent, with its output streamed directly into the board. GigaClaw supports Claude Code and OpenAI Codex, with optional local models through Ollama, and keeps project data and agent memory on the user's machine.

Built with .NET 10 and Blazor Server, with SQLite storage per project. See the [README](https://github.com/FoodBreakPedro/GigaClaw/blob/main/README.md) for setup and supported workflows, or watch the [demo](https://www.youtube.com/watch?v=nqDHH1T5TwA).

Forks are listed separately from projects whose development is orchestrated by KittyClaw.

## Add your project

Using KittyClaw on a project, or maintaining a fork? Open an issue or pull request with:

- the project website or public repository URL, if available;
- a short, plain-language description;
- an optional screenshot or demo link;
- whether this is a real use of KittyClaw or a fork;
- owner confirmation of usage, or a direct link to KittyClaw-specific workspace evidence.

Before linking a repository as public evidence, confirm that it is accessible without authentication. Access through an authenticated GitHub connector does not establish public visibility. Owner-confirmed projects can be listed with private source repositories, without exposing their contents or links.

Projects can be listed without publishing `.agents/`, memories, secrets, run logs, or any other private orchestration data.
