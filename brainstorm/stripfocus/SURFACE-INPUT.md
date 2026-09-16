# Describing the surface to the software

The planner can only be as good as its picture of the part. There are several ways to
build that picture, from a few typed-in numbers to a full 3D scan, and each suits different
parts. They all end in the same place: **a height for any (x, y) under the artwork, in
machine coordinates.**

| Level | Input | Good for | Effort |
|---|---|---|---|
| 0 | Two heights: highest and lowest point | Gentle curves, deep engraving | Seconds |
| 1 | A profile: 5–10 heights along one line | Buckles, flask faces (curved one way) | Minutes |
| 1p | Shape numbers: width and crown height | Blanks that are close to a circular arc | Seconds |
| 2 | A grid of heights | Domes, compound curves | 10–30 minutes |
| 3 | A 3D scan | Irregular parts, repeat production | An hour to set up, then quick |

---

## Level 0: two numbers

Focus halfway between the highest and lowest points under the art. The worst error is half
the difference. If that's inside your process tolerance, you're done and no planner is
needed.

## Level 1: a profile along one line (what the prototype uses)

Most buckles and flask faces curve in only one direction. One line of measurements across
the curve describes the whole part.

**How to take it:**

1. Fixture the part so it can't rock, with the curve running along X or Y.
2. Put a strip of masking tape across the part and mark positions from a fixed edge. Space
   them every 5–10 mm, closer where the surface is steepest. **Always include both ends of
   the artwork area.**
3. Measure the height at each mark, using one of:
   - **The laser's own autofocus:** move to each mark, autofocus, and read the Z value.
     Approach each focus from the same direction every time; mechanical backlash can make
     readings from opposite directions disagree by around a tenth of a millimetre. If the
     readout rises as the surface gets *lower*, choose "Higher = lower surface" in the tool.
   - **A height gauge or caliper depth rod,** from a flat reference such as the bed or a
     surface plate.
   - **A dial indicator** on a stand, sliding the part along a fence.
   - **A contour (profile) gauge** pressed across the part, then photographed flat next to a
     ruler and traced. This is quick and needs no measuring tool at all, at about ±0.2 mm.
4. Type them in as `position, height`, one per line.

The tool fits a smooth curve through the points that never overshoots between them, so a
wrong reading shows up as a visible kink in the profile chart rather than hiding.

## Level 1p: shape numbers (planned)

Many blanks are close to a circular arc. Across a chord of width *c* with a crown (sag) of
*s*, the radius is:

```
R = (c²/4 + s²) / (2·s)
```

Width and crown can be measured with calipers in seconds, and the profile then follows.
Other shapes could be added the same way, for example an ellipse (two radii) or a flat
centre with rounded edges (flat width plus edge radius). Measured points and shape numbers
should also be combinable, so a shape can be corrected by a few real readings.

## Level 2: a grid

A domed part curves in both directions, so one line isn't enough. Take a grid of readings:
5×5 covers a small dome, and 7×9 an oval flask face. Autofocus probing at 25–60 points is
tedious by hand. A fixed probing sequence (a printed grid template, with the Z value written
next to each cell) keeps it manageable. If the machine can ever be scripted, this is the
first thing to automate.

The grid is interpolated into a surface the same way as the profile, in two dimensions.

## Level 3: a 3D scan

This is where the idea stops being about smooth curves. A scanner such as a **Revopoint
MetroX** captures the actual part, including engraved logos, raised rims, bevels and dents.

**About that scanner** (from Revopoint's and reviewers' published figures; not yet verified
here):

- Single-frame precision up to 0.01 mm, accuracy up to 0.03 mm.
- Exports PLY, OBJ, STL, ASC, 3MF, GLTF and FBX.
- Handles "shiny metallic objects (not mirror shiny)" in its cross-line laser mode.
  Mirror-polished brass or stainless will need a vanishing scanning spray. The coat is
  microns thick, which is negligible against a focus tolerance measured in tenths.

Sources: [Revopoint MetroX](https://www.revopoint3d.com/products/3d-laser-scanner-metrox),
[3DWithUs review](https://3dwithus.com/revopoint-metrox-review-3d-scanner-testing-tips-and-settings)

### From scan to height map

1. **Scan the part in the jig it will be marked in**, including part of the jig. The jig
   is what ties the scan to the machine.
2. **Level it.** Fit a plane to points on the jig's top or the bed, and rotate the mesh so
   that plane is horizontal.
3. **Register it to machine X/Y.** Put two or three reference features on the jig, such as
   pin holes or a corner, at known machine coordinates. Find them in the scan, and solve for
   the shift and rotation.
4. **Look straight down.** Cast a ray down through the mesh at every grid point (0.05–0.1 mm
   spacing) and keep the highest hit. The result is a **16-bit height map**: a greyscale
   image where brightness is height, plus a sidecar recording millimetres per level and the
   origin.
5. **Plan the zones** from that height map (see [DESIGN.md](DESIGN.md)).

Step 4 produces exactly the kind of file DepthView already inspects. The same checks apply:
real bit depth, level count, and gaps.

### Registration is part of the accuracy budget

On a slope, an X/Y error becomes a height error:

```
height error ≈ slope × registration error
```

At a 20° slope (tan ≈ 0.36), a 0.3 mm alignment error is a 0.11 mm height error. That's
usually fine for engraving, but it counts against a tight color tolerance. A good jig
matters more than a better scanner.

### What a top-down height map can and can't represent

This works in the planner's favour: **the laser also only sees the part from above.**

- **Overhangs and undercuts:** hidden from the scan, but also out of the laser's reach, so
  nothing is lost.
- **Near-vertical walls:** the map shows a cliff. The planner should mask out anything
  steeper than a set limit (Bambu uses 40°) rather than try to mark it.
- **Steps, raised rims, stamped logos:** the height map captures them. Zones then follow
  height contours instead of straight bands, which is what makes irregular parts workable.
- **Holes and cut-outs:** these read as "no surface" and are masked.

### Other ways to get a height map

- **The laser probing a grid itself**, as the xTool F1 Ultra and Bambu H2D do. The Lumos
  Ultra has the distance measurement but no way to run such a sequence yet.
- **Photogrammetry** from a phone. It's cheaper but less accurate; it may be enough for
  engraving.
- **Manufacturer CAD** for commercial blanks, where a STEP or STL of the blank is published.
- **Designing the blank yourself.** If you 3D-print or machine the part, the model is
  already the height map.

## Relief on a curved part

Once the surface is known, a relief (depth map) engraving can be *added* to it. The engraved
depth is the design's depth measured down from the local surface, rather than from a flat
plane. Two height maps combined is squarely DepthView territory; see item B3 in the
[roadmap](README.md#roadmap).
