# Licensing

KittyClaw — Copyright (c) 2026 Ekioo

## Application license

KittyClaw is licensed under the **GNU Affero General Public License, version 3 or later (AGPL-3.0-or-later)** — see [LICENSE](LICENSE).

In short: you can use, modify, and self-host KittyClaw freely. If you distribute a modified version, or offer a modified version as a network service (SaaS), you must make the complete corresponding source code of your version available under the same license.

## License history

- Versions **up to and including v0.11** (2026-07-30) were published under the **MIT License** and remain available under those terms.
- All later versions are **AGPL-3.0-or-later**.

## Additional terms (AGPL section 7)

As permitted by section 7 of the AGPL, the following supplementary terms apply to KittyClaw and to every covered work based on it:

1. **Attribution preservation — §7(b).** All copies and derivative works, in source or binary form, must preserve: (a) the copyright and license notices in `LICENSE` and in this file; (b) the Appropriate Legal Notice displayed by the application's user interface (the footer identifying KittyClaw, Ekioo, the source repository URL, and the license — see `KittyClaw.Web/Components/Pages/UnifiedBoard.razor`); and (c) a visible statement in the work's README that it is based on KittyClaw (<https://github.com/Ekioo/KittyClaw>). These notices may be restyled or relocated, but not removed, hidden, or made materially less visible.
2. **No misrepresentation of origin — §7(c).** Modified versions must carry prominent notices stating that they differ from KittyClaw, must not be presented as the original work, and must not misrepresent their origin. Conversely, they must not suggest that Ekioo authored or endorses the modifications.
3. **Trademarks — §7(e).** This license grants no rights to the "KittyClaw" name or logos. Derivative works may not use them as their own name or branding; using the name to state provenance, as required by term 1, is expected and permitted.

These terms are supplementary terms of the kinds expressly allowed by AGPL section 7; they are part of the license of this work and must be retained by all downstream recipients.

## Template and output exception

The AGPL covers the KittyClaw application itself — **not what it produces for you**. As an additional permission under section 7 of the AGPL, and for the avoidance of doubt:

1. **Project template files** — everything under [`ProjectTemplate/`](ProjectTemplate/) (agent skills, memory stubs, `preamble.md`, `automations.json`, the workspace `CLAUDE.md`, …), including the copies KittyClaw writes into your project workspace on initialization (`<workspace>/.agents/**`, `<workspace>/CLAUDE.md`) — are additionally licensed under the **MIT License**. You may keep, modify, and redistribute them in your own projects under MIT terms, with no copyleft obligation.
2. **Application output** — tickets, comments, run logs, agent memory files, commits produced by agents, and any other data KittyClaw generates or manages on your behalf — is **yours**. The AGPL imposes no license, disclosure, or attribution requirement on it.

Initializing or managing a project with KittyClaw never places that project under the AGPL.
