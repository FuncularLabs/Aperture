# Quick view zoom

Status: revision 4 (after review round Z3), 2026-11-09. Branch `feat/multiple-libraries`, on top of `3c30055`
(review-CLEAN); the feature is `ede48ca`, round Z1's fixes `8e76bc2`, round Z2's `be1d7b5`, round Z3's the commit
after it.

Paul, 2026-11-09: "Before pushing & cutting a release, the previewer should follow a similar zoom in/out with
Ctrl+Scroll and Ctrl +/- as Photos and most image editors."

"The previewer" is **quick view** — the full-screen quick-look that `Space`, `Enter` or a double-click opens (the
README's "Quick view"; the tile menu's "Preview"). Before this it decoded a photo at 1600 px on its longest side and
showed it fitted, with no zoom. The docked inspector pane already zooms with the wheel (with or without `Ctrl`) and its
buttons, and outside quick view `Ctrl` `+`/`−` and `Ctrl`+wheel keep zooming the grid's tiles: neither changes.

The keys follow the usual convention of Photos, browsers and image editors (`Ctrl`+wheel zooms at the pointer, `Ctrl`
`+`/`−` about the centre, `Ctrl`+`0` back to the fit). No Microsoft page listing the Photos app's shortcuts could be
found to check them against, so this is the convention, not a copy of a spec.

## Acceptance criteria

- **Z1** In quick view, `Ctrl`+wheel zooms in (wheel up) and out (wheel down), keeping the point of the photo under the
  pointer where it is (as far as the photo's edges allow, Z4). A plain wheel does nothing, as before.
- **Z2** `Ctrl`+`+` (the `=`/`+` key or the keypad's `+`) zooms in and `Ctrl`+`−` (either `−`) zooms out, about the
  centre of the view; `Ctrl`+`0` (either `0`) fits the photo again. Quick view's other keys (`Esc`, `Space`, `Enter`,
  `←`, `→`) work as before, with or without `Ctrl`; with quick view closed, the grid's keys are as before.
- **Z3** The zoom runs from the fit (the photo as it opens; zooming out stops there) to 8 × the fit, in steps of 1.25 a
  wheel notch or key press. A touchpad's smaller wheel steps zoom by as much smaller a step, and steps in and back out
  end at the fit.
- **Z4** Zoomed in, dragging the photo moves it until the button is released (or the drag is lost, as to Alt+Tab).
  It never moves past its edges, and stays centred along a side on which
  it is smaller than the view. The drag cursor shows over a zoomed photo, and only there.
- **Z5** A press beside the photo closes quick view, as before — beside the photo as it's shown, zoomed or not. A press
  on the photo doesn't (a double-click opens it, as before); on a zoomed photo it starts a drag.
- **Z6** Another photo (`←`/`→` or the buttons) opens at the fit, and so does quick view when it's opened again, or
  after a library switch. At the first or last photo, `←` or `→` leaves the photo (and its zoom) as it is. Resizing the
  window keeps the zoom, with the point at the centre of the view where it was, within the photo's edges.
- **Z7** Zooming past the detail of the image quick view shows loads the photo shown again at full resolution (up to
  8192 px on its longest side) — the photo shown, even if the view has been rebuilt since — once per visit to it,
  without moving the view. A photo shown smaller than the decode size is all there is — a small photo, or a video's
  frame (its 512 px thumbnail) — and isn't loaded again. A load that finishes after the user has moved on or closed
  quick view is dropped; a full load that fails leaves the photo as shown.
- **Z8** Quick view's hint line names the zoom keys; the README and the CHANGELOG say what zoom does.

## Design

- **`ZoomPan`** (`src/Aperture.App/ZoomPan.cs`, beside `TileSpatialNavigation`: pure, no WPF types, so not covered by
  the window's exemption). It holds the view's size, the photo's shape, the zoom (1 = fit … 8) and the photo's left,
  top, width and height in the view. `SetView` / `SetImage` re-fit, keeping the zoom and the point of the photo at the
  centre of the view; `Clear` (no photo: nothing to zoom); `Reset` (the fit); `ZoomAt(factor, x, y)` (a zoom within
  1e-9 of the fit is the fit); `ZoomByWheel` (1.25 ^ (delta / 120)); `ZoomIn` / `ZoomOut` about the centre; `PanBy`;
  `Contains(x, y)` (on the photo as shown); `ShowsMoreThan(pixelWidth, dpiScale)` (zoomed, and wider on screen than the
  image's pixels). Every move clamps: along each side the photo is centred if it's smaller than the view, otherwise it
  covers the view.
- **The window** (`MainWindow`, exempt as a shell). The quick-view `Image` keeps `Stretch="Uniform"`, centred and not
  rounded to whole pixels, with a `ScaleTransform` and a `TranslateTransform` (the zoom, and the photo's offset from the
  fit), and its area clips. Entry points take their modifiers and points as arguments: `QuickLookWheel(delta, at,
  modifiers)` (the overlay's wheel), `QuickLookKey(key, modifiers)` (quick view's keys, moved out of
  `OnWindowPreviewKeyDown`), `QuickLookPress` / `QuickLookMove` / `QuickLookRelease` (the image area's mouse). The
  handlers read the modifier keys through `ModifiersNow` and the pointer through `PointerIn(element)`, which tests set
  (round Z1), so the handlers themselves are tested by raising the events. `QuickLookImage` becoming null (another
  photo, or closing) clears the zoom, and so does a new view model; a new image sets the photo's shape (a sharper copy
  of the same photo changes nothing). After each change, if `ShowsMoreThan` the image's pixels, the window asks the
  view model for the photo in full. The drag cursor shows when the zoomed photo is under the pointer.
  `ClickIsOnImage` (the fit's rectangle) gave way to `ZoomPan.Contains`.
- **The view model.** Each photo quick view shows is a visit (`_quickLookVisit`, counted on each photo shown and each
  close), and quick view keeps the tile it shows (`_quickLookTile`). `LoadQuickLookDetail()` decodes that tile at up to
  `QuickLookDetailEdge` (8192) px off the UI thread, once per visit, not for an image smaller than `QuickLookDecodeEdge`
  (1600); a load (cut down or in full) is kept only if quick view is still on the visit it was started for, and a
  failed one changes nothing. The decoder (`DecodeQuickLook`) and both sizes are settable for tests. `MoveQuickLook`
  leaves an unchanged index alone (no reload at the first or last photo).

## Test plan

AC → tests (`tests/Aperture.Core.Tests`; QVZ = `QuickViewZoomTests`, QLD = `QuickLookDetailTests`):

| AC | Tests | Mutation each must kill |
|---|---|---|
| Z1 | `ZoomPanTests.ZoomAt_KeepsThePointUnderThePointer`; QVZ `CtrlWheel_ZoomsAtThePointer_APlainWheelDoesnt`, `TheWindowsOwnHandlers_ZoomAndDrag_AsTheMouseAndKeyboardSendThem` | zoom about the centre; the wheel zooms without `Ctrl`; the handler's `Ctrl` or pointer frame |
| Z2 | QVZ `CtrlPlusMinusAndZero_Zoom_AndQuickViewsOtherKeysStillWork`, `TheWindowsOwnHandlers_ZoomAndDrag_AsTheMouseAndKeyboardSendThem`, `TheGridsKeys_WithQuickViewClosed_AreTheGridsAsBefore`, `CtrlPlusAndMinus_ZoomAboutTheCentre`; `ZoomPanTests.Reset_FitsThePhotoAgain` | each key dropped from the map; `Ctrl` turning off `Esc`/`←`/`→`; `Space` not closing; `Enter` not opening; `Ctrl` ± about the corner; the handler's `Ctrl`; quick view's keys while it's closed |
| Z3 | `ZoomPanTests.APhoto_OpensFitted_AndCentred`, `ZoomingOut_StopsAtTheFit`, `Zooming_StopsAtEightTimesTheFit`, `ZoomIn_And_ZoomOut_StepByAQuarter_AboutTheCentre`, `TheWheel_ZoomsInProportionToItsStep`, `ATouchpadsStepsInAndBackOut_EndAtTheFit`, `ASmallStepFromTheFit_Zooms` | no floor; no ceiling; another step; the wheel's sign only; no snap to the fit; the snap too wide |
| Z4 | `ZoomPanTests.Panning_StopsAtThePhotosEdges`, `ASideSmallerThanTheView_StaysCentred`, `APanorama_FillsTheWidth_CentredDown`; QVZ `DraggingAZoomedPhoto_MovesIt_UntilReleased`, `TheWindowsOwnHandlers_ZoomAndDrag_AsTheMouseAndKeyboardSendThem`, `TheDragCursor_ShowsOnlyOverAZoomedPhoto` | no clamp; no centring; the drag not applied, from the press, or not ended (by the button or a lost capture); the mouse handlers not wired; the cursor beside the photo, over a fitted photo, or not following the pointer |
| Z5 | `ZoomPanTests.Contains_IsThePhotoAsShown`; QVZ `APressBesideThePhoto_Closes_BesideItAsShown`, `APressBesideThePhoto_ThroughTheWindowsHandler_Closes`, `APressOnAFittedPhoto_DoesntClose`, `TheWindowsOwnHandlers_ZoomAndDrag_AsTheMouseAndKeyboardSendThem` (a double-click) | `Contains` on the fit; a press on a zoomed photo closes; a press beside doesn't; the press in the overlay's frame; a double-click not opening |
| Z6 | `ZoomPanTests.ANewView_KeepsTheZoomAndTheCentre`, `Clear_LeavesNothingToZoom_UntilAPhotoArrives`, `APhotoBeforeItsView_IsFittedOnceTheViewIsKnown`; QVZ `AnotherPhoto_OpensAtTheFit_AndSoDoesQuickViewOpenedAgain`, `AtTheLastPhoto_RightLeavesItsZoomAlone`, `ResizingTheWindow_KeepsTheZoom_AndTheDrawingFollows`, `ANewViewModel_StartsWithNothingZoomed`; QLD `AtTheLastPhoto_NextLeavesItAsItIs` | `SetView` resets or doesn't re-anchor; `Clear` keeps the shape; no clear on a new photo or view model; the same index reloaded; the area's size not passed |
| Z7 | `ZoomPanTests.ShowsMoreThan_OnlyZoomedAndWiderThanItsPixels`, `ASharperCopy_ChangesNothing`; QVZ `ZoomingPastTheImage_LoadsThePhotoInFull_WithoutMovingTheView`, `TheFullLoad_IsAskedForOnceThePhotoIsShownWiderThanItsPixels`; QLD `TheDecodeSize_IsSixteenHundredPixels_ByDefault`, `APhotoCutDownToTheDecodeSize_IsLoadedInFull_Once`, `APhotoLoadedInFull_IsCappedAtTheDetailSize`, `ComingBackToAPhoto_LoadsItInFullAgain`, `APhotoSmallerThanTheDecodeSize_IsAllThereIs_AndIsntLoadedAgain`, `AFullLoadFinishingAfterTheUserMovedOn_IsDropped`, `ACutDownLoadFinishingAfterTheUserMovedOn_IsDropped`, `AZoomAfterTheViewIsRebuilt_LoadsThePhotoShownInFull`, `AFullLoadFromBeforeQuickViewClosed_IsDropped_EvenAtTheSamePlace`, `ALoadFinishingAfterQuickViewClosed_IsDropped`, `AFailedFullLoad_LeavesThePhotoShown` | no detail asked; asked at any zoom, past 4 × the pixels, or by the height; `IsZoomed` or the DPI ignored; the once-guard, the small-image guard or a visit check removed; the photo at quick view's old place; a cut-down load kept after moving on; closing not ending the visit; a failed load shown; decoded at the decode size or uncapped; the sizes' defaults |
| Z8 | QVZ `TheHintLine_NamesTheZoomKeys` (the whole line); README and CHANGELOG read in review | the hint unchanged or cut |
| Drawing | QVZ `ThePhoto_IsDrawnWhereTheZoomSays_FittedAndZoomed` and the drawing check in five more (to 1e-6) | the shift or the scale wrong; laid out on whole pixels |

Interface coverage: every `ZoomPan` member is called by name in `ZoomPanTests`, which alone reach both ways of fitting
since round Z1; `ZoomPan.cs` is at 100 %. The view model's and the window's members by QLD and QVZ (exempt files, tested
anyway). Mutations run in a clone with the branch's runner (`mutate35.py`).

**Checked by hand on the trial build** (no test can): the mouse capture during a drag (an inactive, off-screen test
window gets none — W6), the DPI the window passes on a screen above 100 % (W9; this machine and CI run at 100 %), and
how zoom, drag and the full-resolution load feel with a real mouse, touchpad and a large photo; and the two seams'
defaults, read rather than tested since no test can hold Ctrl or move the mouse (`ModifiersNow` is
`Keyboard.Modifiers`, `PointerIn` is `Mouse.GetPosition`: the same device `e.GetPosition` reads, capture or not;
`OpenViewed` is the view model's open command, as before this feature).

Also in this layer: round 41's two carried nits (`IntervalGateTests`' comment says the old samples passed a clock at ten
times the speed, not a tenth; `IntervalGate.IsDue` reads `Now`, so the reading the tests check is the one it uses).

## Out of scope

`←`/`→` panning a zoomed photo (they keep moving between photos); double-click toggling the zoom (it keeps opening the
item); an actual-size key; keyboard zoom in the inspector pane; pinch gestures beyond what Windows sends as
`Ctrl`+wheel; re-anchoring quick view's place in the list after a rebuild (its caption's count and `←`/`→` follow the
old place until the next photo, as before this feature).

## Revision 2 — what changed after review round Z1

Two non-author lenses over `3c30055..ede48ca`: behaviour (Z1A) and proof (Z1B). The author's 43 mutation rows all
killed.

| # | Finding | Blame | Change | Tests | Mutation each must kill |
|---|---|---|---|---|---|
| Z1A-1 Medium | Zooming after the view was rebuilt (an index run, the watcher, the launch's reconcile) loaded the photo now at quick view's old place in the list — another photo — in full, under the shown photo's caption. | TEST-GAP | Quick view keeps the tile it shows; the full load decodes it | QLD `AZoomAfterTheViewIsRebuilt_LoadsThePhotoShownInFull` (the lens's probe) | the photo at quick view's old place |
| Z1A-2 Low | A full load from before quick view closed could land on the photo now at the same place after reopening, once a new full load was asked for. | TEST-GAP | Loads are kept per visit (each photo shown, each close), not per place | QLD `AFullLoadFromBeforeQuickViewClosed_IsDropped_EvenAtTheSamePlace` (held by the decoder seam) | a full load kept from an earlier visit |
| Z1A-3 Low | Over a zoomed photo still letterboxed, the drag cursor showed beside it too, where a press closes quick view. | AC-GAP | Z4 amended; the drag cursor shows only with the zoomed photo under the pointer | QVZ `TheDragCursor_ShowsOnlyOverAZoomedPhoto` | the cursor beside the photo; over a fitted one; not following a move or the wheel |
| Z1A-4 Nit | A touchpad's steps in and back out could end at 1.0000000000000002: zoomed, by a hair, at the fit. | OTHER (floating point) | Z3 amended; a zoom within 1e-9 of the fit is the fit | `ZoomPanTests.ATouchpadsStepsInAndBackOut_EndAtTheFit` | no snap |
| Z1B-1 Low | The window's own handlers had no test that could fail: the wheel's hook-up, the `Ctrl` and pointer they read, the mouse's hook-ups; the plan said the wiring had shown-window tests. Also (before this feature) quick view's keys running while it's closed survived every test. | TEST-GAP | The handlers read `ModifiersNow` and `PointerIn`; capture and DPI are listed as checked by hand | QVZ `TheWindowsOwnHandlers_ZoomAndDrag_AsTheMouseAndKeyboardSendThem`, `APressBesideThePhoto_ThroughTheWindowsHandler_Closes`, `TheGridsKeys_WithQuickViewClosed_AreTheGridsAsBefore` | the wheel unhooked; `Ctrl` not read; the pointer in the overlay's frame; move or button-up unhooked; quick view's keys while closed |
| Z1B-2 Low | The test table named tests that don't exist, or by a prefix, and left out tests that prove things. | PLAN-GAP | The table, above | — | — |
| Z1B-3 Low | A load finishing after quick view closed was untested. | TEST-GAP | — | QLD `ALoadFinishingAfterQuickViewClosed_IsDropped` | closing doesn't end the visit; a photo's load kept after its visit |
| Z1B-4 Low | A failed full load (a file gone, a share offline) leaving the photo shown was neither required nor tested. | AC-GAP | Z7 amended | QLD `AFailedFullLoad_LeavesThePhotoShown` | a failed load shown |

Nits taken: the drawing check is to 1e-6 (the photo isn't rounded to pixels); the late-load test waits for the next
photo among the images shown, so the stale load fails at its own assertion; `APressOnAFittedPhoto_DoesntClose` (its
"nor drags" half couldn't fail: at the fit nothing moves); a panorama in `ZoomPanTests`; Z7 says once per visit; the
hint test checks the whole line; a new view model clears the zoom, tested; a comment's wording; the status line; the
table names every test in full (Z1A-5, the same as Z1B-2). Not taken: releasing a drag when the photo changes stays
untested (a drag on a photo that has just been fitted moves nothing).

## Revision 3 — what changed after review round Z2

One non-author lens over `ede48ca..8e76bc2`: every round-Z1 finding verified fixed but Z1A-3 (partial); no behaviour
defect; three tests short of a claim, and nits.

| # | Finding | Blame | Change | Tests | Mutation each must kill |
|---|---|---|---|---|---|
| Z2-1 Low | The cursor test's wheel "over the photo, no move first" came after a move to a point the zoom then grew the photo over, so a wheel not noting the pointer passed it (the author's row survived too). | TEST-GAP | — | `TheDragCursor_ShowsOnlyOverAZoomedPhoto`: the move before that wheel is to the area's edge, which the photo doesn't reach | the wheel doesn't note the pointer |
| Z2-2 Low | The snap to the fit's width was untested: a snap of 1.2, or 1.01, swallowing a touchpad's small steps from the fit, survived. | TEST-GAP | — | `ZoomPanTests.ASmallStepFromTheFit_Zooms` (1/120 of a notch zooms in) | the snap too wide |
| Z2-3 Low | A cut-down load finishing after the user had moved on was untested (only the full load's was): guarding it on quick view being open alone survived. | TEST-GAP | — | QLD `ACutDownLoadFinishingAfterTheUserMovedOn_IsDropped` (p0's load held by the decoder seam until p1 shows) | a cut-down load kept after moving on |

Nits taken: the hand-check list names the seams' defaults; `ModifiersNow`'s summary names the handlers that read it
(the grid's own key and wheel handlers still read `Keyboard.Modifiers`); the table names every test in full (two rows
had a prefix) and the fit's test; the held-load tests wait for the held decode to come back before checking, and the
reopened photo is told from p0 by its height. Not taken: the cursor follows the last point the pointer was seen at
until it moves again (WPF's own moves after a layout change correct it; cosmetic). Record: 8e76bc2's message counts 66
rows for quick-view zoom — 65, and R41's one.

## Revision 4 — what changed after review round Z3

One non-author lens over `8e76bc2..be1d7b5` and a final sweep of the feature: round Z2's three verified fixed (the
whole runner's 68 quick-view rows killed); no behaviour defect; five behaviours of the feature no test pinned.

| # | Finding | Blame | Change | Tests | Mutation each must kill |
|---|---|---|---|---|---|
| Z3-1 Low | `Space` closing quick view (with or without `Ctrl`, Z2) was untested. | TEST-GAP | — | QVZ `CtrlPlusMinusAndZero_Zoom_AndQuickViewsOtherKeysStillWork` (`Space`, both ways) | `Space` doesn't close |
| Z3-2 Low | `Enter` and a double-click opening the item (Z2, Z5), both moved by this feature, were untested: they launch its default app. | TEST-GAP | Both call the window's `OpenViewed` (the view model's open command; settable for tests) | The keys test (`Enter`, both ways); `TheWindowsOwnHandlers_…` (a real double-click: `ClickCount` 2) | `Enter` doesn't open; a double-click doesn't |
| Z3-3 Low | `Ctrl` ± zooming about the centre was pinned only in the model: the window's map zooming about the corner survived for `Ctrl`+`−`. | TEST-GAP | — | QVZ `CtrlPlusAndMinus_ZoomAboutTheCentre` | `Ctrl`+`−` or `Ctrl`+`+` about the corner |
| Z3-4 Low | The window's threshold for the full load was untested: the one test zoomed 33 × past the pixels, so asking only past 4 ×, by the height, or at any zoom survived. | TEST-GAP | — | QVZ `TheFullLoad_IsAskedForOnceThePhotoIsShownWiderThanItsPixels` (cut down to 15 % wider than its fit: none after a fifth of a notch, one after a notch) | asked past 4 × the pixels; by the height; at any zoom |
| Z3-5 Low | A lost capture (Alt+Tab mid-drag) ending the drag was untested. | AC-GAP | Z4 says a drag ends with the button or a lost capture | `TheWindowsOwnHandlers_…` (`LostMouseCapture` raised mid-drag) | the lost capture unhooked |

Nit taken: the snap test's comment and be1d7b5's message overclaimed ("a rounding error wide"): the test pins the snap
narrower than a touchpad's smallest step, which is what Z3 needs.
