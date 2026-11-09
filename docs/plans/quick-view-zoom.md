# Quick view zoom

Status: revision 1 (the plan), 2026-11-09. Branch `feat/multiple-libraries`, on top of `3c30055` (review-CLEAN).

Paul, 2026-11-09: "Before pushing & cutting a release, the previewer should follow a similar zoom in/out with
Ctrl+Scroll and Ctrl +/- as Photos and most image editors."

"The previewer" is **quick view** — the full-screen quick-look that `Space`, `Enter` or a double-click opens (the
README's "Quick view"; the tile menu's "Preview"). Today it decodes a photo at 1600 px on its longest side and shows it
fitted, with no zoom. The docked inspector pane already zooms with the wheel (with or without `Ctrl`) and its buttons,
and outside quick view `Ctrl` `+`/`−` and `Ctrl`+wheel keep zooming the grid's tiles: neither changes.

The keys follow the usual convention of Photos, browsers and image editors (`Ctrl`+wheel zooms at the pointer, `Ctrl`
`+`/`−` about the centre, `Ctrl`+`0` back to the fit). No Microsoft page listing the Photos app's shortcuts could be
found to check them against, so this is the convention, not a copy of a spec.

## Acceptance criteria

- **Z1** In quick view, `Ctrl`+wheel zooms in (wheel up) and out (wheel down), keeping the point of the photo under the
  pointer where it is. A plain wheel does nothing, as before.
- **Z2** `Ctrl`+`+` (the `=`/`+` key or the keypad's `+`) zooms in and `Ctrl`+`−` (either `−`) zooms out, about the
  centre of the view; `Ctrl`+`0` (either `0`) fits the photo again. Quick view's other keys (`Esc`, `Space`, `Enter`,
  `←`, `→`) work as before, with or without `Ctrl`.
- **Z3** The zoom runs from the fit (the photo as it opens; zooming out stops there) to 8 × the fit, in steps of 1.25 a
  wheel notch or key press. A touchpad's smaller wheel steps zoom by as much smaller a step.
- **Z4** Zoomed in, dragging the photo moves it. It never moves past its edges, and stays centred along a side on which
  it is smaller than the view.
- **Z5** A press beside the photo closes quick view, as before — beside the photo as it's shown, zoomed or not. A press
  on the photo doesn't (a double-click opens it, as before); on a zoomed photo it starts a drag.
- **Z6** Another photo (`←`/`→` or the buttons) opens at the fit, and so does quick view when it's opened again. At the
  first or last photo, `←` or `→` leaves the photo (and its zoom) as it is. Resizing the window keeps the zoom, with the
  point at the centre of the view where it was, within the photo's edges.
- **Z7** Zooming past the detail of the image quick view shows loads the photo again at full resolution (up to 8192 px
  on its longest side), once per photo, without moving the view. A photo shown smaller than the decode size is all there
  is — a small photo, or a video's frame (its 512 px thumbnail) — and isn't loaded again. A full-resolution load that
  finishes after the user has moved on is dropped.
- **Z8** Quick view's hint line names the zoom keys; the README and the CHANGELOG say what zoom does.

## Design

- **`ZoomPan`** (`src/Aperture.App/ZoomPan.cs`, beside `TileSpatialNavigation`: pure, no WPF types, so not covered by
  the window's exemption). It holds the view's size, the photo's shape, the zoom (1 = fit … 8) and the photo's left,
  top, width and height in the view. `SetView` / `SetImage` re-fit, keeping the zoom and the point of the photo at the
  centre of the view; `Clear` (no photo: nothing to zoom); `Reset` (the fit); `ZoomAt(factor, x, y)`; `ZoomByWheel`
  (1.25 ^ (delta / 120)); `ZoomIn` / `ZoomOut` about the centre; `PanBy`; `Contains(x, y)` (on the photo as shown);
  `ShowsMoreThan(pixelWidth, dpiScale)` (zoomed, and wider on screen than the image's pixels). Every move clamps: along
  each side the photo is centred if it's smaller than the view, otherwise it covers the view.
- **The window** (`MainWindow`, exempt as a shell; its wiring has shown-window tests). The quick-view `Image` keeps
  `Stretch="Uniform"`, centred, with a `ScaleTransform` and a `TranslateTransform` (the zoom, and the photo's offset from
  the fit), and its area clips. Entry points take their modifiers and points as arguments, so tests can call them:
  `QuickLookWheel(delta, at, modifiers)` (the overlay's wheel), `QuickLookKey(key, modifiers)` (quick view's keys, moved
  out of `OnWindowPreviewKeyDown`), `QuickLookPress` / `QuickLookDrag` / `QuickLookRelease` (the image area's mouse).
  `QuickLookImage` becoming null (another photo, or closing) clears the zoom; a new image sets the photo's shape (a
  sharper copy of the same photo changes nothing). After each change, if `ShowsMoreThan` the image's pixels, the window
  asks the view model for the photo in full. `ClickIsOnImage` (the fit's rectangle) gives way to `ZoomPan.Contains`.
- **The view model.** `LoadQuickLookDetail()` decodes the viewed photo at up to `QuickLookDetailEdge` (8192) px off the
  UI thread and shows it if quick view is still on that photo; once per visit to a photo, and not for an image smaller
  than `QuickLookDecodeEdge` (1600). Both sizes are settable for tests. `MoveQuickLook` leaves an unchanged index alone
  (no reload at the first or last photo).

## Test plan

AC → tests (`tests/Aperture.Core.Tests`):

| AC | Tests | Mutation each must kill |
|---|---|---|
| Z1 | `ZoomPanTests.ZoomAt_KeepsThePointUnderThePointer`; `QuickViewZoomTests.CtrlWheel_ZoomsAtThePointer_APlainWheelDoesnt` | zoom about the centre; the wheel zooms without `Ctrl` |
| Z2 | `QuickViewZoomTests.CtrlPlusMinusAndZero_Zoom_AndQuickViewsOtherKeysStillWork` | each key dropped from the map; `Ctrl` turning off `Esc`/`←`/`→` |
| Z3 | `ZoomPanTests.ZoomingOut_StopsAtTheFit`, `Zooming_StopsAtEightTimesTheFit`, `ZoomIn_And_ZoomOut_StepByAQuarter_AboutTheCentre`, `TheWheel_ZoomsInProportionToItsStep` | no floor; no ceiling; another step; the wheel's sign only |
| Z4 | `ZoomPanTests.Panning_StopsAtThePhotosEdges`, `ASideSmallerThanTheView_StaysCentred`; `QuickViewZoomTests.DraggingAZoomedPhoto_MovesIt` | no clamp; no centring; the drag not applied |
| Z5 | `ZoomPanTests.Contains_IsThePhotoAsShown`; `QuickViewZoomTests.APressBesideThePhoto_Closes_OnItDoesnt_ZoomedOrNot` | `Contains` on the fit; a press on a zoomed photo closes |
| Z6 | `ZoomPanTests.ANewView_KeepsTheZoomAndTheCentre`, `Clear_LeavesNothingToZoom`; `QuickViewZoomTests.AnotherPhoto_OpensAtTheFit`, `AtTheLastPhoto_RightLeavesItAlone` | `SetView` resets or doesn't re-anchor; no clear on a new photo; the same index reloaded |
| Z7 | `ZoomPanTests.ShowsMoreThan_OnlyZoomedAndWiderThanItsPixels`, `ASharperCopy_ChangesNothing`; `QuickViewZoomTests.ZoomingPastTheImage_LoadsThePhotoInFull_WithoutMovingTheView`; `QuickLookDetailTests` (the sizes' defaults; loaded in full once; capped at the detail size; loaded again on coming back; a small photo isn't; moving on drops a late load) | no detail asked; `IsZoomed` or the DPI ignored; the once-guard, the small-image guard, the staleness check or the reset on another photo removed; decoded at the decode size or uncapped |
| Z8 | `QuickViewZoomTests.TheHintLine_NamesTheZoomKeys`; README and CHANGELOG read in review | the hint unchanged |

Interface coverage: every `ZoomPan` member is called by name in `ZoomPanTests`; `ZoomPan.cs` reaches ≥ 85 % (expected
100 %). `MainViewModel.LoadQuickLookDetail` and `QuickLookDecodeEdge` by `QuickLookDetailTests`; the window's entry
points by `QuickViewZoomTests` (exempt file, tested anyway). Mutations run in a clone with the branch's runner
(`mutate32.py`, which adds these rows to `mutate31.py`).

Also in this layer: round 41's two carried nits (`IntervalGateTests`' comment says the old samples passed a clock at ten
times the speed, not a tenth; `IntervalGate.IsDue` reads `Now`, so the reading the tests check is the one it uses).

## Out of scope

`←`/`→` panning a zoomed photo (they keep moving between photos); double-click toggling the zoom (it keeps opening the
item); an actual-size key; keyboard zoom in the inspector pane; pinch gestures beyond what Windows sends as
`Ctrl`+wheel.
