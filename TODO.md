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

### 3.0 Recalibrate the sparse-occupancy warning  *(open question, raised by real files)*

Two of Lee's own 4096x4096 depth maps — genuine 16-bit, level step 1, no ladder, no
replication — come back **WARN**, verdict *"Sparse: about 13 bits of real detail"*, on
7,814 and 6,839 distinct levels (about 12% and 10% occupancy).

The statement is true and the arithmetic is right. The question is whether **WARN** is the
correct severity, and the argument that it is not:

- 7,814 smooth levels is roughly 30x what an 8-bit map carries.
- No laser process will consume it. At the pass counts LightBurn actually runs — tens to a
  few hundred — everything above about 256 levels is already surplus, and item 1.2 works out
  that even a 1.1mm brass pocket at 10µm steps tops out near 110 usable levels.
- A tool whose headline skill is separating real depth from fake depth should not raise a
  warning against files that are unambiguously real. If good work trips the alarm, people
  learn to ignore the alarm, and then it fails on the day it matters.

Options, roughly in order of preference:

1. Judge occupancy against **what the job can use**, not against the container. Sparse only
   means something relative to a pass count; without one, 7,814 levels is simply plenty.
2. Drop it to **INFO** and keep the wording, so it informs without accusing.
3. Keep WARN but raise the threshold well below 10% occupancy, which is really aimed at a
   map carrying a few hundred levels in a 16-bit container.

Worth settling before strangers run it on their own good files. Option 1 is the honest one
and it wants item 1.1 (pass-count simulator) to exist first; option 2 is the ten-minute
version that stops the false alarm now.

### 3.1 Everything else

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

## 5. Blocking a public release

Nothing here is a missing feature. These are the things a stranger arriving at a public
repository would find absent, ordered by how much each one costs the project's credibility.

- ~~**Never built or run on macOS or Linux.**~~ **Settled 2026-08-30.** The CI matrix builds
  clean on ubuntu-latest, macos-latest (arm64) and windows-latest, and all three produce
  byte-for-byte identical analysis numbers across the twelve fixtures. The software relief
  renderer produces a correct lit render on all three. What remains untested on macOS and
  Linux was the **GUI itself**. That is now covered too: CI opens the real window on all
  three platforms — under Xvfb on Linux, natively on macOS and Windows — loads a sample,
  captures the window and uploads the PNG, so what the program actually looks like on each
  platform is inspectable rather than assumed. It immediately found a layout fault nobody
  could have seen on a large monitor (see below). What is *still* unexercised anywhere but
  Windows is the parts a screenshot cannot reach: the **native file dialog**, **drag and
  drop**, and **clipboard paste**, all of which are platform-specific and none of which a
  headless runner can drive. One person opening the binary and using it closes that.
- **No published binaries — but the machinery is now built and proven.**
  `.github/workflows/release.yml` publishes all seven self-contained binaries, names them
  per platform, checksums them and attaches them to a Release, and a dry run has been
  exercised end to end: seven green jobs in about 45 seconds, 36 to 41 MB each. All that
  remains is deciding to tag one, which is a judgement call rather than work:

      git tag -a v1.0.0 -m "DepthView 1.0.0"
      git push origin v1.0.0

  The workflow can also be run manually with `dry_run` on, which builds and uploads without
  creating a release. Do that after any change to it.
- ~~**Unsigned binaries.**~~ **Mitigated, not solved.** `.github/RELEASE_TEMPLATE.md` tells
  users exactly what SmartScreen and Gatekeeper will say and how to get past it, including
  the `xattr -d com.apple.quarantine` line, and ships SHA256SUMS.txt so a download can be
  verified in the absence of a signature. Actually signing still costs money and an Apple
  developer account, and is the only real fix.
- **No macOS `.app` bundle.** `publish.ps1` emits a bare Mach-O executable, so on macOS
  there is no icon, no Finder double-click, and no bundle identifier. A `.app` is a
  directory with an `Info.plist`, an `.icns` and the binary — scriptable, but not free.
- **No Linux desktop integration.** No `.desktop` entry and no icon theme install, so the
  program has no menu entry and no icon in a launcher.
- ~~**No CI.**~~ **Done.** `.github/workflows/build.yml`.
- ~~**No automated test project.**~~ **Mostly done.** `tests/check_report.py` asserts every
  fixture's known-correct answer and cross-checks the three encodings of the same data, and
  CI runs it on all three platforms. It is not a `dotnet test` project, so it exercises the
  program from outside rather than reaching individual classes — good enough that a decoder
  regression cannot land silently, and worth upgrading to xUnit if unit-level coverage of
  `PngDecoder` and `DepthAnalyzer` is ever wanted.
- ~~**CI Actions on the deprecated Node 20.**~~ **Done.** checkout v4→v7, setup-dotnet v4→v6,
  setup-python v5→v7, upload-artifact v4→v7, download-artifact v4→v8. All three platforms
  green afterwards and the deprecation annotations are gone.
- ~~**No issue templates.**~~ **Done.** `.github/ISSUE_TEMPLATE/` has a bug report and a
  platform-test report, both asking first for the About box's *Copy build info* line. The
  platform-test form covers exactly what CI cannot: file dialog, drag and drop, clipboard
  paste. It asks explicitly for "everything worked" reports, because a tester who finds no
  problem usually says nothing, and silence is indistinguishable from nobody having tried.
- Still no `CONTRIBUTING.md` or `CHANGELOG.md`. Only worth writing if contributions arrive;
  the release notes carry the changelog's job for now.
- No versioning policy or git tags. The version lives in one place in the csproj and is
  reported by the About box; nothing yet ties it to a tag.
- **The acknowledgements link may point inside a private Facebook group.** If so it 404s for
  everyone outside the group and publishes a pointer into a private space. Check before going
  public; a name without a link is fine.

## 6. Housekeeping

- Avalonia 11.3 marks `DataFormats` and `IClipboard.GetDataAsync` obsolete in favour of the
  `DataTransfer` API arriving in 12.x. Currently suppressed with a scoped
  `#pragma warning disable CS0618` and a comment. Revisit when moving to Avalonia 12.
- Icon and About box are done. The About box carries the version, the build date, the
  supported platforms, the live runtime and host, the credit roll and the licence. The
  platform list in `BuildInfo.Platforms` is duplicated knowledge: it must be changed in the
  same commit as any RID change in `publish.ps1` / `publish.sh`, or the program starts
  advertising builds that do not exist. It already did that once, briefly.
- `samples/` is an allow-list in `.gitignore`, not a deny-list: everything there is ignored
  except the ten generated files named explicitly. It is the natural place to drop somebody
  else's artwork to try it on, and `git add -A` would otherwise publish it under this
  project's MIT licence. Adding a real sample means adding its name deliberately, after
  checking the rights.
- Licensing is settled and needs no further thought. DepthView is MIT and is not going to
  become a paid tool, and the Six Labors terms grant Apache 2.0 rights to open-source
  consumers regardless of revenue, so the ImageSharp position cannot change. Dependency
  terms are recorded in `THIRD-PARTY-NOTICES.md`.
- Dropping the ImageSharp dependency is optional and would be about binary size and having
  one less third-party component, not about licensing. PNG encoding is an hour's work given
  the decoder already exists, BMP is trivial, and JPEG and TGA could come from the
  public-domain StbImageSharp; TIFF and WebP are the only genuinely awkward parts.
- The published binary is ~44 MB because it is self-contained. Trimming or ReadyToRun
  could cut that, at some risk to Avalonia's reflection-based XAML loading.
