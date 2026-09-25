# DepthView — deferred work

Everything discussed and consciously set aside, with enough context to pick it up cold.
Ordered by my estimate of value per unit of work, not by size.

### Resume here (paused 2026-09-25, after 1.4.0)

1.4.0 is released: true-scale relief, one program-wide blank, zip installers and the in-place
updater. `main` and the `v1.4.0` tag are on GitHub; nothing is waiting to be pushed. Next up,
roughly in order:

- **Listen for Mac and Linux reports** on the 1.4.0 zips - only CI has run them (§6).
- **§7.3-7.5** (terrace width, noise floor, dither detection) need no measured constant and
  can ship before any coupon is cut.
- **§7.9** target-vs-simulated depth comparison and the suggested Z advance with an override
  warning - designed, not built.
- **G-code capture watcher** (planned `GcodeCapture.cs`, `CaptureArchive.cs` - not yet
  written; `Integrations/WeCreat/Gcode/GcodeStream.cs` is the reader they build on) and the
  segmenter that would sit on it; the segmenter waits on WeCreat (§8).
- Decide the `--calibrate` default `--size`.
- **The measurement programme has not started** - no coupon cut, nothing weighed. Depth
  prediction is not a capability yet; see CLAUDE.md, What is owed.

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

### 1.0 ~~Depths per pass count~~  **Done 2026-08-31** — and a lesson worth keeping

Shipped: `SlicesAt(passes)` and the DEPTHS PER PASS COUNT table. For any pass count it
reports how many distinct depths the file actually resolves, how many passes repeat a depth
already cut, and what reclaiming unused headroom would recover.

**The lesson matters more than the feature.** This item was first written claiming 3D Slice
has a flat 256-level ceiling and that 16-bit buys nothing, sourced from a forum quote of
LightBurn's own creator. The quote was accurate and *out of date*: it described the galvo
path before LightBurn 2.1, which added 16-bit depth map support. The current documentation
says so plainly — "As of LightBurn 2.1, LightBurn support 16 bit depth maps", and "if you
plan to run more than 256 passes, a 16 bit image is better".

So the README was correct, then was "corrected" into being wrong, then corrected back. The
error was conceding to an authoritative-sounding quote without checking its date against
primary sources. **An authoritative source can still be describing an old version.** Check
the docs for the version in front of you.

The design that came out of it is better than either wrong answer: the pass count is a
**parameter**, never an assumption. LightBurn was 8-bit and is now 16-bit, MakeIt is quoted
at 256 layers, other toolchains differ. Hard-coding any one ceiling bakes a particular
version of a particular program into the analysis. "At N passes, what do I get" is true
everywhere and stays true.

Still open, and the natural next step:

- Let the user **enter their pass count** and have the whole report speak in those terms.
- Pixel area per slice, for a time estimate.
- **Terracing risk**: the largest area jump between adjacent slices, which is where a
  visible contour step lands on the workpiece.
- Overlay the slice boundaries on the histogram control.

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

### 1.3 ~~Export a LightBurn-ready map~~  **Done 2026-08-31** — this became the Tuner

Shipped as `--tune` and the **Tune…** dialog, sharing one implementation (`TuneJob`) so a
file written from the dialog and one written from the command line with the same settings
are the same bytes.

- ~~invert to LightBurn's convention (black deepest)~~ — `--invert`, and black-deepest is
  the default convention throughout
- ~~remap min/max to the full range so no passes are wasted~~ — the two level points, with
  percentile defaults rather than min/max, because one stray pixel at an extreme makes a
  min/max stretch do nothing at all
- ~~clamp or mask the background so a flat far plane does not get full power~~ — the black
  and white points do this, and the rim does it geometrically at the edge
- ~~quantise deliberately to exactly N levels, matching the pass count~~ — `--slices`
- ~~dither the slice boundaries~~ — 8×8 ordered, `--dither`
- ~~write true 16-bit greyscale PNG, never RGB~~ — own encoder, colour type 0, with the
  settings stamped in as tEXt so a tuned file six months later says what was done to it
- ~~set the pHYs chunk so LightBurn imports at the correct size~~ — from the blank
  diameter, and **off by default**: a file that claims 40 mm because a box happened to say
  40 is worse than a file that claims nothing, since an importer will believe it

**Still open from this item:** actually *resampling* to the engraving raster. DepthView
reports whether the map's resolution matches the spot, but it does not yet resize the map
to a target DPI. That is a real gap for anyone whose art is 1024 px on a 40 mm blank.

One deliberate omission: nothing in the tuner writes over the original. Every path produces
a new file.

### 1.3b ~~Fit a design inside the rim~~  **Done 2026-08-31**

Raised by the obvious question: what happens when the artwork runs past where the rim goes?
The first answer was to report the overlap and suggest a scale factor. The better answer,
and the one shipped, is to grow the canvas — put the map in the middle of a larger square so
the whole design lands inside the rim.

The point is that **padding does not resample**. Scaling the artwork down would interpolate,
inventing grey levels that were never in the file, which is the exact fault this program
exists to detect; doing it to make artwork fit would be indefensible. The physical result is
identical either way, because the blank does not change size — the same artwork simply spans
fewer millimetres of it, at a finer effective resolution than before.

Two choices, both exposed because both have a real cost:

- **What clears the rim.** *Content* measures the furthest engraved pixel; *canvas* contains
  all four corners. Canvas cannot clip anything but has to fit a square inside a circle, so
  it gives up a factor of √2 — 27 mm of art on a 40 mm blank against content's 38 mm.
- **What the new ring is cut to.** Matching the design's background keeps the field
  continuous; leaving it untouched costs nothing to engrave. On art with a cut-away floor the
  untouched fill puts a visible square step around the coin, which is why the default matches
  the background — and why both renders are in the README rather than a sentence describing
  them.

Three bugs came out of building it, all of which would have reached metal:

1. **`--invert` plus a rim cut the rim to full depth.** Inversion ran last, so the rim's
   "paint this white" became "cut this away" — on the one part of a blank nobody wants
   touched. Anything that must end up untouched is now written as its mirror before the flip.
2. **Padding with white left a square step.** The first implementation filled the new space
   with untouched rather than with the design's own background, so the boundary of the source
   image showed up as a raised square around the coin.
3. **The content test was a brightness threshold.** "Above a fifth of the range is content"
   only holds for art on a black floor, reads a white-floor map backwards, and inverts again
   the moment someone ticks Invert. It now takes the background from the image border, which
   is true whatever the convention, and measures content only inside the original rectangle —
   because after padding the border is our own fill, and asking it what the background is
   just reads back our own answer.

`tests/check_fit.py` pins the geometry, byte-for-byte pixel preservation, both padding fills
and the rim polarity, and runs on all three platforms in CI.

### 1.4 Material response calibration  *(the one that closes LightBurn's admitted gap)*

1. ~~DepthView emits a stepped grey wedge.~~ **Done 2026-08-31** — `--calibrate` writes a
   coupon sized to the blank, carrying all three tests at once (see 1.4b), plus a bench
   worksheet to write the measurements on.
2. Engrave it on the real material at the chosen pass count. *(brass and stainless, pending)*
3. Measure each step's depth with a depth gauge.
4. Type the measurements back in. **Not built.** Deliberately deferred until there are real
   measurements to design the entry form around — a form invented before the first coupon
   is a guess about what the numbers look like.
5. DepthView builds the inverse LUT. **Not built**, follows from 4.

Applied, a linear depth map then produces *linear physical depth*. Brass ablation is not
linear as the pocket deepens, so this is the difference between a relief that looks right
and one that is crushed in the shadows.

### 1.4b Wall-angle test piece  *(sibling of 1.4, same "stop guessing" idea)*

Open question raised while building the rim: **what wall angle will a Lumos Ultra actually
hold, and at what depth?** Nobody involved knows, and it is not the sort of thing to put a
default in a config file for.

What is certain: the map cannot express an edge sharper than one pixel (9.8 um on a 40 mm
blank at 4096 px), and the beam smears any transition to roughly its own spot size whatever
the map says, so a ramp between zero and about one spot diameter is pointless. What is not
certain is whether a near-vertical wall survives at 1.1 mm depth - ablated pockets taper as
they deepen, because the beam converges to a waist, debris and plasma shield the floor, and
a deep narrow pocket clips the beam on its own wall.

So emit a test piece: a row of pockets of equal depth with ramps from zero to, say, 1 mm,
each labelled. Engrave it, look at it, measure it. Then the ramp default is a measurement
rather than an opinion, exactly as item 1.4 does for depth response.

Worth pairing with them in one calibration artefact: a depth wedge, a wall-angle row, and a
spot-size resolution comb. One engraving that answers all three, once per material.

**Built 2026-08-31.** `--calibrate` emits exactly that coupon: ramps at known wall angles,
a depth wedge across the middle, and a comb of shrinking gaps, all inside the rim so the
untouched field stays as the datum. The labels are engraved at a fraction of full depth
rather than full depth — caught by rendering the pattern and noticing it was asking for
over a millimetre of deep cutting just to write the numbers.

What remains here is not code: engrave it, look at it, measure it. Until then the ramp
default stays "none", which is an honest admission rather than a guess.

~~**Open: warn when the coupon is generated too small.**~~ **Done 2026-09-24.** The comb
now computes pixels per line pair for every cell. Under 2 px the cell is left uncut and
labelled "-" instead of being drawn — and that fixed a real bug, not just a missing
warning: the old code clamped an unrepresentable pitch up to 2 px **but kept the original
label**, so a `--size 1400` coupon printed "25" over lines that were really 57 µm apart.
Under 3 px the cell is drawn but warned as uneven. Warnings print first and head the
worksheet.

**One consequence to decide on.** The 25 µm cell is 2.56 px at the 4096 default, so **the
default coupon now warns about its own finest cell.** The warning is true — bars alternate
1 and 2 px — but a warning on every default run teaches people to ignore warnings. Either
raise the default `--size` (4800 gives 3.0 px, 6400 gives the 4 px needed for bars at
least 2 px wide) or accept it. Not changed unilaterally: 4096 is also the project's
standard depth-map width and appears in README and CLAUDE.md figures.

Not done: the same check for an over-large `--rim-mm` or a tiny `--blank`.

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
- ~~**True scale mode.**~~ Done 2026-09-24. Blank diameter + target depth in mm, exaggeration
  in stops with 0 = true scale, in the Tune panes, the standalone relief window and
  `--render` alike (shared `Rendering/ZScale.cs`). The slider, its text and an on-picture badge
  shade green → yellow (2x) → red (8x+, "inspection only"). Up to 1.3.0 the relief window and
  `--exag` used a raw ratio where 1.0 drew ~5 mm on a 40 mm blank; the 1.3.0 release notes
  said this was fixed, but only the Tune window had been. Corrected in the 1.4.0 release
  notes, which also call out that `--exag` values changed meaning (now stops).
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

### 3.0 ~~Recalibrate the sparse-occupancy warning~~  **Done 2026-08-30**

Fixed by `AmpleLevels` in `DepthAnalyzer`, which takes option 2-and-a-half below: a file with
at least 1,024 distinct levels **and** a level step of 1 is reported as genuine rather than
sparse, with the occupancy figure kept as an INFO finding. The two files now read *"Genuine
16-bit data, carrying about 13 bits"*. Sample 04 stays a warning because its step is 64.

Option 1 remains the better answer whenever item 1.1 exists — judging the levels against an
actual pass count is more honest than any fixed threshold. The record of why, kept because
the threshold will look arbitrary to whoever reads it next:

Two of Lee's own 4096x4096 depth maps — genuine 16-bit, level step 1, no ladder, no
replication — came back **WARN**, verdict *"Sparse: about 13 bits of real detail"*, on
7,814 and 6,839 distinct levels (about 12% and 10% occupancy).

The statement was true and the arithmetic was right. The question was whether **WARN** was the
correct severity, and the argument that it was not:

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

**One thing to watch.** This whole episode came from pointing the tool at real work rather
than at fixtures. Every fixture in `tests/` was written to have a known answer, which means
none of them could ever have caught a mis-tuned severity — a fixture cannot tell you that a
correct measurement is being reported with the wrong emphasis. Only somebody's actual files
can. That is an argument for taking the platform-test reports seriously when they arrive,
and for asking testers what DepthView said about their own maps rather than only whether it
ran.

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
- Versioning: `<Version>` in the csproj is the one source, and a `v<version>` tag pushed in
  the same commit publishes the release (see CLAUDE.md, Releasing). The tag format is now a
  contract with the updater, so it cannot change casually.
- **The acknowledgements link may point inside a private Facebook group.** If so it 404s for
  everyone outside the group and publishes a pointer into a private space. Check before going
  public; a name without a link is fine.

## 6. Housekeeping

- ~~**Installers and in-place updates.**~~ **Released in 1.4.0 (2026-09-25).** Carried
  over from LUOM What's New: per-platform zips (one `DepthView/` folder: program, README.txt,
  LICENSE.txt; a signed-ad-hoc `DepthView.app` on macOS; a menu-entry script on Linux) from
  `packaging/make_bundle.py`, and `Updates/UpdateService.cs` - daily GitHub check, green bar,
  Skip this version, About > Check for updates, and a verified install (digest or
  SHA256SUMS, `--version` of the new copy, rename-aside swap, rollback).
  - Done: release `dry_run` all green (macOS `codesign --verify` and `--version` on
    osx-arm64, Linux smoke); the 1.4.0 notes tell 1.3.0 users to download once by hand;
    a real update from a 1.3.0-numbered build to the published 1.4.0 on Windows, including
    the renamed-aside .exe being cleaned up on the next start.
  - **Still open: the Mac and Linux zips have only been run in CI**, never on a user's
    machine. First real report from either platform is worth reading closely - Gatekeeper
    wording, the quarantine/"damaged" path in README.txt, and the menu-entry script.
  - The first true two-release update (1.4.0 zip -> 1.4.1 zip) has not happened yet. Watch
    it on whichever platform ships next.
  - Still unsigned in the paid sense: SmartScreen and Gatekeeper still ask once. Notarisation
    would remove the macOS prompt and costs an Apple Developer membership.

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

---

## 7. Depth prediction — the stepwise plan

Full reasoning, formulas, published constants and measurement methods are in
`docs/DEPTH-PREDICTION.md`. This section is only the order of work and what "done" means
for each step.

**The depth model changed on 2026-09-24.** Baseline research
(`docs/research/laser-ablation-baselines.md`) found that the log law `d = δ·ln(F/F_th)` does not
predict MOPA depth: published ns constants split ~10× apart, and neither regime reproduces depth
rising with pulse width at fixed fluence. The model is now **`d_pass = η · P/(v·h)`** — area
energy dose times a measured removal efficiency — with the log law kept only as a
removal-onset gate. See `docs/DEPTH-PREDICTION.md` §2.0.

**The handoff rule, and it is not negotiable.** Steps 1 and 2 produce the measured values
(spot size and threshold for the onset gate; removal efficiency `η` for depth) that step 6
runs on. Doing 6 first means inventing them, which
defeats the entire point of the system — a model seeded with made-up constants looks
exactly like a model fitted to measurements, and the tool would then be lying confidently.
Anything that needs a constant waits for the coupon that produces it.

**What is not blocked.** Steps 3, 4 and 5 need no physical measurement at all. They are
geometry and statistics on the image already loaded. Ship those first so the project keeps
moving while the coupons are cut, and so there is something real in front of contributors
before anyone is asked to buy a scale.

### 7.1 Liu's D² coupon — spot size and ablation threshold  *(bench, blocks 7.2 and 7.6)*

The single cheapest experiment in the whole plan, and the one that resolves the largest
present uncertainty. The Lumos spot is quoted somewhere between 6 and 8 µm; fluence goes as
`1/w²`, so that range alone is a **1.8× spread in every fluence figure** we would compute.
Everything downstream inherits it.

Method: single pulses at a descending energy ladder, measure crater **diameter** — not
depth, which is why a $40 coin microscope is sufficient. Plot `D²` against `ln(E)`. It is a
straight line: slope gives `2w₀²`, x-intercept gives `E_th`.

- Needs: LCD/USB microscope, **calibration slide**, a polished coupon. No scale, no
  indicator. The slide is the non-negotiable part — the magnification printed on these
  scopes is fiction, and calibrating against a known ruling makes that irrelevant.
- **Measure on the PC in ImageJ, not off the scope's LCD.** Lock and record the stand height
  for the session; changing it invalidates the calibration.
- Watch for incubation — `F_th(N) = F_th(1)·N^(S−1)`. Single pulses per site, spaced well
  apart, or the threshold you measure is not the one you think.
- **Use the line-width variant for the UV galvo.** At a 6–8 µm spot a near-threshold crater
  is about 8 px on a 720p sensor at 1 mm FOV, which will not fit to the few percent a D² fit
  needs. A scanned line obeys the same form (`W² = 2w₀²·ln(F₀/F_th)`) and gives a long edge
  to average along instead of one small disc. The threshold it returns is `F_th(N_eff)`, so
  carry the overlap count through the incubation relation — or vary overlap deliberately and
  **fit `S` from the same coupon**, which step 7.6 needs anyway.
- **Split the two constants — they do not need the same instrument.** `F_th` can be had with
  calipers: engrave a ladder of short segments at stepped power, find the first that marks at
  all. Fifty steps gives 2 % resolution, read at millimetre scale rather than micron scale.
  Only `w₀` actually needs the microscope, so a scope that qualifies poorly costs precision
  on one constant instead of blocking the step. **Add this ladder to `--calibrate`** — it is
  a near-trivial addition to the coupon that already exists.
- **Done when:** `w₀` and `F_th` are written down with an R² for the fit and a stated pulse
  duration, source and lens. A number without those four qualifiers is not a result.

**Kit decision, 2026-09-11.** Stage micrometer: WintopScope 4-scale, ~$16 — the 0.01 mm × 100
and 0.1 mm × 10 rulings plus 0.07 mm and 0.15 mm dots. Those dots earn their place twice:
a round known-diameter object validates the crater-measuring *procedure*, and under a tilted
coupon the ellipse they image as measures the tilt angle.

Microscope: **the Elikliv EM4K, not the EM4K-AF.** Same 4K sensor (3840 × 2160), but manual
focus via a wheel on the lens instead of TOF autofocus, and autofocus is a liability for
calibrated work rather than a convenience. 4K is the only spec that translates directly into
measurement quality and it is 3× the 720p coin scopes. Skip the EDM9 mid-range: 1080p for
three times the EDM4's price buys stand and screen, not pixels.

Then **verify USB capture resolution before trusting it.** No Elikliv model states a USB
output resolution and none claims 4K over USB — USB 2.0 cannot carry 4K30 uncompressed. If USB
delivers 1080p, capture stills to the SD card or via HDMI instead, or the 4K purchase buys
nothing. This is acceptance-test item 3 in `docs/DEPTH-PREDICTION.md` §5.3 and it is the one
that decides whether the model choice mattered.

Lighting should not drive the purchase — a clip-on LED ring is $10–15. Ring/coaxial light is
what measurement wants (even illumination, unbiased edges); grazing side light is what seeing
topography wants. The EM4K ships with flexible side lights only.

### 7.2 Step-wedge coupon, measured by mass loss  *(bench, blocks 7.6 and 7.7)*

Measures the removal efficiency `η` per material — `η = Δm / (ρ · P · t)`, directly from the
scale, no depth measurement needed — and finds where depth per pass stops being constant.

Stainless 304 first: it is the only metal with literature to check against
(η ≈ 0.7–2.7 × 10⁻³ mm³/J depending on pulse energy). A 304 result far outside that range means
suspect power calibration or focus before the physics. Brass, copper and aluminium have **no**
measured η in the literature — these coupons produce the first numbers, not a check on them.

`--calibrate` already emits the coupon (see 1.4 and 1.4b). What is missing is the
measurement and the entry path for it.

- **Mass loss is the primary instrument**, not the dial indicator. A 0.001 g jeweller's
  scale resolves 0.29 µm of average depth on a 20 × 20 mm brass pocket, reaches 1 %
  precision above ~29 µm, and does not care about the burr at the pocket edge. The
  Mitutoyo 513-402-10E on hand is the cross-check: 12.7 µm graduation, 5.1 µm
  repeatability, and only 0.76 mm of travel against a 1.1 mm coin target.
- **A step wedge cannot be weighed in one go, and this changes the coupon.** Mass loss gives
  the total removed from whatever goes on the pan, so ten zones engraved in one job yield one
  number, and one number cannot fit two constants across ten levels. Use **one small coupon
  per power level, all cut from the same plate** — 25 × 25 × 3 mm carrying a single 15 × 15 mm
  zone. That puts a 191 mg signal on a 16 g coupon (1:83) instead of on a 191 g plate
  (1:1000), which is the difference between a comfortable reading and one fighting thermal
  drift. **Zones must be at least 10 × 10 mm, 15 × 15 mm by preference**: at 5 × 5 mm a scale
  count is 4.7 µm and the scale's whole advantage over the dial indicator is gone.
  **Done 2026-09-24: `--calibrate --mass`** writes exactly this coupon — a single uniform zone,
  15 mm on 25 mm by default, no engraved labels (they would count as removed mass), 8-bit, with
  a worksheet carrying the controls, a row per coupon and the arithmetic to η. It warns on a
  zone under 10 mm, a border under 2 mm, a coupon heavier than the scale, and an unknown
  material. The original wedge coupon is unchanged but now says it is for a gauge or
  microscope, not the scale.
- **Scale spec: 0.001 g resolution and ≥50 g capacity.** The common cheap milligram scale is
  20 g full scale, which will not weigh a 40 mm brass coin blank (32 g) let alone a 4 mm one
  (43 g). Needs a calibration weight, a draft shield, and a check mass weighed at both ends of
  every session — cheap scales drift with temperature, and mid-session drift looks exactly
  like a depth measurement.
- Record the **roll-off** deliberately. Depth per pass falls as the pocket deepens — defocus,
  debris and plasma shielding, the beam clipping its own wall. **No published depth-vs-passes
  curve exists for these metals**, so a pass-count ladder (10/20/40/80/160, with and without a
  Z step) is the only source. Where it rolls off is a result in its own right.
- **Done when:** `η` is measured per material with its settings vector and a stated spread, and
  the depth beyond which constant `d_pass` is not trusted is stated as a number.
- Blocked on: brass and stainless coupons cut at the chosen pass count. Nothing else.

### 7.3 Terrace-width prediction  *(code only — ship this first)*

Needs **no measured constant whatsoever**, which is why it goes ahead of everything else.
Pure cartography: the spacing between contour lines on a slope.

```
terrace width = level step / |∇level|
```

Compare that width against the spot size and the line interval. Where the terrace is wider
than the spot, the slicing will be visible as contour banding; where it is narrower, the
beam smears it away. This turns "you may see terracing" into *"terracing will be visible
here, and here"*, drawn on the map.

The relief preview already quantises to a slice count and renders in milliseconds, so the
gradient field is most of the way to existing.

- **Done when:** the analysis reports terrace width statistics, and the preview can overlay
  the regions predicted to band at the current pass count.
- Depends on: nothing. Available today.

### 7.4 Spike detection and noise floor  *(code only)*

Two related reports, both purely statistical.

- **Immerkær fast noise variance** — one convolution with `[[1,-2,1],[-2,4,-2],[1,-2,1]]`
  gives a noise-sigma estimate in a single pass. Known to **under**-estimate at low noise,
  so report it as a floor rather than a figure, and say so in the output.
- **Spike detection** — isolated pixels far from their neighbours. In a depth map these are
  not texture, they are a single pass firing where nothing was intended, and at high pass
  counts they cut to full depth.

Both matter because a noisy map wastes slice levels on noise: the analysis currently reports
how many distinct levels exist without asking how many of them are real.

- **Done when:** both figures appear in the report with an explicit statement of what they
  can and cannot tell you.

### 7.5 Dither detection  *(code only)*

A dithered source arriving as a depth map is a category error — it encodes tone as pixel
density, and a slicer will read that density as geometry. Detect the characteristic
frequency signature and say so loudly, because every other number in the report is
meaningless on a dithered input.

- **Done when:** dithered input is identified by name (ordered vs error-diffused, if the
  signature separates them) and the report refuses to quote level statistics for it.

### 7.6 Depth model and settings-driven live preview  *(blocked on 7.1 and 7.2)*

**This is the one that was called the holy grail, and it is deliberately sixth.**

**What it is.** While the 3D relief preview is showing, the user adjusts **the same settings
they will type into MakeIt or LightBurn**, and the surface updates in real time to what the
machine will cut — plus warnings for outcomes they will not want. Driven by measured tests,
bootstrapped from the baseline research.

**Model inputs.** Material (brass, copper, stainless 304, aluminium to start), laser source
(MOPA 1064 nm, UV 355 nm, diode ~455 nm, CO2 10.6 µm), rated power, **lens**, and the cut
settings: power %, speed, line density or interval, passes/layers, frequency, pulse width,
focus offset, per-layer Z descent, crosshatch and cleaning passes.

**Settings must be shown in each program's own units and names.** MakeIt's speed is mm/s but
its G-code is mm/min; its "line density" is lines per *centimetre*. LightBurn uses line
interval in mm. The simulator converts internally and never makes the user translate.

**The lens is a first-class parameter, but the model uses what it measures, not its label.**
The lens sets spot size — and fluence goes as 1/w², so it is the biggest single lever — and sets
Rayleigh range, which governs how fast removal falls off with depth (~0.4–2 mm for the fibre
lens, ~0.1 mm for UV). In `d_pass = η·P/(v·h)` the spot does not appear directly; it acts
**through η** (pulse fluence, overlap) and through the defocus decay. So **η is calibrated per
lens**, and the spot radius comes from test T0 / Liu's D², not the lens specification.

**Pipeline, per pass:** `E_DA = P/(v·h)` → onset gate (is fluence above threshold at all?) →
`d_pass = η(material, laser, lens, τ, f/f₀, overlap) · E_DA` → defocus decay using `w(z)` as the
pocket deepens and Z descends → cumulative depth → quantise to the pass count → drive the
existing relief renderer. The surface changes shape as a slider moves because the realisable
depth genuinely changes, not because an exaggeration factor did.

**Capability branch, before any number is shown.** Blue diode and CO2 on bare brass, copper and
aluminium produce **no depth** — cold absorptivity is 1–3 % at 10.6 µm, and copper's
conductivity holds a 10–40 W blue spot hundreds of kelvin below melting. On 304 they give an
oxide/colour mark at ~zero depth. The simulator must say so plainly rather than predict a
small number. UV 355 nm can remove metal but **no bulk-metal constants exist at all** — show it
as uncalibrated, bounded by an energy-balance ceiling.

**Only stainless ships with a working prior.** Brass, copper, aluminium and UV ship explicitly
marked **uncalibrated**, with absolute depth hidden until the user's own tests exist. This is
the rule below, applied: a seeded prediction must not look like a measured one.

**Interface consequence, and it lands before the model does.** Once depth is predicted rather
than described, **the depth map and the cut settings stop being independent.** The tuner will
adjust total engraving depth and reshape the grey-to-depth correlation, and both of those
change what settings are correct. A revised map sent back without its revised settings
produces the wrong physical depth; revised settings without the map are equally wrong.

So any external interface — the MakeIt CLI handshake under discussion, a LightBurn write-back,
anything — **must carry settings in both directions from the start.** An image-only interface
is not a smaller first version of the right interface; it is a different interface that has to
be broken later. Designing for this now costs one JSON field list. Retrofitting it costs
another round of vendor scheduling, with a different engineer who lacks the context.

Parameters that have to travel, because each one moves predicted depth: power, speed, pass or
layer count, line density or interval, pulse frequency, pulse width, focus offset, any
per-layer Z descent, and the material. Plus, on the map itself: **its physical size in
millimetres on the workpiece** — without that, no depth statement means anything — along with
its bit depth and which operation it belongs to.

Requirements that are easy to lose sight of once it starts working:

- **Provenance on every number.** A prediction from an A-grade fit and a prediction seeded
  from a published constant must not look alike on screen. The uncertainty band widens
  visibly for the seeded one, or the feature is dishonest.
- **Refuse to extrapolate.** Outside the tested envelope the answer is "no evidence", never
  an interpolated number. This is already the rule in the LaserTuner evidence schema and it
  transfers unchanged.
- **Grades never blend.** An A-grade measurement supersedes a C-grade prior. It is not
  averaged with it.

### 7.7 Operating window — slag and visible layering  *(blocked on 7.2)*

The part that cannot be rendered, only bounded.

Geometry can be drawn faithfully. Slag, discolouration and heat-affected zone cannot — they
depend on assist gas, debris evacuation, ambient conditions and the specific alloy. If the
render shows both with the same confidence, the tool has lied about one of them.

So: record the settings where slag appeared on each coupon, mark the region of the settings
space that produced clean cuts, and shade anything outside it as untested. **The seam
between "computed" and "observed" must be visible in the UI**, not buried in a tooltip.

**Flags with a published numeric onset** — only three, all from 316L stainless on a 100 W MOPA,
used as proxies for 304 and as placeholders elsewhere:

| Flag | Computed from | Onset |
|---|---|---|
| Blackening | area dose per pass `E_DA` | > 5.3 J/mm² |
| Melt collapse (groove fills instead of clearing) | frequency at a given pulse width | 350 kHz @ 280 ns, 300 @ 380 ns, 250 @ 500 ns |
| Roughness | pulse and line overlap | best removal-vs-roughness at ~50 % overlap; interlaced, angle-rotated scanning cut Sa 84 % |

**Qualitative flags only, no numeric onset exists:** heat tint on 304 (keyed on the *final*
pass, since deeper passes ablate earlier tint), visible crosshatch/layering (fixed hatch angle
across many layers), slag accumulation (layers since last cleaning pass), recast cracking and
porosity (pulse width ≥ 50 ns on 304), warp (high pulse energy on small fields), zinc loss in
brass (long pulses, high dose), back-reflection on polished copper, aluminium and brass.

**No evidence at all, so the tool must say "no evidence":** burr/rim height, cone structures on
the pocket floor at ns, a back-reflection threshold, a dezincification onset.

- **Done when:** the preview marks regions whose settings sit outside anything measured, and
  the report can name which coupon a clean-cut claim came from.

### 7.7b Guided calibration — test setup and result entry  *(schema can start now)*

The research fixed what a calibration result looks like, which answers the old objection that a
form built before the first coupon is a guess. A result is: **the full settings vector, lens,
source, material grade and surface state, `m_before`, `m_after`, beam-on time, photos under
fixed lighting, and a defect classification.** Every result is stored as grade A and
**overwrites** the matching prior — never averaged with it.

The tests, in order (`docs/research/laser-ablation-baselines.md` §7 has the detail):

| Test | Finds |
|---|---|
| T0 focus and spot | true focus, spot size, Rayleigh range for the fitted lens — **everything else depends on it** |
| T1 power linearity | whether power % is linear in delivered power, in MakeIt and in LightBurn |
| T2 frequency / pulse-width screen | the source's peak-output frequency `f₀` and the melt-collapse limit |
| T3 η factorial | η and how pulse width, frequency and overlap move it — 304 first |
| T4 depth-vs-passes ladder | roll-off and the value of a Z step |
| T5 defect map | blackening, tint, slag and banding onsets; value of rotated or interlaced hatching |
| T6 material transfer | η for brass, copper and aluminium |
| T7 null and UV checks | confirm diode/CO2 produce no depth; bound UV |

DepthView should **generate each test's coupon and its settings sheet**, then walk the user
through entering results. Pocket size follows the scale, not the research's 5 × 5 mm suggestion:
**15 × 15 mm zones on separate coupons**, because at 5 × 5 mm one milligram is 4.7 µm of brass.

Build the data schema now. Build the entry UI after the first T0 and T3 coupons are cut, so it
is shaped by real numbers.

- **Done when:** DepthView can emit T0–T7 coupons with settings sheets, and a result entered
  against one replaces its prior in the baseline table with provenance attached.

### 7.8 Open question — where this documentation lives

`docs/DEPTH-PREDICTION.md` is in DepthView, which is public and is where contributors land.
The evidence store it draws on is LaserTuner, which carries the A–E grading scale in
`Documentation/Evidence_Sources.md` and **has no git remote** — it is local-only, so no
contributor can reach it today. Either the document stays here and references a repository
nobody else can see, or LaserTuner gets published. Decide before asking anyone to
contribute measurements.

### 7.9 Target depth against simulated depth, and a suggested Z advance  *(design, 2026-09-25)*

**The blank comes first (done 2026-09-25).** Diameter, thickness and target depth are one
program-wide `Blank`, shown in the tuning dialog and the relief window and moving together.
Target depth defaults to 18% of thickness (Lee's range: 15–20%) until typed, with an Auto
button to hand it back; past 30% of thickness it says how little floor is left, and past 100%
that the cut would go through. The tuning card quotes depth per pass against it, and the 3D
view stands the relief on a slab of the real thickness.

**Target versus simulated must be obvious.** When 7.6 exists, the two tuning panes are the
comparison: left = the map drawn at target depth, right = what the simulator predicts these
settings cut. Plus one line - "target 1.10 mm, predicted 0.86 mm, 22% short" - and a
depth-by-pass chart with the target as a horizontal line. Never only a number.

**Z advance is an output, not an input.** Removal per pass depends on how far the floor is
out of focus, and how far out of focus it is depends on how much has been removed - so they
are solved together, pass by pass, and the suggested Z advance is the step that keeps focus
on the descending floor. It is not "target depth ÷ passes": when removal and Z advance are
mismatched the floor drifts out of focus, removal falls, and the error compounds in both
directions (too little advance leaves focus above the floor; too much puts it below).

- **Overriding it warns with the consequence, not a word.** e.g. "At 0.020 mm per pass focus
  is 0.4 mm below the floor by pass 50; predicted depth 0.71 mm against 1.10 mm target."
- **Scale the warning to the lens.** Fibre Rayleigh range is ~0.4–2 mm, so a 1.1 mm relief
  may stay inside the depth of focus with little or no advance - say so and do not nag. UV at
  ~0.1 mm is where it matters. Deeper cuts make it matter for fibre too.
- **Allow deliberate defocus.** Running slightly out of focus for a smoother floor is a real
  technique; the override can be marked intentional and the warning then stays quiet.
- **Needs a test:** a focus ladder - identical settings at several Z offsets - added to the
  7.7b sequence, so the defocus falloff is measured per lens rather than assumed.
- MakeIt's "descent per layer" and LightBurn's per-slice Z step are both a single constant,
  so the suggestion is a constant; if removal varies enough across the depth that no constant
  keeps focus, say that too.

---

## 8. WeCreat MakeIt integration — proposed 2026-09-24, awaiting reply

**State: ball is in WeCreat's court.** They approached us — "after further evaluation by our
software team, we would like to move forward with the collaboration on Depth View" — and asked
whether they should study integration themselves or be guided. A full proposal went back on
2026-09-24. Expect a slow reply; do not chase it.

### What was proposed

Their team said MakeIt's framework is Electron.js and asked how to integrate DepthView into it.
**DepthView is .NET 8 + Avalonia and cannot be embedded in an Electron app** — correcting that
premise was the first thing in the reply, because everything downstream of it was wasted effort.

Three options offered, A recommended and named as the one to pick if only one is done:

- **A — a command-line handshake.** MakeIt gains two commands: export a project's depth map
  plus the settings of the operation it belongs to; import a revised map and revised settings
  back. Settings as JSON, image as a PNG file beside it (never base64 — a 4096² 16-bit image
  bloats badly). **The argument that makes this better than what we first asked for: WeCreat
  never opens the `.wws` format, and MakeIt stays the only program that writes it**, so a bug
  on our side yields a rejected import rather than a corrupted project. Either program can
  drive; we offered to build whichever side they prefer.
- **B — document the `.wws` depth-map structures.** The original ask, now the fallback.
- **C — WeCreat reimplements the analysis in MakeIt.** MIT permits it outright; DepthView
  becomes their reference implementation. The only route that literally puts it inside Electron.

We also flagged that **we do not know whether MakeIt has any command-line surface at all**, and
that if not, A is a larger piece of work than it sounds.

### Open questions to WeCreat, none answered

1. **Does an export give the map as the user imported it, or after MakeIt's own reduction?**
   This decides whether A is worth building at all — tuning a map whose precision has already
   gone is the exact failure the tool exists to catch.
2. **Is the X sample pitch configurable?** (asked 2026-09-14) Measured at 0.1 mm against
   0.0333 mm in Y, and line density appears not to affect it. See CLAUDE.md — if this stands,
   sampling rather than spot size is what limits detail through MakeIt.
3. **What decides whether a multi-layer job expands into the G-code?** (asked 2026-09-14) One
   60-layer job staged as a single layer; an earlier one expanded to 11.2 M lines.

### What to build, and when

**Nothing yet for A, B or C — all three are blocked on their reply, and all three imply
different work.** Do not speculatively build an interface to a spec that does not exist.

**One thing is not blocked.** `Integrations/WeCreat/Gcode/GcodeStream.cs` was added and
**nothing consumes it.** Reading the generated `.gc` is a documented, supported user action
(`Ctrl+Shift+P` per WeCreat's own KB), needs no vendor cooperation, and yields the ground
truth: distinct S values, X and Y sample pitch, layer structure — what the machine was actually
sent rather than what the project file claims. That is a real analysis feature on its own and
it is the only way to verify that any future round trip preserves precision. **Build the
consumer.**

### Standing constraints on this thread

- `.wws` protection is **never** to be defeated. Stated in the reader, in `CONTRIBUTING.md`,
  and now in writing to WeCreat. Pull requests that reverse-engineer it are declined.
- **Depth prediction must not be described as a capability.** It is not built. The reply said
  so plainly, which is why it can be trusted on everything else.
- Support correspondence is citable; the private beta stream is not. See CLAUDE.md — the dates
  nearly touch, so check which stream a fact came from rather than assuming.
