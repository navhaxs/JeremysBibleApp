# Fix Scrollbar H-Scroll Pinning Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep the vertical scrollbar and chapter markers pinned to the viewport's right edge when the user horizontally scrolls in journal mode.

**Architecture:** Cut `ReaderProgressTrack` and `ChapterMarkersCanvas` from inside `InkAreaGrid` (which lives inside the horizontal ScrollViewer) and paste them into the outer layout Grid at `Grid.Row="2"`. As later siblings of `ContentHScrollContainer` in the same Grid row, they overlay the content but are unaffected by horizontal scroll. No code-behind changes — all handlers and positioning logic use track-relative coordinates.

**Tech Stack:** Avalonia UI (AXAML only)

## Global Constraints

- No code-behind changes — `MainView.axaml` only
- No new NuGet packages
- Element names must stay identical: `ReaderProgressTrack`, `ReaderProgressThumb`, `ChapterMarkersCanvas`
- Mobile scrollbar behavior unchanged (`PlatformHelper.IsDesktop` guard in code-behind is untouched)

---

### Task 1: Relocate scrollbar and chapter markers to outer Grid

**Files:**
- Modify: `MyBibleApp/Views/MainView.axaml`

**Interfaces:**
- Produces: `ReaderProgressTrack` and `ChapterMarkersCanvas` now children of the outer `Grid` at `Grid.Row="2"` instead of `InkAreaGrid`

---

- [ ] **Step 1: Remove the two elements from InkAreaGrid**

In `MyBibleApp/Views/MainView.axaml`, find and delete the following block (currently around lines 609–646, inside `InkAreaGrid`). Delete from the comment through the closing tag of `ChapterMarkersCanvas`:

```xml
                <!--  Custom scrollbar — index-based so it stays accurate with virtualization.  -->
                <Border
                    Background="#15808080"
                    Cursor="Arrow"
                    Grid.Row="0"
                    HorizontalAlignment="Right"
                    PointerCaptureLost="OnProgressTrackPointerCaptureLost"
                    PointerMoved="OnProgressTrackPointerMoved"
                    PointerPressed="OnProgressTrackPointerPressed"
                    PointerReleased="OnProgressTrackPointerReleased"
                    VerticalAlignment="Stretch"
                    Width="12"
                    ZIndex="20"
                    x:Name="ReaderProgressTrack">
                    <Canvas>
                        <Border
                            Background="{DynamicResource ThemeAccentBrush}"
                            Canvas.Left="2"
                            Canvas.Top="0"
                            CornerRadius="4"
                            Height="40"
                            Opacity="0.55"
                            Width="8"
                            x:Name="ReaderProgressThumb" />
                    </Canvas>
                </Border>

                <!--  Chapter markers — shown to the left of the scrollbar while dragging the thumb.  -->
                <Canvas
                    Grid.Row="0"
                    HorizontalAlignment="Right"
                    IsHitTestVisible="False"
                    IsVisible="False"
                    Margin="0,0,14,0"
                    VerticalAlignment="Stretch"
                    Width="52"
                    ZIndex="19"
                    x:Name="ChapterMarkersCanvas" />
```

- [ ] **Step 2: Insert the elements into the outer Grid at Row 2**

In `MyBibleApp/Views/MainView.axaml`, find the outer Grid's closing tag (immediately after `</ScrollViewer>` that closes `ContentHScrollContainer`, currently around line 671–672):

```xml
            </ScrollViewer>
        </Grid>
```

Insert the two elements between `</ScrollViewer>` and `</Grid>`, changing `Grid.Row="0"` to `Grid.Row="2"` on each:

```xml
            </ScrollViewer>

            <!--  Custom scrollbar — pinned to viewport right edge, outside horizontal scroll container.  -->
            <Border
                Background="#15808080"
                Cursor="Arrow"
                Grid.Row="2"
                HorizontalAlignment="Right"
                PointerCaptureLost="OnProgressTrackPointerCaptureLost"
                PointerMoved="OnProgressTrackPointerMoved"
                PointerPressed="OnProgressTrackPointerPressed"
                PointerReleased="OnProgressTrackPointerReleased"
                VerticalAlignment="Stretch"
                Width="12"
                ZIndex="20"
                x:Name="ReaderProgressTrack">
                <Canvas>
                    <Border
                        Background="{DynamicResource ThemeAccentBrush}"
                        Canvas.Left="2"
                        Canvas.Top="0"
                        CornerRadius="4"
                        Height="40"
                        Opacity="0.55"
                        Width="8"
                        x:Name="ReaderProgressThumb" />
                </Canvas>
            </Border>

            <!--  Chapter markers — shown to the left of the scrollbar while dragging the thumb.  -->
            <Canvas
                Grid.Row="2"
                HorizontalAlignment="Right"
                IsHitTestVisible="False"
                IsVisible="False"
                Margin="0,0,14,0"
                VerticalAlignment="Stretch"
                Width="52"
                ZIndex="19"
                x:Name="ChapterMarkersCanvas" />

        </Grid>
```

- [ ] **Step 3: Build**

```
dotnet build MyBibleApp/MyBibleApp.csproj
```
Expected: 0 errors. (1 pre-existing warning about CS8602 in JournalJsonSerializer.cs is fine.)

- [ ] **Step 4: Manual verification**

1. Run the app on a tablet/device with journal mode.
2. Open a journal — scrollbar visible at right edge.
3. Horizontally pan to the journal column — scrollbar stays pinned at right edge of viewport.
4. Drag the scrollbar thumb — chapter markers appear to its left, also pinned.
5. Pan back to Bible text — scrollbar still at right edge, thumb position correct.

- [ ] **Step 5: Commit**

```
git add MyBibleApp/Views/MainView.axaml
git commit -m "fix: pin vertical scrollbar and chapter markers outside horizontal scroll container"
```
