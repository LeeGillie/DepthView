# Design notes

How the surface description turns into something a laser can run. Terms used here:

- **Band:** a straight strip, used when the part curves in one direction.
- **Zone:** a region of any shape, used when it doesn't.
- **Tolerance:** the largest height change allowed inside one band or zone. Focus sits at
  the middle, so the worst focus error is half the tolerance.

---

## 1. Tolerance comes from a test, not a guess

Every process tolerates defocus differently. The tolerance should be *measured* for the
process and then typed in:

- Mark one small swatch at focus offsets 0, ±0.25, ±0.5, ±0.75 and ±1.0 mm on flat scrap of
  the real material, with the real settings.
- The largest offset whose result still matches the in-focus swatch is the half-tolerance.

Deep engraving will usually allow a large band. Color marking will not. A generator for this
test pattern is roadmap item A3. No tolerance values are given here, because none have been
measured yet.

## 2. One-direction banding (in the prototype)

1. **Fit a curve** through the measured points using monotone cubic (PCHIP) interpolation.
   It never overshoots between points, so it can't invent a bump that isn't there.
2. **Sample it finely** (0.02 mm) across the artwork.
3. **Grow bands greedily:** from the left edge, extend a band while the height change inside
   it stays within tolerance. A narrowest-band setting stops slivers on steep ground, and a
   leftover sliver at the end is folded into its neighbour.
4. **Set each band's focus** at the midpoint of its height range, and report the offset from
   the highest point of the whole artwork. The operator focuses on the high point once, then
   steps down.
5. **Unwrap, optionally.** The artwork is laid out along the *surface* (arc length), not the
   flat projection, so a 60 mm design is 60 mm measured over the curve. Inside each band the
   art is compressed by that band's projection factor. This is piecewise linear and exact at
   every band boundary.
6. **Cut the artwork:**
   - Filled vector shapes are clipped with boolean intersection.
   - Stroked lines are flattened to polylines and clipped exactly. Boolean-clipping open
     strokes proved unreliable in testing and leaked outside the band.
   - Bitmaps are resampled per band into full-size transparent PNGs, so every band file
     shares one origin.

Output files: one per band, a combined SVG with one color per band (so LightBurn creates one
layer per band), `focus-plan.csv`, and a README.

## 3. Two-direction zoning (planned)

The same idea on a height map (a grid from Level 2, or a scan from Level 3):

1. **Slice the height range into levels** one tolerance tall. Each level becomes a zone,
   focused at its middle.
2. **Clean up the zone masks.** Smooth the edges, drop specks, and merge zones below a
   minimum area into a neighbour. Otherwise a noisy scan produces confetti.
3. **Prefer seams where there's no artwork.** Zone boundaries follow height contours, but
   they can shift within the tolerance slack so seams land in gaps in the design.
4. **Cut the artwork to each zone.** Bitmaps use the zone mask. Vectors get the zone outline
   traced (marching squares) and then clipped by boolean intersection.
5. **Mask what can't be marked:** slopes above a set limit, holes, and anything outside the
   scan.

**Unwrapping is harder in 2D.** A dome can't be flattened without distortion, unlike a
cylinder. The first version should project straight down and show a *stretch map* of where
the art will be distorted. A true surface layout (as in texture mapping) can come later if
parts need it.

## 4. What the galvo optics do

A galvo's beam fans out from the scanning mirrors rather than coming straight down. For a
mirror-to-focal-plane distance *L*, a surface *dz* above the focal plane gets a point meant
for radius *r* at roughly:

```
r_actual ≈ r · (L − dz) / L
```

LightBurn's Cylinder Correction asks for this same "mirror distance" for the same reason.

**The zone approach mostly avoids this:**

- **Moving Z to focus on a zone moves the whole head,** so the zone surface sits in the
  focal plane and the geometry is correct there. Only the ±half-tolerance within the zone
  remains.
- **Running a curved part at one focus height does not avoid it.** There, parts of the art
  far from centre shift. Keep such parts near the centre of the field.

*L* for a given lens can be measured by marking the same square at two Z heights and
comparing sizes. That's worth doing once per lens before trusting any single-focus approach.

## 5. Tilt

Even in focus, a surface tilted by θ receives a spot stretched by 1/cos θ, with energy per
area reduced by the same factor.

| Tilt | Spot stretch |
|---|---|
| 10° | 1.5% |
| 20° | 6.4% |
| 30° | 15.5% |
| 40° | 30.5% |

Options, roughly in order of effort:

1. **Warn** (the prototype flags tilts over 20°).
2. **Raise power per zone by 1/cos θ.** Laser processes aren't linear, so this is a starting
   point, not a law.
3. **Measure it:** mark swatches on a printed wedge at 0/10/20/30° and tune each zone's
   settings from the results.
4. **Remove the tilt** with a rotary (section 6).

## 6. Rotary indexing

A rotary can remove tilt, not just height: turn the part so each zone faces the beam, then
set focus for that zone.

- **Where it's worth it:** color marking, the steep edges of a flask, and anything past about
  20° of tilt.
- **Where it isn't:** gentle buckle crowns. The tilt is small, and the part would have to be
  mounted at the centre of a large radius.
- **Geometry:** for a part rotating about an axis, each zone gets an angle (its surface
  normal turned to vertical) and a height (its distance from the axis at that angle).
- **Practical limits:**
  - The Lumos rotary takes parts up to 100 mm in diameter.
  - Off-axis parts need a counterweighted arm.
  - Every index adds registration error (section 3 of SURFACE-INPUT.md applies).

## 7. Getting it onto the machine

**Level A: separate files (works today).** One file per zone and a focus plan. The operator
refocuses between zones. Nothing custom touches the machine. The combined color SVG lets
LightBurn users put each zone on its own layer.

**Level B: one job.** A single G-code file with a focus move before each zone, plus a rotary
move if indexing. Two things decide whether this is possible:

1. **Is there a supported way to run user-generated G-code on the Lumos Ultra?** Unknown.
   This is a question for the manufacturer and won't be worked around.
2. **What is the dialect?** MakeIt! stages the job it is about to send as a plain G-code
   file on the PC, and a focus height appears in it as a `G0 Z` move. How rotary rotation is
   encoded is not yet known. Capturing one small MakeIt! rotary job would settle it.

Any generated job must be:

- dry-run at zero power and with the framing light first, and
- compared against a MakeIt!-generated file for the same simple artwork.

This is a 100 W source.

## Open questions

- Does MakeIt! allow a different focus height per layer in a normal surface job? If so,
  Level A becomes a single import there too.
- What is the Lumos Ultra MOPA lens's mirror distance *L*? (Measure it: section 4.)
- How repeatable is autofocus probing on bare polished brass and stainless?
- What tolerance does MOPA color marking on 304 stainless actually allow? (Section 1 test.)
- Is Level 1p (width and crown) accurate enough for real buckle blanks, compared against a
  measured profile?
