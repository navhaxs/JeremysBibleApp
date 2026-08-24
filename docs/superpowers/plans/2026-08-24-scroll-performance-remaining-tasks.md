# Scroll & startup performance — remaining tasks

Working notes from the 2026-08-24 debugging session on windowed chapter scrolling in
`MyBibleApp/Views/MainView.axaml.cs`. Everything below was diagnosed against real-device
`adb logcat` evidence, not inferred.

## Diagnostics available

Two stable logcat tags, both `Console.WriteLine` (not `Debug.WriteLine`, so they survive
Release builds — device testing may well be a Release APK):

```bash
adb logcat | grep MBA_SCROLL     # scroll, inertia, chapter add/remove, slow layout passes
adb logcat | grep MBA_STARTUP    # startup phases, book parse, journal store
```

`MBA_SCROLL` covers: per-tick inertia state (velocity, real elapsed ms, step), every
`ScrollChanged` (offset/delta/extent/viewport), `LayoutUpdated` gaps over 32ms,
`RebuildParagraphTopCache` timing over 5ms, and scroll position on every chapter add/remove.

The debug scroll minimap (`ScrollMinimapControl`, visible when `AppVM.IsDebugMode` is on) shows
the whole book proportionally: loaded vs virtual chapters, the viewport as a chapter range, and
a green/red flash per chapter enter/exit.

## Root cause that explained most of it (fixed, `b0fbbdd`)

`BibleParagraph` is a `record` → structural equality. `PrepareForDisplay`
(`MainView.axaml.cs:1961`) returns `para with { EffectivePoetryLevel = ... }` for poetry, so
display copies are **not value-equal** to originals. But `_paragraphChapterInfo`
(`MainView.axaml.cs:115`) uses the default comparer and is keyed by originals, while every
`ListBoxItem.DataContext` is the display copy.

Result: all four visual-tree lookups silently `continue`d past most rows in a poetry-heavy book.
Five downstream consequences, all confirmed on device — ~10x chapter-height undercount,
uncompensated content shifts on every top trim, extent shrinking over time, `_chapterStartY`
missing entries entirely (which steered every extend/trim decision), and `_chapterLocalTops`
holes breaking ink anchoring for poetry.

Notably this **also** invalidated an earlier hypothesis in the same session: the estimator was
blamed for a "27x error" when in fact `EstimateChapterHeight` is only ~1.15-1.8x high. Post-fix
logs show `est=1500` against measured 979-1725px. A planned estimator rewrite was dropped as
unnecessary.

## Done (all device-verified)

| Commit | Fix |
|---|---|
| `8dd7ab2` | Touch-drag axis detection used per-event delta instead of total displacement from press |
| `5beb421` | `JournalStore` re-read + re-deserialized `journals.json` on every call — startup 195s → 22.9s |
| `d1d2a61` | Debug scroll minimap; `FindParagraphIndex` O(N) scan → O(1) reference-keyed dictionary |
| `4794e77` | Minimap redrew the whole chapter strip on every scroll tick; now cached to a bitmap |
| `8cf5e30` | Avalonia's built-in FPS overlay wired to `IsDebugMode` |
| `4a037ab` | Widened chapter trim buffer; idle chapter preloading |
| `afbbeeb` | Header showed previous chapter/verse right after navigation (sub-pixel tie-break) |
| `b0fbbdd` | Record-identity measurement bug; time-based inertia; minimap coordinate space; `MBA_SCROLL` logging |

## Remaining tasks

### 1. Flings get cut short (highest priority)

**Symptom.** Four `inertia ABANDONED` events in a 10-second scroll log, at
`realDeltaMs=424/564/408/720`. The 200ms bailout added in `b0fbbdd` is working as designed, but
it means a chapter-load stall is killing the coast mid-glide.

**Do NOT "fix" this by raising the 200ms threshold** — that just restores the runaway-scroll bug
it was added to stop (a starved timer stretching a sub-second coast over 10+ seconds while still
covering the full distance).

**Cause.** Chapter ops are still one-at-a-time and interleave into add+trim pairs, each a full
layout pass at 150-720ms. Observed: `+ch14` → `gap=300ms` → `-ch9` → `gap=188ms` → abandoned at
`realDeltaMs=564`.

**Key measurement that shapes the fix.** Inertia decays with a time constant of ~130ms, so total
coast distance is roughly `v0 × 0.13`. At the observed `v0` of 2000-3000px/s that is only
**~260-390px**. The loaded window is 5-7 Psalms chapters, i.e. several thousand px. **A coast
cannot outrun the buffer.** So deferring non-urgent window mutation until the coast ends is safe.

Chosen approach: gate non-urgent extends/trims on inertia being idle (same pattern the idle
preloader already uses), with an urgency escape when the viewport genuinely approaches the edge
of loaded content, and a single deferred check when the coast stops.

Alternatives considered and their tradeoffs:
- *Fix the dead extend gates* (below) so bulk extend becomes reachable: fewer stalls, but each
  batch stalls **longer**, so it may not stop abandonment by itself.
- *Template slimming* to make each op cheaper: real but medium-effort, and the per-op cost would
  still likely exceed 200ms.

### 2. Dead extend gates

`contentBottom` is `Extent.Height` at `MainView.axaml.cs:2289` (also `:2434`, `:2389`), which
**includes the bottom spacer**. So `contentBottom - scrollBottom < vpHeight` compares against
~336,000px and is effectively never true mid-book, making the bulk
`ExtendWindowDown(vpHeight * 3)` path unreachable. Only the one-chapter
`ExtendWindowDown(1)` path in `CheckWindowExtend` actually runs.

`GetVisibleChapterRange` computes the correct quantity —
`Extent.Height - _bottomSpacerHeight` — 200 lines later at `:2512`, and does not share it.

Needs a chapter cap on the loop when the bulk path goes live, or one op will load many chapters
and produce a single long stall.

### 3. Velocity sampling is noisy and stale

`inertia START v0=10758px/s (2 samples over 27ms)` — a single large post-stall `ScrollChanged`
delta divided by a tiny dt. Also `v0=552px/s (3 samples over 488ms)`, a window far too wide to
describe a flick.

Fix: require a minimum sample count, age out samples older than ~100ms, reject implausible
velocities. Small and self-contained. `_touchVelocitySamples` is populated in
`OnMarginTouchMoved` and consumed by `StartInertiaFromSamples`.

### 4. `StrokePoint` JSON size (user wants this in a separate worktree)

`journals.json` is **17.9MB** for 5 journals / 4659 ink strokes — ~3.8KB per stroke. Logged
breakdown: 252ms disk read, **4065ms deserialize**, migration not involved (`dirty=False`).

`StrokePoint` is `record struct StrokePoint(double X, double Y)`
(`MyBibleApp/Models/Journal.cs:59`), so every point serialises as a full JSON object with
default double precision. `JournalStore.JsonOptions` previously had `WriteIndented = true`,
pretty-printing every point onto its own indented line (already flipped to `false`).

Ranked options:
1. Round coordinates to 1-2 decimals plus a compact `[x,y]` array encoding via a custom
   converter — needs to still read the old format for backward compatibility.
2. Lazy/indexed loading so reading one chapter's strokes does not deserialise the whole store.

### 5. Verify ink strokes on poetry paragraphs

`_chapterLocalTops` had holes wherever poetry rows were skipped, so strokes anchored to poetry
paragraphs fell back to `delta=0` and rendered at wrong positions. `b0fbbdd` should have fixed
this, but the visual result has **not** been checked. Open a journal with handwritten strokes
over Psalms poetry and confirm they land correctly.

### 6. Consider whether the window should be symmetric

Backward and forward buffering are deliberately asymmetric:
- `ExtendWindowUp()` fires only when `scrollTop < vpHeight * 0.5` and adds exactly one chapter.
- `ExtendWindowDown(vpHeight * 3)` gets three viewport-heights of budget.
- `TrimWindowTop` then actively removes from behind.

Reading forward therefore accrues a forward buffer and sheds the back, which is defensible for
forward reading but means scrolling back up re-pays chapter-load cost. Decide intentionally
rather than by accident.

### 7. Deferred: chapter-count buffers instead of pixel thresholds

Raised earlier and deferred in favour of raising the pixel multiplier. Worth revisiting: pixel
thresholds are measured against `scrollTop`, which lives in a **mixed** coordinate space
(estimate-based spacers plus real measured loaded content), so the same threshold means very
different things in different parts of a book. Two investigator agents assigned to assess this
died on connection errors, so it remains formally unassessed.

Note the constraint: the scroll extent itself must stay pixel-based (the `ScrollViewer` needs a
pixel extent), so switching only the *decision* thresholds may not be sufficient on its own.

The live-tunable `_trimThresholdMultiplier` (default 10) and `_idlePreloadTargetMultiplier`
(default 3.5) are exposed as `NumericUpDown` controls in the Scroll Debug overlay, so they can
be swept on-device without a rebuild.
