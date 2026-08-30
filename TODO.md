# DepthView — deferred work

Everything discussed and consciously set aside, with enough context to pick it up cold.
Ordered by my estimate of value per unit of work, not by size.

---

## 1. LightBurn workflow services

Background established by research on 2026-08-29:

- **LightBurn 2.1 has a native 3D Sliced Image mode, and it is galvo-only** — which the
  Lumos Ultra MOPA is. It accepts 8-bit *and* 16-bit greyscale depth maps.
- **Number of Passes is the slice count.** Darkest pixels receive every pass, pure white
  receives none, everything else scales in between. No live Z required; it is 2.5D.
- **Black is deepest** by default; Negative Image inverts.
- LightBurn's docs state a 24-bit depth map "is actually three 8-bit channels, so they
  display as 8-bit" — meaning DepthView's existing *grey data stored as RGB* finding is a
  hard failure mode for LightBurn, not a tidiness note. Same for the imposter classes.
- LightBurn's docs also admit 3D Slice "does not offer precise control over the
  engraving's depth". Item 1.4 below is aimed squarely at that gap.

### 1.1 Pass-count simulator  *(highest value, mostly arithmetic on data we already have)*

Enter N passes; report:

- how many of the N slices are actually non-empty (a map spanning 60% of its range wastes
  40% of the passes)
- pixel area per slice, for a rough time estimate
- how many source grey levels collapse into each slice
- **terracing risk**: the largest area jump between adjacent slices, which is exactly where
  a visible contour step lands on the workpiece
- overlay the slice boundaries on the existing histogram control

Partially prototyped already: the relief preview's *Quantise to steps* control shows the
terracing visually. This item is the numeric half.

### 1.2 Depth budget

Target depth ÷ passes = µm per slice, worked in both directions. For the brass coin work
that is 1.1 mm per side; at ~110 passes that is 10 µm steps, which means 110 is the real
ceiling and a 56,299-level map is 99.8% wasted. Feed it measured depth-per-pass and it
becomes a planning tool. These are the same numbers LaserTuner recipes want.

### 1.3 Export a LightBurn-ready map

- invert to LightBurn's convention (black deepest)
- remap min/max to the full range so no passes are wasted — the *Unused headroom* row
  already reports what there is to reclaim
- clamp or mask the background so a flat far plane does not get full power
- quantise deliberately to exactly N levels, matching the pass count
- **dither the slice boundaries** — the trick that breaks up terracing on smooth curves
- write true 16-bit *greyscale* PNG, never RGB
- resample to the engraving raster from physical size + DPI, and set the pHYs chunk so
  LightBurn imports at the correct size without resampling

### 1.4 Material response calibration  *(the one that closes LightBurn's admitted gap)*

1. DepthView emits a stepped grey wedge.
2. Engrave it on the real material at the chosen pass count.
3. Measure each step's depth with a depth gauge.
4. Type the measurements back in.
5. DepthView builds the inverse LUT.

Applied, a linear depth map then produces *linear physical depth*. Brass ablation is not
linear as the pocket deepens, so this is the difference between a relief that looks right
and one that is crushed in the shadows.

### 1.5 Focus-stepping schedule

DepthView knows the slice-to-depth mapping, so it can emit the table of which pass ranges
need which Z/focus offset, and optionally split the export into per-focus-group images.

### 1.6 Spot-size simulation

Apply a Gaussian at the beam width and line interval to show which fine detail actually
survives the physical spot. Cheap, and genuinely predictive rather than cosmetic.

### 1.7 Speculative — not scheduled

- Emit a `.lbrn2` project directly, with the image embedded, sized, and on a 3D Slice layer
  with pass count and power set. Doable (XML with base64 image) but fragile against
  LightBurn version changes.
- Two-sided coin registration: mirrored/aligned pairs for 4 mm blanks engraved both sides.
- Export analysis as JSON for LaserTuner's Recipe → Run → Result model.

---

## 2. Relief preview — beyond Tier 1

Tier 1 (lit height field, material presets, AO, movable light, slice quantisation) and
Tier 2 (orbit, tilt, zoom, pan with real geometry) are both **built**. Deferred:

- **Perspective camera.** The orbit view is orthographic, which is honest for inspection but
  flatter looking than a real photograph. A modest FOV would need near-plane clipping.
- **Shadows.** Occlusion is baked from the height field and does not depend on the light, so
  a low sun does not throw a long shadow. A height-field ray march per pixel toward the light
  would fix it, and could reuse the same max-height pyramid a proper raycaster would need.
- **True scale mode.** Enter physical width and depth in mm and drop the exaggeration
  slider, so the preview shows the honest 4% aspect ratio rather than a flattering one.
- **A/B compare** raw versus sliced, side by side or on a toggle.
- Directional/rim light as a second source.
- **Texture minification.** Textures are sampled with a single bilinear tap, so a texture
  scale much above 2 repeats aliases. Mipmapping or a few extra taps when minified would fix
  it. Not urgent while the intended use is one copy of a photo of the actual board.
- Texture thumbnails in the material panel, so you can see what is loaded without rendering.
- A material picker that shows each preset rendered on a sample sphere.
- Import a normal map directly, rather than deriving one from a greyscale height image.

---

## 3. Analysis

- Per-channel histograms are computed but not plotted — the histogram control only draws
  the grey one. Worth a channel selector for colour-contaminated maps.
- Detect turbo/viridis/magma colour-encoded depth maps and offer to decode them back to
  grey. AI depth pipelines emit these constantly and they are currently just reported as
  "lots of non-grey colours".
- Region statistics: drag a rectangle on the thumbnail and analyse only that area.
- Compare two files side by side (before/after a processing step).
- Noise floor estimate — distinguish genuine fine gradation from dither or sensor noise
  masquerading as high bit depth.

---

## 4. Formats

- EXR (float, common from 3D and AI pipelines). Deferred because the format is genuinely
  complex; PFM covers most of the same ground for now.
- BigTIFF — currently detected and rejected with a clear message.
- Floating-point TIFF — currently detected and rejected with a clear message.
- 16-bit TIFF is supported but goes through ImageSharp, so it is flagged as not
  bit-exact. A native TIFF decoder would close the last precision gap.

---

## 5. Housekeeping

- Avalonia 11.3 marks `DataFormats` and `IClipboard.GetDataAsync` obsolete in favour of the
  `DataTransfer` API arriving in 12.x. Currently suppressed with a scoped
  `#pragma warning disable CS0618` and a comment. Revisit when moving to Avalonia 12.
- No automated test project. The fixtures in `tests/` are verified by hand against NumPy;
  worth wrapping in a real test runner so regressions are caught on build.
- Icon is done. Still outstanding for a release: a splash or About box, a version stamp
  visible in the UI, and a macOS `.icns` / Linux `.desktop` entry so the icon shows there too
  (the `.ico` only covers Windows).
- Licensing is settled: MIT, with dependency terms in `THIRD-PARTY-NOTICES.md`. If DepthView
  ever ships closed-source from an entity at or above 1M USD annual gross revenue, the
  ImageSharp position changes and would need a commercial licence — or the dependency could
  be dropped. PNG encoding is an hour's work given the decoder already exists, BMP is
  trivial, JPEG and TGA could come from the public-domain StbImageSharp; TIFF and WebP are
  the only genuinely awkward parts.
- The published binary is ~44 MB because it is self-contained. Trimming or ReadyToRun
  could cut that, at some risk to Avalonia's reflection-based XAML loading.
