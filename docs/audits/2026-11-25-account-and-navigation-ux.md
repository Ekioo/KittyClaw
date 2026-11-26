# Account and navigation UX review — 25 November 2026

Scope: the marketing website, the central account portal, and the local KittyClaw interface. The existing purple website identity and green application identity are preserved.

| Before | After | Why |
| --- | --- | --- |
| Instance cards expose rename fields, a destructive button and an ISO timestamp immediately. | Cards lead with the machine name, a presence badge and a relative last-seen time. Native “Manage instance” disclosures contain the actions. | Make the instance directory readable before asking the user to edit settings. |
| The portal header has an ambiguous “My account” brand link. | Explicit My instances / Account navigation, current-page state and the official picto. | Separate installations from GitHub and Google sign-in identities. |
| Association can be mistaken for remote access. | A concise availability note explains tracking and management, with remote access explicitly unavailable from the portal. | Describe the current product accurately. |
| Most website navigation disappears on mobile. | All ten content pages share a compact native menu, including installation and My instances. | Preserve access to the full website on small screens. |
| API examples and the homepage FAQ use clickable non-controls. | Keyboard-operable tabs with selection/panel relationships, and native FAQ disclosures. | Make keyboard and touch interaction predictable. |
| The website demo runs automatically without a pause control. | A pause/play button and reduced-motion handling for the board and run illustration. | Let readers stop motion while reading. |
| Local account settings show only explanatory copy. | Settings show the associated account or the current unassociated/pending state. Account setup distinguishes primary, cancel and disconnect actions. | Make ownership visible and explain what disconnecting removes. |
| Error pages contain default developer instructions, and several pages nest main landmarks. | Translated recovery copy, a project return link, one main landmark and a skip link that retains the current route. | Give users a recovery action and a consistent keyboard entry point. |

## Verification

The local application was compiled into `.artifacts/ux-build`, then exercised with disposable data and mock provider binaries on port 5240. Port 5230 was not used. The existing user debug instance on port 5232 was restarted with the compiled interface and its existing data directory.

| Surface | Coverage |
| --- | --- |
| Local application | Project directory, costs, temporary storage, global settings, a populated kanban, a populated ticket, project settings, dashboard, workflows, approvals, not-found, error and local account setup at 390 px. Settings, costs, storage, board, ticket and workflows additionally at 320, 430 and 1440 px. |
| Marketing site | Homepage at 320, 390, 430, 1200 and 1440 px; nine editorial/guide pages at 390 and 1440 px. No page or navigation overflow observed. |
| Central portal | Populated and empty instance lists, two linked identities, one linked identity and login at 320, 430 and 1440 px; instance management opened at 390 px. Synthetic profiles only. |
| Keyboard | Installation tabs with ArrowRight, API tabs with End, native FAQ with Enter, mobile menu with Escape, and local skip-link focus/route preservation. |
| Automated checks | 17 account Worker tests; 9 website cache tests; 7 account/header runtime tests from the isolated .NET output; 48 existing account/settings/localization checks from the usual test output. Worker dry-run bundle and .NET compilation passed. |

Some existing .NET source-inspection tests assume the usual four-parent repository path. Running the broader localization filter from the custom artifacts directory therefore produced 21 path failures; the same 48-check filter passed from the usual output directory. The isolated runtime filter passed independently.

The revised account/recovery strings cover all eight supported local UI languages. Provider sign-in, ownership confirmation, last-provider protection and CSRF handling remain covered by the existing account tests. This pass does not add a remote tunnel or perform paid agent dispatches. Layout fixtures do not test a new real OAuth session.

Screenshots and geometry observations are kept locally under `.artifacts/ux-ui/`. Publication is a separate step; a local preview is not evidence of a production deployment.
