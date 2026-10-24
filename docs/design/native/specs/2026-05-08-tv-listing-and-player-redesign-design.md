# TV Listing & Player Redesign

**Date:** 2026-05-08
**Status:** Design approved, awaiting plan
**Scope:** Tizen .NET TV app (`src/StreamedTv/`)

## Overview

Replace the current text-only `TextLabel`-based UI with a cohesive cinematic TV interface. Three primary surfaces share a single visual language: the listing page (browse), the player (watch), and the More overlay (configure favorites).

## Goals

- One consistent visual language across listing, More overlay, and player chrome.
- Listing page surfaces the three things a sports viewer cares about — what's on now, what's next, what just ended — as three horizontal rows.
- Smooth horizontal navigation within rows (Netflix-style row scroll with focus-anchored tile placement).
- A centered glass nav pill replaces the current "weird" top bar.
- A center-stage hero promotes the focused tile after a short dwell, giving a richer preview before commit.
- Player chrome is sports-aware (live score, stream switcher) and gets out of the way (auto-hide).

## Non-goals

- No in-tile autoplay video previews (still images / gradients only on tiles; the hero is also static).
- No watch history, recommendations engine, or sign-in. "For You" is a popularity-driven view, not a personalized one.
- No first-launch onboarding wizard for favorites; defaults to Cricket / Football / Basketball, configurable via More.
- No real backdrop blur on glass surfaces (NUI limitation; see Technical Notes).
- Bundled `live-demo.json` fallback is not extended with poster art; offline mode renders gradient-only tiles.

## Visual Language

### Color tokens

| Token            | Value                  | Usage                                    |
| ---------------- | ---------------------- | ---------------------------------------- |
| `bg-base`        | `#060608`              | Page background bottom                   |
| `bg-top`         | `#0c0c0e`              | Page background top                      |
| `bg-glow`        | `rgba(229,9,20,0.18)`  | Top radial glow (50% × 30% from top)     |
| `surface-glass`  | `rgba(20,20,22,0.55)`  | Nav pill, hero overlay panels, player chrome |
| `surface-tile`   | `linear(#1a1a1a → #262626)` | Default tile background                |
| `surface-live`   | `linear(#1a0a0a → #3a1414)` | Live tile background                   |
| `surface-sched`  | `linear(#0a1622 → #142b3e)` | Scheduled tile background              |
| `surface-recent` | `linear(#181818 → #232323)` | Recent tile background (75% opacity)   |
| `accent-live`    | `#e50914`              | Live pill, live row glow, hero CTA back  |
| `accent-sched`   | `rgba(64,156,255,0.85)`| Scheduled pill, scheduled hero gradient  |
| `text-primary`   | `#ffffff`              | Tile titles, hero title, active chip text|
| `text-secondary` | `rgba(255,255,255,0.7)`| Subtitles, scores                        |
| `text-tertiary`  | `rgba(255,255,255,0.5)`| Row labels, hints                        |
| `divider`        | `rgba(255,255,255,0.08)`| Glass borders                           |

### Typography

All sizes expressed as % of viewport width (1920px → multiply by 19.2 for px). NUI `PointSize` should be derived from these at runtime against the actual window width so the layout scales identically on 4K displays.

| Token       | vw     | px @1920 | Weight | Usage                              |
| ----------- | ------ | -------- | ------ | ---------------------------------- |
| `text-hero` | 2.4%   | 46       | 700    | Hero title                         |
| `text-l`    | 1.6%   | 31       | 800    | Logo (if added later)              |
| `text-m`    | 1.0%   | 19       | 600    | Tile title, hero subtitle, chip text|
| `text-s`    | 0.85%  | 16       | 600    | Row labels                         |
| `text-xs`   | 0.7%   | 13       | 500    | Pills, tile subtitle, time         |

### Glass surface

Used for the nav pill, hero overlay panels, player top/bottom bars, and stream switcher overlay.

- Fill: `surface-glass` (`rgba(20,20,22,0.55)`)
- Border: 1px `divider`
- Shadow: `0 8px 32px rgba(0,0,0,0.45)` outer, `inset 0 1px 0 rgba(255,255,255,0.06)` inner top highlight
- Corner radius: 16px (`Hero`/`bar`) or pill-shaped (`nav`)

NUI doesn't provide backdrop blur. Against the dark page background the semi-transparent fill + inner highlight + subtle outer shadow reads as glass without a real blur shader.

## Information Architecture

Single page, no scrolling. Layout, top to bottom:

```
┌─────────────────────────────────────────────────────┐
│            [glass nav pill, centered]                │  ~6%  height
├─────────────────────────────────────────────────────┤
│                                                      │
│              [hero — promoted match]                 │  ~30% height
│                                                      │
├─────────────────────────────────────────────────────┤
│  ▶ Live now                                          │
│  [tile] [tile] [tile] [tile] [tile] →               │  ~20% height
├─────────────────────────────────────────────────────┤
│  ⏰ Coming up                                        │
│  [tile] [tile] [tile] [tile] [tile] →               │  ~20% height
├─────────────────────────────────────────────────────┤
│  ⏪ Recently finished                                │
│  [tile] [tile] [tile] [tile] (dimmed)  →            │  ~20% height
└─────────────────────────────────────────────────────┘
```

Vertical totals must fit 1080p without scroll. Padding/margins consume the residual ~4%.

## Components

### Nav Pill (top)

- Centered horizontally, sits ~1.6% from top.
- Glass surface, pill-shaped (border-radius half its height).
- Layout: `★ For You │ <Fav 1> · <Fav 2> · <Fav 3> │ ＋ More`
- Two thin vertical dividers (`divider` color) separate the three groups.
- Active chip = solid white pill (`#ffffff` background, `#0a0a0a` text, weight 600).
- Inactive chip = transparent, `text-secondary` text. Focus = same as active treatment.
- "★ For You" chip when not active has a 1px subtle border to set it apart from the favorites.
- "＋ More" chip opens the More overlay (described below).

### Hero (center)

- One row beneath the nav pill, full-width minus 6% horizontal padding on each side, ~30% screen height.
- Background: gradient based on the promoted match's status (live = red gradient, scheduled = blue, recent = neutral). When the catalog API supplies a poster URL for the match, the gradient is replaced/overlaid by the poster image (async-loaded with the gradient as a fallback while loading).
- Left-side overlay (vertical gradient mask `rgba(0,0,0,0.55)` to `rgba(0,0,0,0)` left-to-right) ensures text remains legible regardless of image content.
- Content (left column, max-width 60%, bottom-aligned):
  - Status pill (live red / sched blue / recent gray) with relevant clock value.
  - Hero title (`text-hero`, weight 700).
  - Subtitle (`text-m`, `text-secondary`): competition · score (or scheduled time / final result) · venue · "N streams available".
  - Primary CTA: white "▶ Play" button.
  - Hint: "Press OK" in `text-xs`, `text-tertiary`, all-caps.

### Tile (cinematic poster)

- Aspect 16:9, width ~14% of viewport.
- Border-radius 10px.
- Background: status gradient (or poster image when available).
- Top-left status pill, top-right optional clock value (e.g. `15.2 ov`, `9:00 PM`).
- Bottom-left meta: title (`text-m`, weight 600) and subtitle (`text-xs`, `text-secondary`). Both have a soft drop shadow so they remain legible over imagery.
- Recent tiles render at 0.7 opacity to visually de-emphasize the row.

### Row

- Row label sits 0.5% above the row, indented to align with the first tile.
- Tiles laid out horizontally with 1.2% gap.
- Row clips horizontally; only tiles within the viewport draw, but the row scrolls with NUI animations.

### More overlay

- Triggered from the `＋ More` chip in the nav pill.
- Modal-style overlay, glass surface, ~70% of viewport, centered.
- Content: full sport list as a grid of pill buttons (Cricket, Football, Basketball, Tennis, F1, Boxing, MMA, Hockey, Baseball, etc.).
- Three of the pills are flagged as the user's current favorites (filled treatment, "PINNED" small label).
- OK on a non-favorite pill replaces the **least-recently-pinned** favorite slot (FIFO, no prompt).
- Back closes the overlay and persists changes.

### Player chrome

Layered on top of the video surface. Auto-hides after 3s of no input; reappears on any remote press.

- **Top-left glass pill**: `● Live · <clock>` immediately followed by the match title. Always visible (does not auto-hide).
- **Top-right glass card**: team badges + score, only rendered when the catalog API provides team or score data. Auto-hides with the rest of the chrome.
- **Bottom-center glass action bar**: four chip-style actions, focus-navigable left/right.
  1. `⤢ Switch stream (N)` — opens the stream switcher overlay.
  2. `🔊 <language> <quality>` — read-only display in v1 (focusable but OK is a no-op).
  3. `⚙ Quality` — read-only display in v1 (focusable but OK is a no-op).
  4. `ℹ Info` — read-only display in v1 (focusable but OK is a no-op).

### Stream switcher overlay

- Triggered from the `⤢ Switch stream` action.
- Glass row pinned to the bottom of the player, above the action bar (which hides while the switcher is open).
- Each stream rendered as a chip showing `<source> · <quality> · <language>` (e.g. `alpha · HD · English`).
- Currently-playing chip has the active treatment.
- Left/Right scroll between chips. OK swaps the source in place; video continues playing while the new source prepares, then transitions when ready.
- Back closes the switcher and re-shows the action bar.

## Focus & Navigation

### Focus zones (vertical order)

```
1. Nav pill chips
2. Hero
3. Live row
4. Coming up row
5. Recently finished row
```

### Key handling

| Key            | Behavior                                                        |
| -------------- | --------------------------------------------------------------- |
| `Up`           | Move focus to previous zone. From nav pill: no-op.              |
| `Down`         | Move focus to next zone. From last row: no-op.                  |
| `Left`         | Move within the focused zone. At zone start: no-op (no wrap).   |
| `Right`        | Move within the focused zone. At zone end: no-op.               |
| `OK` / `Enter` | On a tile or hero: play the match. On a chip: switch sport. On `＋ More`: open overlay. |
| `Back`         | Player → exit to listing. More overlay → close. Listing → exit app. |

### Initial focus

On launch:
- Nav pill: active chip is the persisted last-selected sport (defaults to Cricket on first run).
- Focus zone: the **first tile of the Live row** if there are live matches, otherwise the **first tile of the Coming up row**, otherwise the **hero** (with empty-state messaging).

### Row scroll behavior

When focus moves Left/Right within a row:
- Focused tile is anchored at ~20% from the row's left edge.
- The row scrolls with a NUI `Animation` (200ms ease-out) so the focused tile slides into the anchor position.
- If the focused tile is among the first three tiles, scroll position stays at 0 (no negative scroll).
- If among the last three tiles, scroll position clamps so the last tile is at the right edge.

### "For You" tab

- When the active chip is `★ For You`, the rows below pull from a different data source:
  - Row 1 = popular live matches across all sports (matches with `popular: true` AND `status: live`, sorted by sport-mix to ensure variety).
  - Row 2 = popular upcoming matches across all sports.
  - Row 3 = recently finished matches across all sports.
- Same row labels, same tile rendering, same dwell-to-promote behavior.
- No personalization in v1.

## Dwell-to-Promote Mechanics

- Constant: **dwell duration = 2500ms**.
- When a tile gains focus:
  - A thin (~3px) white ring is drawn around the tile, starting at 12 o'clock.
  - The ring fills clockwise to a complete circle over the dwell duration via NUI `Animation`.
  - On completion, the hero's content cross-fades (300ms) to the focused match: status pill, title, subtitle, CTA, gradient color.
  - Whichever match the hero shows is what `OK` plays from the hero.
- When focus leaves a tile before the dwell completes:
  - The ring animation cancels and resets.
  - The hero remains showing the previously promoted match (does not revert).
- When focus moves between zones:
  - Dwell ring clears on the previously-focused tile.
  - On entering a row, the most recently focused tile in that row regains focus and the dwell starts again.
- On landing (initial render): the hero is auto-promoted to the initially-focused tile (no dwell wait, instant cross-fade).
- `OK` on a tile plays that tile immediately, regardless of dwell state.

## Persistence

Stored in a JSON file at `Tizen.Applications.Application.Current.DirectoryInfo.Data + "/favorites.json"`:

```json
{
  "favoriteSports": ["cricket", "football", "basketball"],
  "lastSelectedSport": "cricket",
  "version": 1
}
```

- Read on app start. If file missing or unparseable, fall back to defaults (`["cricket","football","basketball"]`, `"cricket"`).
- Written when the user changes favorites via the More overlay, or switches sport tabs.
- All file IO via `System.IO.File.ReadAllText` / `WriteAllText`. No async required given file size.
- Errors writing the file are logged to console and otherwise swallowed — the user's session continues with in-memory state.

## Technical Notes

### NUI components used

| Surface              | NUI primitives                                       |
| -------------------- | ---------------------------------------------------- |
| Page root            | `View` with `LinearLayout` (vertical)                |
| Nav pill             | `View` with `LinearLayout` (horizontal), rounded background via `BackgroundColor` + `CornerRadius` |
| Hero                 | `View` with absolute-positioned children; `ImageView` for poster (when present) |
| Row                  | `ScrollableBase` with `LinearLayout` (horizontal)    |
| Tile                 | `View` with `ImageView` background + overlay text via `TextLabel` |
| Player video surface | `Tizen.Multimedia.Player` rendering into a NUI `View` via `MediaView` |
| Player chrome        | `View` overlay layer above the player surface        |

### Glass effect

NUI has no `BackdropFilter` / blur. We approximate with:

- `BackgroundColor = new Color(20/255f, 20/255f, 22/255f, 0.55f)`
- `CornerRadius = 16` (or full pill)
- A 1px-tall `View` overlaid along the top edge for the inner highlight (`rgba(255,255,255,0.06)`)
- `BoxShadow = new Shadow(blurRadius: 32, color: rgba(0,0,0,0.45), offset: (0, 8))`

This is visually sufficient against the dark page background. A real blur shader would be a future enhancement.

### Animations

All transitions use `Tizen.NUI.Animation`. Durations:

| Animation              | Duration | Easing      |
| ---------------------- | -------- | ----------- |
| Tile focus scale       | 200ms    | ease-out    |
| Dwell ring fill        | 2500ms   | linear      |
| Hero content crossfade | 300ms    | ease-in-out |
| Row horizontal scroll  | 200ms    | ease-out    |
| Player chrome fade     | 200ms    | ease-in-out |
| More overlay open      | 250ms    | ease-out    |

### Image strategy

- Catalog matches may include poster URLs via `StreamedEndpoints.MatchPoster(...)` or `ProxiedImage(...)`.
- Tiles with a poster URL: load asynchronously into the tile's `ImageView`. While loading, show the gradient. On error, keep the gradient.
- Hero with a poster URL: same pattern at higher resolution.
- The bundled `live-demo.json` does not contain poster URLs, so offline mode is gradient-only by design.
- No image cache layer in v1; rely on NUI's image cache.

### Catalog data flow

- `StreamedCatalogService.GetMatchesBySportAsync` is called per sport tab on activation, with the existing 1-hour cache.
- For "For You", a new method `GetForYouMatchesAsync` aggregates `GetLiveMatchesAsync` (already exists) plus a new `GetUpcomingMatchesAsync` and reuses cached responses when available.
- Match → row partitioning happens in the listing controller, not in the service. The service returns matches; the controller buckets them by `Status` ("live", "scheduled" → coming up; "recent" → recent).
- Hero content is derived from a `CatalogMatch`; no additional fetch.
- Stream list for the player is fetched on-demand by `GetStreamsAsync` (existing).

### Code organization

The current `Program.cs` (350 lines) does too much — it owns state, key handling, all rendering, and the catalog. The redesign should split this:

| File                              | Responsibility                                |
| --------------------------------- | --------------------------------------------- |
| `Program.cs`                      | `NUIApplication` entry, top-level wiring only |
| `Views/ListingView.cs`            | Listing page composition (nav + hero + 3 rows)|
| `Views/NavPillView.cs`            | Nav pill + chip rendering                     |
| `Views/HeroView.cs`               | Hero rendering + crossfade animation          |
| `Views/RowView.cs`                | Row of tiles + horizontal scroll              |
| `Views/TileView.cs`               | Single tile + dwell ring                      |
| `Views/PlayerView.cs`             | Player chrome (top/bottom bars, action bar)   |
| `Views/StreamSwitcherView.cs`     | Bottom overlay for switching streams          |
| `Views/MoreOverlayView.cs`        | Favorites picker overlay                      |
| `Services/UserPreferences.cs`     | Read/write `favorites.json`                   |
| `Services/FocusManager.cs`        | Tracks focused zone + tile, routes key events |
| `Theme/`                          | Color, typography, animation duration tokens  |

Existing files unchanged: `Models/ApiModels.cs`, `Services/StreamedCatalogService.cs`, `Services/StreamRanker.cs`, `Services/StreamedEndpoints.cs`, `Services/IStreamPlayer.cs`, `Platforms/Tizen/TizenStreamPlayer.cs`.

`StreamedCatalogService` gets one new method (`GetForYouMatchesAsync` — or composed inline by the listing controller from existing methods).

## Acceptance Criteria

The redesign is done when:

1. App launches into the listing page with the glass nav pill, hero, and three rows visible without scrolling on 1920×1080.
2. Initial favorites are Cricket, Football, Basketball; Cricket is the active sport.
3. Left/Right within a row smoothly scrolls (≤200ms) with the focused tile anchored ~20% from the left.
4. Up/Down moves between [nav, hero, row1, row2, row3] with no wrap.
5. Focusing a tile for ≥2.5s promotes it to the hero with a 300ms crossfade. Quick scrolling does not promote.
6. OK on a tile or hero starts playback of the match's top-ranked stream.
7. Player chrome shows live pill + title (always), score (when available), and bottom action bar (auto-hide ~3s).
8. `⤢ Switch stream` opens the bottom overlay, swaps the stream in place on OK, and never freezes the underlying video.
9. Back from player returns to listing with focus restored to the previously-focused tile.
10. `＋ More` opens the favorites overlay; changes persist across app restart via `favorites.json`.
11. "For You" tab shows popular live / upcoming / recent matches across all sports.
12. With the bundled `live-demo.json` (offline mode), the layout still renders with gradient tiles and gradient hero, no image-loading errors logged.
13. The TPK builds cleanly via `dotnet build` and signs/installs on a TV without regressing the existing build pipeline.

## Out of scope (deferred)

- Real backdrop blur shader for glass surfaces.
- Image cache (rely on NUI's built-in for v1).
- Watch history and personalized "For You".
- First-launch onboarding wizard.
- In-tile autoplay video previews.
- Audio/track and quality selectors do anything beyond display in v1.
- Landscape ↔ portrait support (TVs are landscape-only).
