# Working on DepthView

Context for anyone — human or model — picking this up cold. `README.md` is for users,
`TODO.md` is what to do next; this is what you need to know before you do anything at all.

DepthView inspects candidate depth maps: what the file claims to be, what its pixels
actually contain, and whether the two agree. It then tunes them for engraving. Cross
platform, Avalonia, net8.0, published as self-contained single files for 7 RIDs.

---

## Build, run, test

```powershell
# Stop the app first. A running DepthView.exe locks the build output and the build fails
# with MSB3027. This has cost time more than once.
Get-Process DepthView -EA SilentlyContinue | Stop-Process -Force

dotnet build src\DepthView\DepthView.csproj -c Release -warnaserror --nologo -v minimal

# The stable copy to actually run. Never point a shortcut at bin\Release - that path is
# what causes the lock above, and it is a framework-dependent dev build rather than the
# artefact users get.
powershell -ExecutionPolicy Bypass -File publish.ps1 -Rids win-x64
#   -> publish\win-x64\DepthView.exe
```

Tests, both of which CI runs on Windows, macOS and Linux:

```powershell
python tests\make_fixtures.py            # if tests\fixtures is empty
python tests\make_textures.py            # relief_demo.png, a different generator
DepthView --report tests\fixtures --summary --out summary.txt
python tests\check_report.py summary.txt # 12 fixtures, exact expected classification

# check_fit.py works on tuned copies written beside the repo root; see build.yml for
# the exact --tune lines that produce them.
python tests\check_fit.py
```

`docs\make-screenshots.ps1` regenerates every README image. The app screenshots its own
windows (`--screenshot`) and renders its own relief art (`--render`), so documentation
images are reproducible rather than hand-grabbed.

### Releasing

`.github/workflows/release.yml` fires on `push: tags: ['v*']`. Pushing a `v*` tag builds
all seven RIDs and **creates a public GitHub Release** — so tagging is publishing, not a
bookkeeping step. `workflow_dispatch` with `dry_run` builds the artefacts without releasing;
use that to check the machinery.

`<Version>` in `src/DepthView/DepthView.csproj` is the single source of truth and feeds the
About box. Bump it in the same commit as the tag.

---

## Conventions that are load-bearing

- **Black is deepest, white is untouched.** LightBurn's 3D Slice default and MakeIt's too.
  `--invert` exists for art authored the other way round.
- **Never write over the original.** Every tuning path produces a new file. No exceptions.
- **Never resample a depth map.** Interpolation invents grey levels that were not in the
  file, which is the exact fault this program exists to detect. Fitting artwork inside a
  rim grows the canvas by padding instead — see `DepthCanvas`.
- **Measure, do not assert.** Every claim the tool makes about an improvement is a number
  it computed. `--tune` re-reads the file it just wrote and analyses it as a stranger's
  file; the Tune dialog does the same on save. If a prediction and a measurement ever
  disagree, the prediction is what is wrong.
- **The pass count is a parameter, never an assumption.** Toolchains have different
  ceilings and those change between versions. "At N passes, what do I get" is true
  everywhere and stays true.
- **One implementation per job.** `TuneJob` is shared by the dialog and the command line
  so a file written either way with the same settings is the same bytes.

---

## Hard constraints

- **No third-party depth map is ever committed.** `samples/` is an *allow-list* in
  `.gitignore`: everything there is ignored unless named explicitly. This exists because
  `git add -A` once nearly published someone else's artwork. Drop anything you like in
  that folder; git will leave it alone. Adding a sample has to be a deliberate act by
  someone who has checked they may redistribute it.
- **WeCreat correspondence.** Their support asked that a private build link, a config
  file, and the specific text of internal email stay within that thread and not be shared
  externally. High-level progress and summarised findings are fine and were encouraged.
  Honour this — do not reproduce those details anywhere in the repo or in public posts.
- **Do not reintroduce removed attributions.** A previously credited third-party coin
  design was removed from the examples at the owner's request; `git grep` should return
  nothing for it.

---

## Corrections already made — do not re-make them

**LightBurn and 16-bit.** A quote from an authoritative source said 3D Slice thresholds to
256 levels. It was conceded without checking, the README was rewritten, and a feature was
built around a 256 ceiling. It was wrong: **LightBurn 2.1 added 16-bit depth map support**,
and the original README had been right. The quote was accurate about an older version.

The lesson, which is the reason this paragraph exists: *an authoritative-sounding quote
still has a date on it.* Check what version a claim describes before rewriting anything.

**16 bits matter below 256 passes too.** The docs' framing is about how many depths a file
*contains*. There is a second, independent effect: a slicer cuts the level range into equal
bands, levels are integers, so unless the pass count divides the range exactly some bands
hold more levels than others and the terraces come out unevenly spaced. With 256 levels
only the powers of two divide evenly — 8 pass counts out of 255 — while 127 of 255 give a
2:1 spread. A genuine 16-bit map never exceeds 1.004 anywhere in that range. This is the
`band spread` column. Raised by Nathaniel Klumb on the forum and verified before it went in.

**"Wasted passes" was wrong, and stretching is not a repair.** Two corrections from Finn65,
and the second one is the sharper.

A slicer masks each pass by a threshold. When two consecutive thresholds fall in a gap where
no pixel value exists, the second pass fires on the same mask as the first — it still cuts,
it still removes material. What it does not do is add a distinguishable step. Calling that a
"wasted pass" implies an idle laser and is simply false. `PassesAt` now splits the job three
ways instead: **uniform** (mask covers everything, so it deepens without shaping — a flat
recess), **relief** (mask shrinking, the only passes carrying shape), and **empty** (nothing
in the mask at all). They sum to the pass count.

And stretching a narrow range does not recover wasted resolution. Work it: a map occupying
34% of the range resolves 88 depths into 87 passes of relief — 1.01 levels per pass of relief
depth, which is the maximum possible. Nothing is being wasted. Stretching makes the relief
~3x deeper and gets more levels *because* it is deeper; levels per unit of depth are
unchanged. If the narrow range was deliberate, stretching overrides the intent by 3x, and
nothing in the file says which it was. So the tool reports and does not prescribe — no
warning fires merely because stretching would add depths.

The general lesson, which is the reason this is here: the *numbers* stayed on the right side
of the file/material line, and the *language* did not. "Wasted", "fixing", "reclaiming" are
physical claims dressed as file analysis. Watch for that wording creeping back.

**Floor polarity.** An early design assumed a white (untouched) floor. Lee's coins use a
black floor — deepest, cut away. Both are supported now via the two level points, and
nothing should assume one convention again.

---

## Traps this codebase has already sprung

- **`--` inside an XML comment** makes the file unparseable. Bit an AXAML comment
  (AVLN1001) and is now guarded in `check_fit.py` for the generated SVG, which leads with
  a long prose comment.
- **Defining your own `InitializeComponent()`** in an Avalonia code-behind suppresses the
  generated named fields, and every `x:Name` reference throws `NullReferenceException` at
  runtime. Don't.
- **Layout breaks at small window sizes, not on your monitor.** A CI runner at 1024×768
  found the buttons and verdict card pushed off the bottom of the main window. `--window
  <w> <h>` forces a size below the minimum so this is checkable, and CI captures both
  windows at 1024×660 on every push.
- **Inversion runs last.** Anything that must come out untouched has to be written as its
  mirror *before* the flip. `--invert` plus a rim once cut the rim to full depth — the one
  part of a coin blank nobody wants the laser to reach.
- **Padding fill is not cosmetic.** Fill the grown canvas with the design's own background,
  not with "untouched", or the boundary of the source image appears on the coin as a raised
  square. Both renders are in the README.
- **Brightness thresholds are not a content test.** "Above a fifth of the range is content"
  only holds for art on a black floor, reads a white-floor map backwards, and inverts again
  the moment someone ticks Invert. Take the background from the image border instead, and
  measure content only inside the original rectangle — after padding, the border is our own
  fill, and asking it what the background is just reads back our own answer.
- **An image frames as a rectangle.** LightBurn's Bounds, Hull and Contour framing all see
  an image as its bounding box, whatever is drawn inside it. Aligning a round design to a
  round blank needs a *vector* — hence `--outline`. No amount of white or transparency in
  the pixels can help, because the framer never looks at them.
- **Alpha in a depth map is a hazard, not a feature.** A tool that composites transparency
  against black turns those pixels into full depth. DepthView warns when it *sees* an alpha
  channel, so it has no business writing one. `samples/alpha-behaviour-test.png`
  (generated, untracked) is a four-quadrant tile that reveals which behaviour a slicer
  actually has, if this ever needs settling.
- **PowerShell through the device shell mangles `$_`, `$var` and nested quotes.** Write a
  `.ps1` to temp and run it with `-File`, or use `cmd /c findstr`.

---

## Physical numbers in use

40 mm coin blank, 4 mm thick, brass and stainless. Rim measured at slightly under 1 mm wide
and 0.1 mm deep. At 4096 px across a 40 mm blank: 102.4 px/mm, 2601 dpi, 9.8 µm/pixel.
WeCreat Lumos Ultra UV spot is 6–8 µm (the 1.9 µm figure sometimes quoted is motion
accuracy, not spot size). The "256 depth layers" figure is an 8-bit software representation,
not a controller limit — corrected by WeCreat support.

---

## Integrations (added 1.3.0)

`src/DepthView/Integrations/` — `Common/` holds the format-neutral job model, `LightBurn/`
and `WeCreat/` hold the readers, `LightBurn/Control/` the UDP client.

Facts established by opening real files, not from memory. Do not re-derive these; do
correct them if a file disagrees.

**`.lbrn2` is plain uncompressed UTF-8 XML.** The "2" is not a container change.

Layers are elements whose name *starts with* `CutSetting` — `CutSetting`, `CutSetting_Img` —
carrying a **lowercase** `type` attribute. Their parameters are **child elements**, each with
a capital-V `Value` attribute. Shapes are `Shape` elements with a **capital-T** `Type` and
their parameters as **attributes**; the image is base64 in a `Data` attribute and the
transform is an `XForm` child holding six space-separated numbers. That casing difference is
real, not a transcription error. Layer `type` values seen: `Cut`, `Scan`, `Tool`, `Image`.

**LightBurn omits any parameter at its default.** Absent is not zero. Every field in
`CutLayer` is nullable for this reason, and nothing downstream may substitute a default
silently — a missing pass count changes every depth figure quoted against it.

**Embedded bitmaps are stored bottom-up**, LightBurn's bed having Y increasing upward.
Verified against two projects whose `XForm` disagreed on the Y sign: both stored the source
flipped vertically and byte-identical otherwise. `ImageData.FlipVertical()` undoes it by
reordering rows. Never resample to fix orientation — an arbitrary rotation in the transform
is *reported*, not applied.

**`XForm` scale is the length of each basis vector**, not `m[0]`/`m[3]`. A rotated placement
has near-zero on the diagonal, and reading it naively reports a size of zero.

**Still unknown:** the `ditherMode` string LightBurn writes for 3D Sliced. That mode is
galvo-only and every sample to hand was saved against a GRBL profile, so the reader matches
on the words "slice"/"3d" and reports any image mode it does not recognise. The docs do
establish that 3D Sliced's "Number of Passes" is the slice count. Frequency and pulse width
are left null on purpose: no fibre sample, so the unit is unconfirmed.

**UDP control** — send 19840, listen 19841. Community knowledge, not documentation, which is
why `SendRawAsync` exists alongside the typed methods. Observed against LightBurn Core
2.1.04: `PING` → `OK`; `STATUS` → `OK`, an acknowledgement rather than a state, so it cannot
be polled for job completion; `LOADFILE:<good path>` → `OK` and the project opens;
`LOADFILE:<bad path>` → **nothing**; `VERSION`, `GETSTATUS`, `HELP` → nothing. Silence is
also what a dropped datagram looks like, so no method reports success. `START` fires a laser
and is never sent by a test.

**`.wws` is opaque.** Magic `WWS2`, then high-entropy bytes with no readable strings in
3.3 MB. Compressed, encrypted or both. Support depends on WeCreat documenting it; the
program will not attempt to defeat it, and that is written into the reader's own notes
rather than only promised in an email.

---

## What is owed

The calibration coupon (`--calibrate`) is built but has never been cut. Until it is
engraved on brass and stainless and measured, several defaults are honest admissions
rather than knowledge — the rim ramp defaults to none for exactly this reason. Measurement
entry and the inverse LUT are deliberately unbuilt: a form invented before the first coupon
is a guess about what the numbers look like.

`TODO.md` has the rest, with enough context to pick each item up cold.
