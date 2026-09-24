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

**"Galvo" is two different words, and the README equivocated between them.** The Lumos Ultra
section argued: 3D Slice is galvo-only, a MOPA Lumos Ultra is a galvo, therefore LightBurn is
its 16-bit relief path. Both premises are true. The conclusion is false.

LightBurn grants galvo features by **device class in LightBurn**, not by what the machine is
physically made of. **The Lumos Ultra connects as a GRBL/GCode device** — streamed G-code, and
a serial buffer-size setting, which is a GCode-device concept galvo devices do not have. That
was established during work with LightBurn staff on a buffer-size bug on this exact machine,
so it is first-party and not inference. The public citation is **LightBurn's own galvo driver
documentation**, which separates GRBL-style devices from the native Galvo class (EZCad2/EZCad3
and BSL boards). The sibling `WeCreat-Lumos-Ultra-LightBurn-Config` repo is where this was
caught. **It went private on 2026-09-19, so never link it from here** — it is unreachable to
every reader of this repo, and the README was cleaned of its one citation before the switch.
The general rule stands beyond this instance: **a public README must not depend on a
repository that can disappear**, and the sibling projects are exactly the kind that can.

Corrected in the README 2026-09-19, left visible rather than deleted. **Nothing else changes**
— the 16-bit and band-spread arguments were never about the Lumos; they are about machines
LightBurn treats as galvo devices, which LightBurn documents as EZCAD2/EZCAD3 and BSL-class
controllers, and which is most 3D Slice work.

**A second lesson, about how to write a correction.** The first attempt led with the retracted
sentence, quoted prominently, and explained why it was wrong underneath. Lee then read the
updated README and reported the wrong claim as still live — **the author of the project
misread his own corrected document.** A reader skimming before sharing it would do the same.
So: **state the correct claim first and in full; put the retraction below it, short and
clearly subordinate.** Publishing mistakes rather than hiding them is right, but a retraction
that leads with the error is a retraction that re-publishes the error.

The generalisable form: *a spec that sounds like a hardware fact may be a fact about a
software device profile.* Also note this was caught by a second repository contradicting this
one — **when two of these projects disagree, one of them is wrong and it is worth finding out
which before either gets quoted.**

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
accuracy, not spot size). The "256 depth layers" figure is **a software figure** — WeCreat
support states their software uses 8-bit processing and supports up to 256 processing layers.

**Measured through MakeIt 3.0.6, and it reframes what limits detail.** From a genuine 16-bit
4096 × 4096 source with 61,898 distinct levels, on a 40 mm blank, what actually reaches the
machine is:

| | |
|---|---|
| Distinct S (power) values | **239** |
| X samples | ~397, i.e. **0.1 mm pitch, 10/mm** |
| Y scan lines | 1,195, i.e. **0.0333 mm pitch, 30/mm** |

Two consequences, both counter to how this project has been framing things.

1. **X and Y are not sampled equally**, and "Line density 300" — which is lines per
   *centimetre*, not per inch or per mm — sets Y only. X appears fixed at 0.1 mm.
2. So on this path **the binding constraint on detail is the 0.1 mm X sample pitch, not the
   6–8 µm spot.** That is a factor of 12–16. "Is this map finer than my spot can cut" is the
   right question for a galvo running its own slicer; through MakeIt the sampling runs out
   first, by more than an order of magnitude.

**Provisional.** Measured from one job's generated G-code, with settings written down before
the file was read. Whether the X pitch is configurable was asked of WeCreat on 2026-09-14 and
is unanswered. Do not bake it into analysis defaults until that comes back.

Reading the generated G-code is a **documented, supported** user action, not a workaround:
WeCreat's own KB gives `Ctrl+Shift+P` (`Cmd+Shift+P` on macOS) → open G-code files → `.gc`.
`src/DepthView/Integrations/WeCreat/Gcode/GcodeStream.cs` exists to read those files.

**Both came from WeCreat support, and both are publishable — check which stream a fact came
from rather than treating "WeCreat" as uniformly restricted.** There are two:

1. **Ordinary support correspondence** — `support@wecreat.com` via Zendesk, answering
   questions anyone could ask. The 27 Aug 2026 reply carries both facts above and is the
   source to cite. Allen's exact words: *"our software uses 8-bit processing, and it supports
   up to 256 layers for processing. The 256-layer setting refers to the number of processing
   layers available in the software."* And on the spot size: the published 0.0019 mm figure
   *"was not the actual UV laser spot size, but rather a parameter describing the machine's
   motion accuracy"*, with the real 355 nm spot *"approximately 6–8 µm"* — and WeCreat said
   they would revise their materials. No confidentiality marking, no NDA, no embargo.
2. **The private beta** — from 28 Aug 2026, the LightBurn test build, its link and its config
   files. **That one is confidential and stays out of every public artefact.**

The dates nearly touch, which is exactly why the distinction has to be checked rather than
assumed. Note also that "let us keep discussion in this email thread" in that reply is channel
consolidation — it appears immediately after Allen praises what Lee had already shared
publicly — and is **not** a restriction on publishing.

**This was got wrong once, in the cautious direction.** The attribution was stripped from the
README on a guess that it might be beta-adjacent; reading the actual email showed it was not.
Over-caution is the cheaper error but it is still an error: it cost a well-grounded citation
and left a weaker unsourced claim in its place. **Read the source before deciding a fact is
unpublishable.**

One precision point worth keeping: Allen said 256 is a *software* figure. He did **not** say
"not a controller limit" — he never addressed the controller. The README quotes him directly
rather than paraphrasing, because the paraphrase was stronger than the source.

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

## Measurement and depth prediction (added 2026-09-11)

`docs/DEPTH-PREDICTION.md` is the research document: the physics, published constants with
grades, how to measure a test engraving without a laboratory, and a 26-step protocol in §5.7.
`TODO.md` §7 is the stepwise plan. **Read those rather than re-deriving any of it.**

The shape of the plan, because it is easy to get backwards: steps 7.3–7.5 (terrace width,
noise floor, dither detection) need **no measured constant** and ship first. The depth model
(7.6) waits on the two coupons that produce `δ` and `F_th`, because seeding it with invented
constants makes a dishonest prediction indistinguishable from a fitted one.

**Four retractions happened in one session, and the pattern matters more than the facts.**
Every one was a confident number stated without checking, then corrected when checked:

- Ultrasonic power was given as "50–100 W/L". The industry figure is **8–15 W/L**. Wrong by
  5×, and the advice inverted with it: the risk is **cavitation erosion**, not insufficient
  power, because eroded substrate is indistinguishable from engraved depth.
- Field of view was inferred from a listing's "Real Angle of View: 16°". The field is junk —
  a sibling product lists **178°**, which is an IPS panel viewing angle. Never reason from it.
- Step-wedge zones were to be cut sequentially on one plate. **Separate small coupons win**:
  the same 191 mg signal sits on 16 g rather than 191 g, so 1:83 instead of 1:1000.
- Oxide was called a hazard. It is **0.4–1.4 mg** — under 1 %, about the same as a fingerprint.

This is the same failure as the LightBurn 16-bit concession above, in a new domain: an
authoritative-sounding number accepted without a source. **Check the figure before it goes in
a document a contributor will follow.**

Facts worth not re-deriving:

- **Megapixel claims in this product class are interpolation.** Find the *video* resolution,
  which cannot be faked upward. Silence about 4K is evidence against it.
- **Mass loss is the primary depth instrument**, not any gauge: `depth = Δm / (ρ·A)`, 0.52 µm
  per 1 mg count on a 15 × 15 mm brass zone. The microscope only measures spot size.
- **`F_th` needs no microscope.** A ladder of short segments at stepped power, read at
  millimetre scale — the first segment that marks is the threshold.
- **Same-frame calibration** (the scale in the photograph with the subject) defeats autofocus
  drift, stand repeatability and session drift at once. It also measures tilt angle, so a
  3D-printed wedge needs no accuracy.
- **"Accuracy" is usually the wrong spec.** Every measurement here is a *difference* taken
  minutes apart on one instrument, so **repeatability governs**. This is why the Mitutoyo's
  0.0002″ figure is usable — it is repeatability and hysteresis, not an accuracy claim,
  whatever the retail listing calls it.
- **Bonded recast stays on the coupon and is correctly counted.** Only loose debris must go.
  The difference between a mass result and a profile result *is* the recast.
- **Cleaning chemistry that etches produces fake depth.** Water and dish detergent only; no
  ammonia on brass, no acids. The blank-coupon control detects a violation.

---

## What is owed

The calibration coupon (`--calibrate`) is built but has never been cut. Until it is
engraved on brass and stainless and measured, several defaults are honest admissions
rather than knowledge — the rim ramp defaults to none for exactly this reason. Measurement
entry and the inverse LUT are deliberately unbuilt: a form invented before the first coupon
is a guess about what the numbers look like.

**Status as of 2026-09-23: the microscope and calibration slides have been bought, and
nothing has been measured.** The milligram scale and dial indicator were already owned. **No
part of the measurement programme has started** — not the microscope acceptance test, not the
noise floor, not the blank-coupon control, and no coupon has been cut. Every constant in
`docs/DEPTH-PREDICTION.md` is still a published prior, not a measurement of this machine.

So do not describe depth prediction as a capability, to anyone. The method is written up and
the instruments are in hand; the calibration it depends on has not been done. **This project
has repeatedly had to retract confident statements, and a vendor's engineering team acting on
an overstated roadmap would be the most expensive version of that mistake yet.**

The first code blocker is `--calibrate` itself: it sizes the wedge to fit the blank, where the
measurement needs **zones of at least 10 × 10 mm on separate 25 × 25 mm coupons** (see TODO
7.2). **Fix that before cutting anything**, or the first coupon produces numbers the scale
cannot resolve.

`TODO.md` has the rest, with enough context to pick each item up cold.
