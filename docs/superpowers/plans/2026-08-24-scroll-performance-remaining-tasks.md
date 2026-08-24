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
| `e7900f3` | Task 1 — defer chapter window work during momentum coasts |
| `8010c76` | Task 3 — reject stale and outlier samples when launching inertia |
| `359bde4` | Minimap viewport band drifting outside the loaded region (two bugs — see task 8) |

Task numbers below are kept stable even once done, since they are referred to by number
elsewhere. Check the status line on each.

## Remaining tasks

### 1. Flings get cut short — DONE (`e7900f3`), VERIFIED on device

Post-fix log: **zero** `inertia ABANDONED` across the whole session (was 4 in a 10s window).
Every coast reaches `inertia STOP decayed` after 19-32 ticks, mostly at `realDeltaMs=16`
(clean 60fps). Chapter ops land after `coast ended` lines as intended — e.g. `+ch7` at
19:08:19.101 following a coast that ended at 19:08:18.958.

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

**Implemented** as `ShouldDeferWindowWorkForCoast()`, gating `CheckWindowExtend` and
`CheckWindowBounds`, with an urgency escape at half a viewport from either edge of realized
content, and a catch-up check posted from `StopInertia`. The edge check uses
`Extent.Height - _bottomSpacerHeight`, sidestepping the task-2 bug.

**Verify on device:** `inertia ABANDONED` should become rare or disappear (coasts reaching
`inertia STOP decayed` instead), and chapter ops should cluster after `coast ended` lines rather
than interleaving with `inertia tick#`. If abandonment persists, the stall is coming from
outside the gated paths and task 2 is the real culprit.

Alternatives considered and their tradeoffs:
- *Fix the dead extend gates* (below) so bulk extend becomes reachable: fewer stalls, but each
  batch stalls **longer**, so it may not stop abandonment by itself.
- *Template slimming* to make each op cheaper: real but medium-effort, and the per-op cost would
  still likely exceed 200ms.

### 2. Dead extend gates — priority DOWNGRADED after task 1 shipped

Originally expected to matter because chapter-load stalls were cutting flings short. Task 1
removed that coupling: loads now happen between coasts, so a stall no longer damages a glide.
What remains is a ~300-460ms stutter when a chapter does load (`+ch7` → `gap=464ms`).

Batching would trade several short stalls for fewer longer ones, which is a much less clear win
than it looked before task 1. Worth doing for tidiness and for the correctness of the
`contentBottom` value itself, but no longer urgent.

`contentBottom` is `Extent.Height` at `MainView.axaml.cs:2289` (also `:2434`, `:2389`), which
**includes the bottom spacer**. So `contentBottom - scrollBottom < vpHeight` compares against
~336,000px and is effectively never true mid-book, making the bulk
`ExtendWindowDown(vpHeight * 3)` path unreachable. Only the one-chapter
`ExtendWindowDown(1)` path in `CheckWindowExtend` actually runs.

`GetVisibleChapterRange` computes the correct quantity —
`Extent.Height - _bottomSpacerHeight` — 200 lines later at `:2512`, and does not share it.

Needs a chapter cap on the loop when the bulk path goes live, or one op will load many chapters
and produce a single long stall.

### 3. Velocity sampling is noisy and stale — DONE (`8010c76`), VERIFIED on device

Post-fix log: every launch is 1360-2738px/s over a 44-65ms span, all using 5 of 5 samples and
4 intervals. No outlier spikes, no stale windows, nothing approaching the 6000px/s clamp.

`inertia START v0=10758px/s (2 samples over 27ms)` — a single large post-stall `ScrollChanged`
delta divided by a tiny dt. Also `v0=552px/s (3 samples over 488ms)`, a window far too wide to
describe a flick.

**Implemented** in `StartInertiaFromSamples`: discard samples older than 120ms relative to the
newest, require ≥3 samples, take the **median** of per-interval velocities rather than comparing
endpoints, and clamp to 6000px/s. Age-filtering also fixes press-drag-hold-release, which
previously flung from stale samples.

The 6000px/s clamp is load-bearing for task 1: at a ~130ms decay constant it caps coast distance
at ~780px, keeping it well inside the realized window that `ShouldDeferWindowWorkForCoast`
depends on. Raising it without re-checking that assumption would undermine the deferral.

**Verify on device:** the `inertia START` line now reports interval count and how many samples
survived filtering — no launch should exceed 6000px/s, and holding still before release should
produce no fling at all.

### 4. `StrokePoint` JSON size — now the largest remaining cost (separate worktree)

Latest startup log: total 7.4s to hide the overlay, of which
`JournalStore.LoadEntriesAsync: read=223ms deserialize=4034ms` is by far the biggest single
item. Every other startup phase is now sub-second (book parse 958ms for Psalms' 6307 paragraphs,
journal strokes 2ms thanks to the `5beb421` cache). This is the next real win.

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

### 8. Minimap viewport band outside the loaded region — DONE (`359bde4`), VERIFIED on device

Two independent bugs, found in sequence:

1. **Stale/missing window notification.** `UpdateSpacers` was the only thing telling the minimap
   `_windowStart`/`_windowEnd`, and it runs *before* the index update in `TrimWindowTop` and
   `ExtendWindowDown`, and is skipped entirely inside a guard in `ExtendWindowUp`/`Down`. Fixed
   by carrying the window range on `ScrollMinimapControl.SetViewport`, called every scroll tick
   where it's always current, instead of on the mutation-triggered `SetChapterHeights`.
2. **`RenderTargetBitmap` DPI mismatch.** After fix 1, `MBA_MINIMAP` diagnostics showed the band
   and strip agreeing numerically (same total height, same chapter count, same window, band
   computed strictly inside the loaded range) while the device screen still showed the band
   outside the blue. The mismatch was in `DrawImage`'s blit of the cached bitmap at this
   device's DPI, not in the coordinate math. Fixed by dropping the `RenderTargetBitmap` cache
   entirely and drawing the strip directly every frame — cheap enough now that coast-deferral
   (task 1) keeps window mutation off the scroll path.

`MBA_MINIMAP` logging is kept in as a permanent low-cost tripwire for bug 1's class of issue.

## Lessons & process notes

Generalizable takeaways from this session, not tied to a specific task above.

- **A `record`'s structural equality can silently break dictionary lookups keyed by the
  original object, and knowing about the hazard once does not protect every call site.** This
  codebase already had the fix pattern documented — `_paragraphIndexByRef` explicitly uses
  `ReferenceEqualityComparer` with a comment explaining why — but three other lookups
  (`_paragraphChapterInfo` used in four places) used the default comparer against the same
  `record` type and broke the same way. When a hazard like this is found, audit *every* lookup
  against that type, not just the one that surfaced the bug.

- **Numeric agreement between two computations does not guarantee visual agreement.** The
  minimap band and strip could log identical inputs and a mathematically-correct relationship
  while still rendering in visibly different places, because a caching/blit layer
  (`RenderTargetBitmap` + `DrawImage`) introduced a device-specific DPI mismatch downstream of
  the math. When two things "should" line up and don't on a real device, suspect the *last*
  rendering/serialization step before the pixels, not just the numbers feeding it — and be
  willing to log the actual output geometry (as `MBA_MINIMAP` did), not just the inputs.

- **A fixed decay-per-callback model breaks silently when the callback itself is delayed.** The
  original inertia applied a fixed friction factor per `DispatcherTimer` tick, assuming ticks
  arrive on their nominal interval. When a chapter-load stall delayed a tick, the same *number*
  of decay steps still had to elapse before stopping, but now spread over however long the
  stalled ticks actually took — turning a sub-second coast into a 10+ second one. The fix
  (`b0fbbdd`) scales every step by the real elapsed time since the last tick, not by tick count.
  General shape of the bug: assuming wall-clock time per event when only event *count* is
  guaranteed.

- **On-device diagnostics prevented at least two actively-harmful fixes.** The `MBA_SCROLL`/
  `MBA_STARTUP`/`MBA_MINIMAP` logging repeatedly turned a plausible hypothesis into a *tested*
  one before code changed: the "estimator is 27x wrong" theory (actually a symptom of the
  record-identity bug — the real estimator error was ~1.15-1.8x) and "raise the 200ms inertia
  bailout" (would have restored the runaway-scroll bug it was added to prevent) were both live
  candidates that logged evidence ruled out. When a fix is tempting but its assumptions haven't
  been measured, add the log line before writing the fix.

- **Fixes can be load-bearing on each other's assumptions.** Task 1's coast-deferral relies on
  "a coast cannot travel further than ~780px," which is only true because of task 3's 6000px/s
  velocity clamp and the ~130ms decay constant. Raising that clamp later without re-checking the
  deferral's edge margin would silently reintroduce the bug task 1 fixed. When two fixes share a
  derived constant, say so explicitly in both places (done here in the doc and in code comments)
  so a future change doesn't touch one without the other.

- **Re-rank remaining work after each fix lands, don't fix the priority order upfront.** Task 2
  (dead extend gates) was originally the second-highest priority because chapter-load stalls
  were cutting flings short; once task 1 removed that coupling, task 2's urgency dropped and
  task 4 (`StrokePoint` JSON, unrelated to scrolling) became the largest measured cost. The
  ranking is a function of what's already fixed, not a fixed list.

- **Small magic-number constants should come from logged real-world data, not intuition.** The
  200ms inertia bailout, 120ms sample-age window, 6000px/s velocity clamp, and 32ms slow-layout
  threshold were all set by looking at what the device actually produced (tick timings, launch
  velocities, layout pass durations) rather than picked as round numbers. Constants derived this
  way carry their justification in the same commit as the number, which is what makes them safe
  to revisit later — the numbers above and in tasks 1/3 show the pattern.
