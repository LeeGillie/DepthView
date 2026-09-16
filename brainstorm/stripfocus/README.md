# StripFocus

**Keeping a flat-bed galvo laser in focus on parts that aren't flat.**

Status: **brainstorming**. The research is done and there's a working browser prototype for
smooth curves that run in one direction. Nothing here is part of a DepthView release yet.

---

## The problem

Coins are flat, but belt buckle blanks, hip flasks and many other metal blanks aren't. A
buckle has a gentle crown across its face, and a flask curves away toward its edges. If the
job is run as if the part were flat, some of the design is always out of focus.

A fiber or UV galvo has a limited depth of field, and some processes are much less forgiving
than others:

- **Deep engraving and plain marks** tolerate a little defocus.
- **MOPA color marking on stainless** does not. The color comes from how much energy lands
  per unit area. Defocus changes that, so the color shifts across the part. An industrial
  marking shop describes exactly this failure on radiused parts
  ([Jimani](https://www.jimani-inc.com/blog/color-marking-with-fiber-lasers)).
- **Tilt matters too, even in focus.** Where the surface slopes away from the beam, the spot
  stretches by 1/cos(angle): about 6% at 20°, 15% at 30°. Energy per area drops by the same
  factor.

The question came up in the WeCreat Lumos Ultra owners' group. Several owners asked the same
thing about buckles and flasks, and nobody had a good answer for the Lumos.

## What people do today

Full notes with sources are in [RESEARCH.md](RESEARCH.md). In short:

1. **Focus at mid-height** and rely on depth of field. This is common for flasks in a flat
   jig. It works for engraving on gentle curves, but not reliably for color.
2. **Rotary.** It's good for true cylinders. It's awkward for parts whose curve has a large
   radius or isn't circular, which describes most buckles and flasks.
3. **Measure the surface and let software refocus.** This exists, but only locked to
   particular machines: xTool Creative Space on the F1 Ultra, Bambu Suite on the H2D, and
   EZCAD3 with dynamic-focus hardware. **Nothing does this for the Lumos Ultra**, and
   LightBurn's surface mapping is still a feature request.

## The idea

Describe the surface, split the job into zones that are each flat enough to run at one
focus height, and produce the files (and eventually the G-code) to run them.

- **Describing the surface** is its own subject. Options run from typing in a few caliper
  readings to a 3D scan, which could extend this beyond smooth curves to truly irregular
  parts. See [SURFACE-INPUT.md](SURFACE-INPUT.md).
- **Splitting and running** covers banding and zoning, rotary indexing, what the galvo's
  optics do to all of this, and the path from separate files to a single job. See
  [DESIGN.md](DESIGN.md).

### Why this belongs with DepthView

A scanned surface seen from above *is* a height map: a greyscale image where brightness is
height. DepthView already reads, checks and reasons about exactly that kind of file. The
zone planner is a new consumer of the same data. A relief engraved *onto* a curved part is
also the difference between two height maps: the design, and the surface it's cut into.

## Try the prototype

[`prototype/StripFocus.html`](prototype/StripFocus.html) is a single offline HTML file.
Download it (use GitHub's **Download raw file** button) and open it in any browser. There's
nothing to install.

It handles **smooth curves in one direction** (X or Y):

- **Surface:** enter measured points as `position, height`, one per line. It fits a smooth
  curve through them that never overshoots.
- **Artwork:** load an SVG or a bitmap. It's sized along the surface so it isn't squashed on
  slopes.
- **Banding:** set the largest height change allowed within one band.
- **Output:** a profile chart, a preview, and a focus plan giving each band's focus height,
  offset from the highest point, worst focus error and maximum tilt.
- **Download:** a zip with one file per band, a combined color-coded SVG (one color per
  band), `focus-plan.csv`, and a README.

To run it on the machine: focus on the highest point of the part, then for each band lower
focus by that band's offset and run only that band.

Known limits: SVG text must be converted to outlines first. Domed or irregular parts aren't
handled yet. It hasn't been verified on a real part yet. That's the next step, and results
will be posted here.

The prototype bundles [paper.js](http://paperjs.org/) 0.12.18 (MIT) for vector clipping.
Its licence is in `prototype/paper.js-LICENSE.txt`.

## Roadmap

| Step | What | Status |
|---|---|---|
| A1 | One-direction bands from typed-in measurements, files per band | Prototype works |
| A2 | Verify on a real buckle: engraving and a color swatch test | Next |
| A3 | Defocus tolerance test pattern generator (sets the band tolerance per process) | Idea |
| B1 | 2D zones from a grid of measurements (domed parts) | Idea |
| B2 | Import a 3D scan (STL/PLY/OBJ) and turn it into a height map | Idea |
| B3 | Zones for irregular parts, with occlusion and steep-slope warnings | Idea |
| C1 | Single job: one G-code file with a focus change per zone | Blocked on whether custom G-code can be run on the Lumos |
| C2 | Rotary indexing: turn each zone to face the beam, then focus | Idea; see DESIGN.md |
| C3 | Tilt compensation: raise power by 1/cos(tilt) per zone | Idea |

## Contributing

Measurements from real parts, test results (especially color swatches at known defocus),
and corrections are the most useful things right now. Open an issue with **StripFocus** in
the title.
