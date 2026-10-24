# Mobile UX audit — 2026-10-24

## Scope

All routed application pages, the unified board's card and Kanban modes, nested
boards, and the remaining dialog and drawer families were inventoried. The ticket
sheet already has separate desktop and mobile components. Other screens now have
purposeful phone compositions while sharing their existing service calls and form
state with desktop.

The breakpoint is 767px. Feature styles continue to own desktop; phone
compositions are in `css/mobile-views.css` and the existing `css/mobile.css`.
Ticket-specific phone styles stay in `css/ticket-panel-mobile.css`.

## Findings and changes

| Before | After | Why |
| --- | --- | --- |
| Settings exposed a long stack of unrelated sections. | A sticky, localized section picker shows one section on phones. All sections remain mounted and visible on desktop. | Reach the needed setting quickly and preserve unsaved form drafts. |
| Members, labels and secrets used compact editable table rows. | Phone rows become labelled cards with full-width inputs and reachable actions. | Retain editing features without sideways table navigation. |
| The cost filter form occupied most of the initial phone screen. | A compact date summary expands the filter form on demand. Desktop filters stay open. | Keep the report and totals visible first. |
| Column configuration and sorting depended on a context click. | Phone column headers expose a touch button; context menus become scrollable bottom sheets. | Make these commands discoverable without a mouse. |
| Unified lane titles competed with badges, pipeline tabs and links. | A phone grid gives the name a real text column and puts pipelines and actions on their own rows. | Prevent long project names from breaking into a narrow stack. |
| Dashboard examples and some native file inputs expanded beyond their containers. | Examples wrap; import grids and file controls can shrink to phone width. | Fix the sizing cause rather than conceal overflow. |
| The workflow wizard clipped pipeline previews and kept horizontal column cards. | Pipeline previews wrap and column/review steps stack vertically on phones. | Keep every workflow step readable and editable. |
| Dialogs retained nested outer margins and inconsistent mobile scrolling. | Creation, folder selection, column editing, pipeline kits, first-ticket forms, ticket improvement and tile configuration use full-height phone surfaces. | Give forms useful width and keep their header/footer actions reachable. |
| A clickable agent spinner rotated its whole hit area and was only 14px wide. | The button stays stationary; a pseudo-element spins inside it. Phone run controls have a 44px target. | Improve touch precision and keyboard/browser interaction without changing the status signal. |
| Project creation could accept a click before its interactive component attached. | Its opening button enables after the first interactive render. | Avoid dropping an early click during page startup. |
| Standalone network/access forms used desktop gutters on phones. | Their shared HTML shell uses phone-width forms, scalable images and safe-area spacing. | Cover network setup, remote setup and authenticator forms consistently. |
| Error and missing-page views had no content boundary or recovery navigation. | A bounded readable surface includes a localized link back to projects. | Keep recovery usable on either viewport. |

## View coverage

| View family | Phone composition |
| --- | --- |
| Home, `/board`, `/projects` | Wrapped search/navigation, readable project cards, full-width unified lanes, touch-sized lane controls. |
| Project and nested Kanbans | One snapping column per viewport, existing previous/next navigation, visible context-action buttons and bottom-sheet menus. |
| Ticket detail | Dedicated mobile component with a sticky toolbar, full-width heading and native Details disclosure; dedicated desktop component retained. |
| Ticket creation, criteria and fullscreen editing | Full-width form fields, wrapped priorities/actions, scrolling editor body and safe-area spacing. |
| Global settings | Network, Internet access, language, providers and MCP available through the phone section picker. |
| Project settings | Worktrees, workspace, fallback/local models, RTK, permissions, secrets, members, labels and deletion remain individually accessible. Editable lists use cards. |
| Workflows and project skills | Separate phone sections for pipeline kits, pipelines and skills; field groups stack. |
| Column identity, processor and schedules | Full-screen editor with scrollable section tabs, stacked fields, routes and action settings, and a reachable footer. |
| Workflow onboarding/reconfiguration | Full-screen wizard, visible progress navigation, stacked pipeline/column/review cards and wrapped footer actions. |
| Pipeline export/import | Full-screen dialog, bounded native file input, stacked details and local scrolling for source content. |
| Costs | Collapsed phone filters with date summary, touch-sized date presets, bounded project picker and stacked report cards. |
| Dashboard and tile configuration | A vertical tile feed preserves stored desktop coordinates. Empty-state examples wrap; configuration and tile chat fit the screen. |
| Agent chat and execution drawers | Full-screen layouts retain their close action, readable header metadata and a bounded message/log area with a reachable composer. |
| Approvals | Phone cards, vertically arranged decisions, readable technical details and wrapped history summaries. |
| Project creation, folder selection and first ticket | Full-screen form surfaces, full-width path entry and stable actions. |
| Access/network/remote setup | One shared responsive HTML shell; authentication and enrollment behavior unchanged. |
| Error, missing page, update and reconnect surfaces | Readable wrapping, reachable controls and recovery navigation. |

## Verification

Browser verification used an isolated instance with its own temporary registry,
projects and runs, with mock provider executables. Port 5232 was already in use,
so the audit instance used 5233. The main instance was not used.

The browser sweep covers 320, 390 and 430px phone widths and a 1440px desktop
viewport, including populated members, labels, secret metadata, pending approvals,
dashboard tiles, nested tickets and a synthetic run. It checks document width,
dialog widths and screenshots, and exercises section switching, draft retention,
column menus, editor tabs, native file selection surfaces, chat navigation, run
opening, filter disclosure and desktop/mobile layout transitions.

Dashboard layout records were compared before and after responsive browsing;
saved positions and sizes did not change. Translation keys and format placeholders
match in all seven supported languages. The targeted localization and network
suite passed 31 tests.

The main sweep rendered 185 view/viewport combinations. A further 24 combinations
covered the first-ticket draft, folder selection, detailed workflow columns and
review, criterion editing and deletion confirmations. Failures caused by test
locators or server reloads were corrected and the affected paths were repeated;
the final supplemental pass had no layout failures. No browser JavaScript errors
were reported by the main sweep.

Screenshots and detailed execution output remain outside the repository, in
temporary audit storage. This document contains only the maintained UI inventory
and verification summary.

## Boundaries

Phone checks use Chromium/Edge viewport emulation. Physical iOS/Android keyboards,
browser safe-area behavior and real provider execution are not certified by this
pass. The new layouts do not depend on executing a real provider. No deployment
or production data migration was performed.

## Project directory follow-up

The requested home simplification replaces the multi-project Kanban and project-card switch with one directory on `/`, `/board`, and `/projects`. Project Kanbans and ticket details remain available through each project's direct link.

| Before | After | Purpose |
| --- | --- | --- |
| Separate project grid and multi-project ticket lanes | One project directory with no tickets or view-mode switch | Focus the home on choosing a project |
| Project-card entry plus lane-specific actions | Kanban, dashboard and settings links on every project | Keep each destination directly reachable on desktop and phone |
| Ticket-oriented global search | Project, pipeline and column-name search | Filter projects without exposing ticket results |
| Label chips on project rows | Ordered column names and root-ticket counts, including zero | Show board progress at a glance, separated by pipeline |
| Light native select popup with pale options | Dark native menus with explicit option foreground and background | Keep ticket-column choices readable |
| Read-only column counters | Direct links to the matching pipeline and column, with touch-sized targets | Open the correct part of a Kanban in one action |

The initial directory was verified on all three home routes at 320, 390, 430, 768, 1024 and 1440 px in Edge emulation. The corrected column-summary version is checked at 320, 390, 430 and 1440 px: ordered names and counts match each board, zero-count columns remain visible, ticket creation/movement/deletion updates counts live, and no ticket cards or label chips appear on the directory. The ticket-column menu uses the dark native color scheme and explicit readable option colors on mobile and desktop. The related updated service, search, refresh and localization selection passes 35 tests. An unrelated existing `TipsPool_ExplainsTheNewWorkflowFeatures` text contract still expects the word `migration` in the unchanged English tips bundle; it is excluded from that targeted result.

Column links were verified at 320 and 390 px with touch emulation and at 1440 px with keyboard activation: first/last columns, empty columns in a second pipeline with a duplicate column name, reload, query-only navigation, browser back/forward, and invalid/mismatched IDs. The destination is brought into view without scrolling the page header, receives keyboard focus, and is highlighted. Mobile navigation remains usable afterward. The updated related test selection passes 30 tests. Temporary verification projects were removed; existing demo tickets remain unchanged.
