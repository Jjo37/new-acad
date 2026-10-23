# Image / Scanned Drawing → CAD (baseline fixed 2026-09-22)

> Trigger: the user gives an image (screenshot / scan / freehand sketch / photo) and says "draw it into CAD / make a drawing from this".

## 0. Overarching principles (read these first — more important than the flow)

- **A hand-drawn sketch's scale carries no information**: freehand sketches vary wildly; only **text annotations** and the **rough shape** are meaningful.
  → So **do not trace pixels — copy the dimensions.**
- **Coordinates come from only three sources**: dimension annotations on the drawing / dimensions the user states / a target overall size the user gives.
- **Pixel geometry is used only** to recognise shape and topology (what connects to what, where openings are, which line matches which annotation).
- **If you can't read it accurately, flag it and ask — never guess.** A guessed "professional drawing" is worse than a traced one.

## 1. Triage (decide first, then act; don't skip)

| What's on the image | How to draw | What it becomes |
|---|---|---|
| **Has dimension annotations** | Route B: annotation-driven reconstruction + dimension-chain closure check | A true-dimension, constructible drawing |
| **No annotations, but scale clues** (title-block scale 1:100, grid, coordinate labels) | Calibrate from the clue → rebuild at true size | Constructible (slightly degraded) |
| **No dimensions at all** | **Ask once** (see §4) → per the user's choice: proportionally / single-point calibration / target overall size | Proportional or schematic drawing |
| **Not an engineering drawing** (photo, real object, illustration, schematic) | Trace the shape (traceImage), or scale to a user-given target size | Schematic — **say so honestly**, don't pretend it's a construction drawing |

## 2. Route B: Annotation-driven reconstruction (standard five steps)

1. **Read the image and build a dimension table** (don't draw yet!)
   - Output a structured `dimension-table` (save it to the workspace for review):
     ```json
     {
       "unit": "mm",
       "scaleHint": null,
       "walls": [{"id": 1, "from": "A", "to": "B", "length": 3600, "label": "3600"}],
       "total": {"x": 8400, "y": 5400},
       "openings": [{"onWall": 3, "width": 900, "offsetFromStart": 1200, "kind": "door"}],
       "uncertain": [{"field": "walls[4].length", "seen": ["1200", "1800"], "why": "handwritten 2/8 hard to distinguish"}]
     }
     ```
2. **Dimension-chain closure check**: sum of segments == total; if not closed → report an error and point out which segments conflict (the most common defect in hand sketches)
3. **Ask about uncertain items**: list `uncertain` to the user and confirm before drawing (**ask only about the genuinely uncertain ones — don't dump the whole table back for re-entry**)
4. **Rebuild from the table** (anchor point + relative dimensions is the most robust style)
   - Example: origin at the lower-left → `createLineSegment (0,0)-(3600,0)` for the exterior bottom wall; `createPolyline` for a continuous outline; arcs via `bulges`; openings by `offsetEntity` to locate then `createLineSegment` to break the gap
   - You may also draw the annotations: `createText` / `dimLinear` / `dimAligned`
5. **Self-check (mandatory)**
   - Read back: `getEntitiesByLayer` + endpoint coordinates, measure the key segments, reconcile against the dimension table
   - Topology comparison: against the "shape skeleton" — is the closed loop closed, are perpendicular edges perpendicular, parallel edges parallel, do wall-opening counts and positions match
   - Pass only when "shape relationships correct + dimensions from annotations"

## 3. Three safety nets (all required)

| Safety net | How | Blocks |
|---|---|---|
| Dual-channel cross-check | Read once with a vision model + once with OCR, compare item by item | Misread handwritten digits |
| Dimension-chain closure | Sum of segments vs total | Self-contradictory drawing / reading error |
| Uncertain items surfaced | The model must output `uncertain[]`, no guessing | Silent wrong guesses (the most dangerous) |

## 4. Fallback for dimension-less images (**ask only once, don't repeat**)

Ask: **"Is there any length in the drawing that you know? Even one."** Offer three defaults:

1. **Draw proportionally** (default) — shape accurate, true size unknown; state this clearly to the user
2. **Give one reference dimension** (single-point calibration): the user reports one length → that segment's pixel length → mm/px → **scale the whole drawing to real units** (`scaleEntities`, or multiply by the factor while drawing)
3. **Give a target overall size** (e.g. "make it 10 m wide") → scale proportionally to the target

**A counter-intuitive point (remember)**: with no dimensions, "shape" is the only usable information → here "de-jitter / clean up the linework" is *more* valuable (be faithful to the shape) — complementary to Route B's usage.

## 5. Available tools (already in the plugin, verified 2026-09-22)

- Draw: `createLineSegment` / `createPolyline` (`bulges` = arcs) / `createCircle` / `createText` / `createMText` / `addLabel`
- Dimensions: `dimLinear` / `dimAligned` / `dimRadius` / `dimAngular` / `dimDiameter`
- Edit: `offsetEntity` / `trimEntity` / `extendEntity` / `filletEntities` / `joinEntities` / `moveEntities` / `scaleEntities` / `rotateEntities` / `mirrorEntities` / `copyEntities` / `breakEntity` / `stretchEntity` / `overkillEntities`
- Layers / read-back: `createLayer` / `setLayerColor` / `getEntitiesByLayer` / `getEntityInfo`
- Shape tracing (Route A / schematic only): `traceImage` (omit `mode` = auto-detect; to place it directly use `draw:true`)

## 6. To-do (not urgent, as needed)

- `prepSketch`: de-jitter/clean the drawing + **numbered overlay** (draw detected segment numbers onto the image so the model can say "no. 3 = exterior wall 3600" — reduces mis-indexing) — **auxiliary reading aid only, not used for coordinates**
- Dual-channel OCR for handwritten digits
- Automatic recognition of title-block scale / grid
