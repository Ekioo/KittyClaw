# Graphic charter

Single source of truth for KittyClaw's visual language. When adding new UI, reuse
existing CSS variables and component patterns rather than introducing one-off colors,
radii, or font sizes.

All variables are defined at the top of [`KittyClaw.Web/wwwroot/app.css`](../KittyClaw.Web/wwwroot/app.css).

## Palette

### Surfaces (background layers)

| Variable | Value | Usage |
|---|---|---|
| `--bg` | `#05050a` | App background (the deepest layer) |
| `--surface` | `#191928` | Cards, panels, popups, headers |
| `--surface2` | `#232338` | Inputs, secondary panels, hover-emphasis |
| `--surface3` | `#2e2e48` | Track backgrounds (progress bars, gauge background, raised pills) |

Layering rule: deeper surface = lower in the stacking. Never paint a `surface` block
on top of `surface3` — the contrast inverts visually.

### Text

| Variable | Value | Usage |
|---|---|---|
| `--text` | `#e4e4f0` | Primary text |
| `--text-muted` | `#7a7a96` | Labels, captions, metadata, axis labels |
| `--text-dim` | `#46466a` | Disabled/placeholder, divider hints |

### Borders

| Variable | Value | Usage |
|---|---|---|
| `--border` | `#2e2e48` | Default 1px borders on inputs, cards, tiles |
| `--border-strong` | `#464672` | Borders that need to read as "interactive" (focus-adjacent, dialogs) |

### Accents (semantic)

| Variable | Value | Usage |
|---|---|---|
| `--accent` | `#22c55e` (green) | Primary action, success, "go", confirmed state |
| `--accent-hover` | `#4ade80` | `--accent` on hover |
| `--accent-dim` | `rgba(34, 197, 94, 0.15)` | Subtle accent fill (tile hover, soft selection) |
| `--accent-info` | `#3b82f6` (blue) | Info states, secondary data, "in progress" |
| `--accent-info-hover` | `#60a5fa` | `--accent-info` on hover |
| `--accent-success` | `#22c55e` | Status "ok" (alias of `--accent`) |
| `--s-blocked` | `#f06b6b` | Errors, blocked status, destructive actions |

### Chart palette (categorical)

When rendering categorical charts (bar, donut, leaderboard…), cycle through this
sequence. Defined in `TileRenderer._palette`:

```
#22c55e #3b82f6 #f59e0b #ef4444 #a855f7
#06b6d4 #ec4899 #84cc16 #f97316 #14b8a6
```

Status-grid uses a fixed mapping: `ok` → `--accent-success`, `warn` → `#f59e0b`,
`err` → `--s-blocked`.

## Typography

- Font family: `"Inter", -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif`
- Base size: `14px` on `body`
- Headings: `h1: 1.4rem`, `h2: 1.1rem`, `h3: 0.95rem` — all 600 weight, `letter-spacing: -0.02em` on h1
- Labels (form, axis, tile-kpi-label): `0.7–0.78rem`, `text-transform: uppercase`,
  `letter-spacing: 0.04em`, `color: var(--text-muted)`
- Tabular numbers: add `font-variant-numeric: tabular-nums` whenever values align
  in columns (KPIs, progress, leaderboard scores, bar-chart values)

## Spacing & shape

- **Border radius**: `4px` for chips/cells, `7px` for inputs/selects/buttons,
  `8px` for tiles and large cards, `12px` for popups
- **Tile content padding**: `0.9rem` (defined on `.dashboard-tile-content`)
- **Tile snap grid**: 20px (defined as `SNAP` in `dashboard.js` and the
  `snap()` helper in `DashboardService`)

## Form controls

Inputs, textareas, and selects in popups must use this exact pattern (see
`.tile-config-field input/textarea/select` in app.css):

- `background: var(--surface2)`
- `border: 1px solid var(--border)`
- `border-radius: 7px`
- `color: var(--text)`
- `font-size: 0.88rem`
- `padding: 0.5rem 0.65rem`
- `:focus { border-color: var(--accent); outline: none; }`
- `:disabled { opacity: 0.55; cursor: not-allowed; }`

For `<select>`: also set `appearance: none` and an inline-SVG dropdown arrow
positioned at `right 0.7rem center` to match the dark theme.

## Buttons

| Variant | Background | Border | Color | Hover |
|---|---|---|---|---|
| Primary (`tile-config-accept`) | `var(--accent)` | none | `#000` | opacity 0.9 |
| Outline / secondary (`tile-config-revise`) | none | `1px solid var(--border-strong)` | `var(--text)` | bg `accent 12%`, border + text accent |
| Ghost / cancel (`tile-config-cancel`) | none | `1px solid var(--border)` | `var(--text-muted)` | bg `surface3`, color `text` |
| Tile header icon (refresh, edit, remove) | none | none | `var(--text-muted)` | tinted bg + colored icon (accent for action, `s-blocked` for destroy) |

Tile-header icons start at `opacity: 0` and become `opacity: 1` on
`.dashboard-tile:hover` — keep the chrome out of the way until the user
interacts with the tile.

## Animations

- All transitions use `0.12–0.15s` linear or default ease — never overshoot 0.2s
  on hovers, the UI should feel snappy.
- `transition` is always declared with the specific property list (`background`,
  `border-color`, `color`, `opacity`), never `all` — avoids reflows on unrelated
  property changes.

## Dashboard tiles (renderer-specific)

Each template renders inside `.dashboard-tile-content` with a fixed root class.
See `TileRenderer.cs` and `app.css` for the per-template implementations:

- `.tile-markdown`, `.tile-table`, `.tile-kpi`, `.tile-kpi-grid`,
  `.tile-progress`, `.tile-sparkline`, `.tile-bar-chart`, `.tile-donut`,
  `.tile-gauge`, `.tile-status-grid`, `.tile-heatmap-wrap`, `.tile-leaderboard`,
  `.tile-image`, `.tile-mermaid`, `.tile-timeline`

When adding a new template, mirror the conventions:
- Numeric values use the chart palette or semantic accents.
- Labels in `--text-muted`, values in `--text`.
- SVG charts use `viewBox` so they scale with the tile; no fixed pixel sizes.
- No client-side JS dependency unless absolutely required (mermaid is the lone
  exception, lazy-loaded from CDN in `dashboard.js`).
