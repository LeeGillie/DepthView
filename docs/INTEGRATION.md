# Calling DepthView from another program

This is for developers who want their own application - a laser control program, an image
pipeline, a batch script - to use DepthView's analysis without a person driving the window.
It was written with WeCreat's MakeIt in mind, which is an Electron application, but nothing
here is specific to MakeIt.

**The short version:** ship DepthView's command-line program alongside yours, run it on a
depth map file, and read one JSON document from its standard output.

DepthView is C#/.NET, not JavaScript, so it cannot run *inside* an Electron renderer. It does
not need to. Electron (and almost everything else) can start a native helper program and read
what it prints. DepthView is built as a self-contained executable for Windows, macOS and Linux,
so the host needs no .NET runtime and nothing else installed.

---

## What works today, and what does not

| | Status |
|---|---|
| Analyse a depth map: true bit depth, levels actually used, 8-bit-in-16-bit imposters, range use, depths per pass count | **Built.** `--report --json` |
| Tune a depth map into a new file: level points, stretch, rim, fit, quantise, invert, bit depth | **Built.** `--tune --json` |
| Exchange engraving settings (power, speed, passes, frequency, pulse width, ...) with the host | **Not built.** Designed below so a host can plan for it |
| Read a G-code job: power levels actually sent, line spacing, heights, settings in MakeIt's units | **Built.** `--gcode --json` |
| Where a map will terrace at a pass count, judged against the spot (geometry, not a depth prediction) | **Built** (1.9.0). `--terraces --json` |
| Predict physical depth from material and settings | **Not built.** Needs calibration measurements that have not been made yet. Nothing in the output below is a depth prediction |

---

## Getting the program

Download the zip for each platform from the
[latest release](https://github.com/LeeGillie/DepthView/releases/latest). Each holds a
`DepthView/` folder; the program inside it is:

| Platform | Executable to call |
|---|---|
| Windows | `DepthView/DepthView.exe` |
| macOS | `DepthView/DepthView.app/Contents/MacOS/DepthView` |
| Linux | `DepthView/DepthView` |

Keep `LICENSE.txt` with it. DepthView's own code is MIT licensed, so bundling it in a
commercial product is permitted with the licence text included. The executable also contains
third-party components under their own licences, listed on the About box's licence page
(`DepthView --licence` opens it). One to check
before shipping: SixLabors.ImageSharp is under the Six Labors Split Licence, which DepthView
uses under its open source terms. Whether those terms cover your distribution is a question for
your own review of that licence.

A few things a host should know:

- **Command-line runs never touch the network.** The update check exists only in the window
  and in `--check-update`. A bundled copy should be updated by the host shipping a newer one.
  If a host also opens DepthView's *window*, that window checks GitHub daily and can offer to
  update itself; users can turn that off in About.
- **Check the version** with `DepthView --version`, which prints one line: `DepthView 1.5.0`.
  Every JSON document also carries it.
- **macOS:** release builds are signed ad hoc, which is enough to run on Apple silicon. An
  application that ships DepthView inside its own signed bundle will normally re-sign it with
  its own identity as part of that bundle.
- **Windows:** `DepthView.exe` is a windowed executable, so starting it never flashes a
  console. Standard output still works through pipes, which is what a host uses.

---

## Calling it

Run the executable directly with an argument array - no shell, so no quoting problems with
paths that contain spaces. Read standard output as one JSON document.

```js
// Electron main process, or Node
const { execFile } = require('node:child_process');

execFile(depthViewExe, ['--report', mapPath, '--json'],
         { maxBuffer: 64 * 1024 * 1024 },
         (error, stdout, stderr) => {
  // Exit code 1 means "an imposter was found" - the JSON is complete and valid.
  // Only exit code 2 is a failure, and even then stdout is JSON describing it.
  const report = JSON.parse(stdout);
  const file = report.files[0];
  if (!file.ok) { /* file.error says why */ return; }
  showVerdict(file.verdict.severity, file.verdict.title, file.verdict.detail);
});
```

Node's `execFile` sets `error` for any non-zero exit code; that is expected for exit code 1
and does not mean the output is missing.

### Exit codes

| Code | `--report` | `--tune` |
|---|---|---|
| 0 | every file analysed, none flagged | tuned file written |
| 1 | analysed, and at least one file's verdict is `alert` (an imposter, or a picture that is mostly colour and so not a depth map) | - |
| 2 | at least one file could not be read | nothing written: bad input, refused path, or an error |

### Commands

**Analyse:**

```
DepthView --report <file> [<file>...] --json [--passes 200,256] [--histogram] [--out result.json]
```

- `<file>` may also be a folder (every image in it) or a wildcard.
- `--passes` sets the pass counts for the `passCounts` table. Default:
  `64,100,128,200,256,512,1024`. Pass the count the job will actually run.
- `--histogram` adds every occupied grey level and its pixel count - up to 65,536 pairs for
  a 16-bit map, so only ask for it when drawing one.
- `--out` writes the document to a file instead of standard output.
- Only JSON goes to standard output, and nothing is written next to the input.

**Tune** - always writes a *new* file:

```
DepthView --tune <input.png> --out <output.png> --json [options]
```

The options most useful to a host:

| Option | Effect |
|---|---|
| `--black <level>` `--white <level>` | level points; unstated, they default to the 0.1 and 99.9 percentiles |
| `--no-stretch` | keep levels where they are instead of filling the range |
| `--slices <n>` | quantise to exactly n depths |
| `--invert` | for art authored white-deepest |
| `--bits <8\|16>` | output bit depth (default 16) |
| `--blank <mm>` | blank diameter; also writes the correct DPI into the PNG |
| `--rim-mm <mm>` `--ramp-mm <mm>` | leave an untouched rim |
| `--fit [content\|canvas\|design]` | fit the design inside the rim; nothing is resampled. `design` centres the blank on the design and may crop background (never design) |
| `--cover-rim` | with `--fit design`: if the artwork has its own raised rim, size the blank so that rim lands under the new one |
| `--depth-mm <mm>` `--passes <n>` | the job's target depth and pass count, for the figures in the output |
| `--levels-from <design\|floor>` | level points as the tuning wizard suggests them, from `--survey`'s measurements: from the design without its surround (`design`), or with the background counted as a cut-away floor (`floor`). With `--cover-rim`, only what is inside the drawn rim counts. `--black` / `--white` still win |
| `--flat <leave\|smooth\|flatten,...>` | one word per nearly level area, in `--survey`'s order: `smooth` removes pixel noise only, `flatten` makes the area one level |
| `--uniform-surround` | (added 1.8.0) when the surround is not one level - shaded, vignetted, or marked by the program that exported it (`--survey`'s `background.shaded`) - set all of it to the background level before the level points, so none of it is taken for design. Does nothing on a clean surround |
| `--no-dpi` | write no resolution into the PNG, even with `--blank` |

**Survey** - what the tuning wizard measures; changes nothing:

```
DepthView --survey <file> --json [--passes <n>]
```

**Terraces** (added 1.9.0) - where a map will terrace when it is cut at a pass count, measured
on the map as it stands; changes nothing unless `--out` asks for an overlay picture:

```
DepthView --terraces <file> --json [--passes <n>] [--blank <mm>] [--depth-mm <mm>] [--spot <um>] [--out <overlay.png>]
```

The blank and target depth default to the ones last saved in the window, and the spot to
7 um; pass all three for a repeatable answer.

**Detail and noise** (added 1.10.0) - detail finer than the spot, and pixel noise on
surfaces that should be smooth; same options and defaults as `--terraces`, and changes nothing
unless `--out` asks for an overlay picture:

```
DepthView --detail <file> --json [--passes <n>] [--blank <mm>] [--depth-mm <mm>] [--spot <um>] [--out <overlay.png>]
DepthView --noise  <file> --json [--passes <n>] [--blank <mm>] [--depth-mm <mm>] [--out <overlay.png>]
```

**Jagged edges** (added 1.10.0; `--jaggies` is the same) - diagonal and curved step edges
that jump a whole step in one pixel, the mark of a map rendered at its final size:

```
DepthView --aliasing <file> --json [--blank <mm>] [--out <overlay.png>]
```

`--help` lists everything. **`--out` may not name the input file**: DepthView refuses, exits 2,
and leaves the input untouched. It never writes over an original.

---

## Conventions

- **Black is deepest, white is untouched.** Level 0 gets every pass, the maximum gets none.
- **Paths in the output are absolute and normalised**, so they may be spelled differently from
  the argument - on Windows, for example, an 8.3 short name such as `RUNNER~1` comes back
  expanded. To match an entry to a file you passed, compare resolved paths, not strings.
- **A value that does not apply is `null`**, never a stand-in default.
- **Output is plain ASCII.** Anything else inside strings is `\u` escaped, so no console code
  page can damage a file name on its way through a pipe.
- **Every document names its schema.** Within a version, fields may be added but none are
  renamed, retyped or removed. Anything else gets a new version. Ignore fields you do not know.

---

## `depthview.report/1`

```json
{
  "schema": "depthview.report/1",
  "depthview": "1.5.0",
  "files": [ { ...one entry per input... } ]
}
```

An entry that could not be read is `{ "path", "name", "ok": false, "error" }`. A successful
entry:

| Field | Meaning |
|---|---|
| `path`, `name`, `ok` | the file, and `true` |
| `verdict.severity` | `good`, `info`, `warn` or `alert`. `alert` means the file is not what it claims - an imposter, or (added 1.8.0) a picture that is mostly colour, titled `NOT A DEPTH MAP: mostly colour`, with `imposter` `none`. (Added 1.9.0) `warn` titled `LOOKS LIT, NOT DEPTH: a shaded picture?` when a grey image is shaded from one side like a render; see `content.litScore` |
| `verdict.imposter` | `none`, `replicated257` (8-bit bytes doubled into 16), `highByteOnly` (8-bit shifted into the high byte), `quantisedLadder` (evenly spaced levels, e.g. 10-bit), `sparseLevels` |
| `verdict.title`, `verdict.detail` | plain-English explanation, ready to show a user |
| `container.*` | what the file declares: `format`, `colorModel`, `declaredBitDepth`, `declaredChannels`, `hasAlpha`, `isPalette`, `bitExactDecode`, `dpiX`, `dpiY`, `fileBytes`, and (added 1.10.0) `displayCurve`: `null`, or the display curve the file declares - `kind` (`srgb`, `gamma` or `icc`; an sRGB chunk overrides gAMA, and gAMA 1.0 is linear, so `null`), `fileGamma` (the gAMA value, for `gamma`), `canUndo` (`false` for `icc`), `depthAtStoredHalf` (how far down a level stored halfway down would be if the curve were undone: about 0.79 for sRGB). The tag alone does not prove the curve was applied; DepthView always uses the values as stored |
| `content.*` | what the pixels contain: `width`, `height`, `channels`, `bitDepth`, `maxValue`, `isFloat`, `uniqueGreyLevels`, `greyPixels`, `nonGreyPixels`, `greyStoredAsColor`, (added 1.10.0) `flatPeaks` (how many flattened peaks, as in `--survey`, on the circle the short side spans) and `jaggedEdges` (`edgePixels` judged, `share` that jump in one pixel, `jagged`), each `null` for a float map or a mostly-colour picture, and (added 1.9.0) `litScore` - how one-sided the shading is, near 0 for a depth map and a few hundredths for a render lit from one side - and `litThreshold`, the score at which the verdict says it looks lit |
| `levels.*` | `min`, `max`, `rangeUse` (0-1), `occupancy` (0-1), `effectiveBits`, `step` (1 for genuine data; 257, 256, 64... for imposters), `uniformLadder`, `gaps`, `largestGap`, `mean`, `median`, `stdDev`, `p1`, `p99`, `pureBlackPixels`, `pureWhitePixels`, `headroomTop`, `headroomBottom` |
| `passCounts[]` | one row per pass count: `passes`; `depths` (distinct engraved depths actually produced); `uniform`, `relief`, `empty` (what the passes do - they always sum to `passes`); `stretched` (depths if the range were filled); `bandSpread` (`min`, `max`, `ratio` of levels per band; `null` when the map has fewer levels than passes) |
| `findings[]` | `severity`, `title`, `detail` - everything the window's report lists |
| `warnings[]` | decoder warnings, as strings |
| `histogram` | only with `--histogram`: `[[level, count], ...]` for occupied levels. `histogramBinned` is `true` for floating-point maps, whose levels are bins |

`uniform`, `relief` and `empty` are worth showing as they are. A pass in `uniform` still cuts
real material; it deepens the whole design equally rather than shaping it. It is not wasted.

A complete example, for an 8-bit map saved as 16-bit:

```json
{
  "schema": "depthview.report/1",
  "depthview": "1.5.0",
  "files": [
    {
      "path": "C:\\maps\\imposter_x257.png",
      "name": "imposter_x257.png",
      "ok": true,
      "verdict": {
        "severity": "alert",
        "imposter": "replicated257",
        "title": "IMPOSTER: 8-bit data in a 16-bit container",
        "detail": "All 256 distinct levels satisfy value = v x 257 (for example level 65,535 = 0xFFFF, the same byte twice). This is exactly what an 8-bit depth map looks like after being saved as 16-bit. There is no additional precision in this file."
      },
      "container": {
        "format": "PNG", "colorModel": "Grayscale", "declaredBitDepth": 16, "declaredChannels": 1,
        "hasAlpha": false, "isPalette": false, "bitExactDecode": true,
        "dpiX": null, "dpiY": null, "fileBytes": 3829
      },
      "content": {
        "width": 640, "height": 480, "channels": 1, "bitDepth": 16, "maxValue": 65535,
        "isFloat": false, "uniqueGreyLevels": 256, "greyPixels": 307200, "nonGreyPixels": 0,
        "greyStoredAsColor": false
      },
      "levels": {
        "min": 0, "max": 65535, "rangeUse": 1, "occupancy": 0.003906, "effectiveBits": 8,
        "step": 257, "uniformLadder": true, "gaps": 255, "largestGap": 256,
        "mean": 32639.803125, "median": 32639, "stdDev": 18952.639188, "p1": 514, "p99": 64764,
        "pureBlackPixels": 1440, "pureWhitePixels": 480, "headroomTop": 0, "headroomBottom": 0
      },
      "passCounts": [
        { "passes": 200, "depths": 200, "uniform": 0, "relief": 200, "empty": 0, "stretched": 200,
          "bandSpread": { "min": 1, "max": 2, "ratio": 2 } },
        { "passes": 256, "depths": 256, "uniform": 0, "relief": 256, "empty": 0, "stretched": 256,
          "bandSpread": { "min": 1, "max": 1, "ratio": 1 } }
      ],
      "findings": [
        { "severity": "alert", "title": "Byte-replicated levels",
          "detail": "Every 16-bit sample has its high byte equal to its low byte, so the file carries at most 8 bits of real depth information while costing twice the storage." }
      ],
      "warnings": []
    }
  ]
}
```

(Produced by `--report imposter_x257.png --json --passes 200,256`; four more findings trimmed.)

---

## `depthview.tune/1`

| Field | Meaning |
|---|---|
| `ok`, `input`, `output` | `true`, and the two files. On failure: `ok: false` and `error`, nothing else |
| `mask`, `outline` | extra files written by `--mask` / `--outline`, else `null` |
| `size` | `inWidth`, `inHeight`, `outWidth`, `outHeight` - fitting can grow the canvas |
| `applied` | the settings actually used: `blackPoint`, `whitePoint`, `stretch`, `invert`, `slices`, `dither`, `bits`, `fit`, `pad`, `rim`, and (added 1.8.0) `uniformSurround` |
| `changedPixels`, `flattenedToBlack`, `liftedToWhite` | what moved; (added 1.8.0) `surroundPixelsEvened`, surround pixels `--uniform-surround` set to the background level |
| `flat` | (added 1.8.0) `areas[]`: one per nearly level area, in `--survey`'s order - `mode` (`leave`, `smooth`, `flatten`), `low`, `high` (the band of levels it may touch), `level` (what `flatten` sets); `pixelsChanged`, `maxChange` (levels). Empty `areas` without `--flat` |
| `fit` | `canvasPx`, `artAcrossMm`, `pixelsPerMm`, and (added 1.8.0) `recentred`, `offsetX`, `offsetY` - where the input's top-left corner lands on the output, negative where background was cropped - `cropped`, and `designRim`: with `--cover-rim`, the artwork's own rim that was found and put under the new one (`innerPx`, `outerPx` from the design's centre, `widthMm`, `footLevel`, `topLevel`, `pixelsCovered`), else `null`; or `null` |
| `designOffCentreMm` | (added 1.8.0) how far the middle of the design sits from the middle of the blank, when a rim was drawn with `--blank`; else `null`. Above about 0.5 mm, `--fit design` is worth trying |
| `rim` | `widthMm`, `rampMm`, `radiusPx`, `rampPx`, `pixelsPainted`, `contentPixelsClipped`, `contentClippedFraction`, `summary`, or `null` |
| `physical` | with `--blank`: `blankDiameterMm`, `pixelsPerMm`, `dpi`, `micronsPerPixel`, `spotMicrons`, `resolutionNote`; else `null` |
| `target` | with `--depth-mm`: `depthMm`, `passes`, `targetMicronsPerPass`; else `null`. **This is the target divided by the passes, not a prediction** of what each pass will cut |
| `passes` | the pass count the figures were computed for |
| `terraces` | (added 1.9.0) with `--blank` and `--depth-mm`: `before` and `after`, each the fields of a `depthview.terrace/1` document below (from `width` to `seconds`) for the input and for the file as written; else `null` |
| `before`, `after` | full report entries, as above, for the input and for the file written. `after` is measured by reading the new file back, not predicted |

---

## `depthview.survey/1`

What the tuning wizard measures before it asks anything. Nothing is written. Levels are source
levels, before any level points.

| Field | Meaning |
|---|---|
| `ok`, `path`, `width`, `height`, `maxValue` | the file |
| `background` | `level` (taken from around the image's edge), `share` of the image, `shareInsideDesign` (of the area inside the design's circle), `looksLikeFloor` (`shareInsideDesign` of 15% or more: a cut-away floor rather than a surround), `isLow` (below the middle of the design), `low`, `high` (its 1st and 99th percentiles), `shaded` (the surround is not one level: a vignette or exporter marks, followed in from the edge; `--uniform-surround` evens it out) |
| `design` | `centreX`, `centreY`, `radiusPx`, `offCentrePx` (from the canvas centre), or `null` |
| `drawnRim` | the artwork's own raised rim: `innerPx` (its foot), `outerPx`, `footLevel`, `topLevel`, or `null` |
| `readings[]` | the floor and top under each reading of the design: `backgroundIsDesign`, `drawnRimCovered`, then `floor` and `top`, each `found`, `low`, `high`, `noise` (`high - low`: the roughness a level point removes), `pixels`, `share`, `suggested` (the level point that makes it one exact level), `source` (`background`, `flat area N`, `gap` - a few detached pockets beyond an empty stretch of the range, with `suggested` closing the gap - or `percentile` when nothing was found, when `suggested` is the 0.1st or 99.9th percentile) |
| `noiseSigma` | Immerkaer's whole-design noise estimate, in levels. Fine detail reads as noise too, so treat it as an upper figure; each flat area's `jitter` is the one that matters |
| `passes` | the pass count `boundariesCrossed` is quoted at |
| `flatPeaks[]` | (added 1.10.0) flattened peaks - small level plateaus on top of a local bump, below pure white, the "flat nose tip" a depth estimator or a clipped export leaves: `rank`, `pixels`, `level`, `centreX`, `centreY`, `bandPixels` (pixels one level below the plateau; a real rounded top has many, a clipped one almost none). Largest first, up to 50, inside the blank. Inspection only: nothing offers to fix them |
| `flatAreas[]` | nearly level areas inside the design (inside any drawn rim), largest first: `rank`, `pixels`, `shareOfDesign`, `median`, `low`, `high`, `jitter` (median pixel-to-pixel deviation), `mostlyJitter` (spread no wider than the jitter explains), `floor`, `top` (at that end of the design), `boundariesCrossed` (slice boundaries the area straddles at `passes`, after the default reading's suggested level points), `centreX`, `centreY` |
| `seconds` | time taken |

---

## `depthview.terrace/1`

Where a map will terrace when it is cut at a pass count (added 1.9.0). Each layer edge - where
the slice a pixel falls in changes - is judged by the flat treads either side of it, walked
across the contours: wider than the spot on both sides and the step survives as a step;
narrower and the beam smears it into the slope. Geometry only - not a depth prediction, and
not a promise about what the eye will see, which also depends on step height, finish and light.

| Field | Meaning |
|---|---|
| `ok`, `path`, `overlay` | the file; `overlay` is the picture written by `--out`, else `null` |
| `width`, `height`, `passes` | the map and the pass count |
| `pixelsPerMm`, `micronsPerPixel` | the blank spans the short side |
| `spotMicrons`, `targetDepthMm`, `stepMicrons` | the spot, the depth, and one pass's share of it |
| `blankRadiusPx` | radius of the circle measured: the blank, centred, spanning the short side. Everything here - levels, edges, overlay colour - is inside it; the corners are not on the coin, so a shaded background there is not counted |
| `usedLevels` | distinct levels the map holds inside the blank |
| `edges` | `pixels` (layer-edge pixels), `lengthMm` (about), `shareWiderThanSpot`, `shareWiderThan3Spots` (0-1), `medianTreadMicrons`, `p90TreadMicrons` (the narrower tread at each edge), `treadCapped` (the 90th percentile hit the four-spot measuring limit, so the true figure is larger) |
| `passesToBlend90` | passes at which nine edges in ten would have treads no wider than the spot; equal to `passes` when they already do; `null` when the map cannot supply that many depths |
| `limitedByLevels` | `true` when more passes cannot help: the map's own levels are the steps |
| `seconds` | time taken |

---

## `depthview.detail/1`

Detail finer than the spot (added 1.10.0). A grey-scale opening with a flat disc the size
of the spot removes every ridge and dot narrower than it, and a closing removes every groove;
what they take away is the detail a spot that size cannot cut as drawn. Counted where it stands
at least one layer step proud of (or below) its surroundings, inside the blank.

| Field | Meaning |
|---|---|
| `ok`, `path`, `overlay` | the file; `overlay` is the picture written by `--out`, else `null` |
| `width`, `height`, `passes` | the map and the pass count (which sets the layer step) |
| `pixelsPerMm`, `micronsPerPixel`, `spotMicrons`, `targetDepthMm` | the scale and the job |
| `blankRadiusPx` | the circle measured, as for `depthview.terrace/1` |
| `finerThanPixels` | `true` when even two spots are under a pixel: nothing in the file is narrower than the spot, and every count is 0 |
| `blankPixels` | pixels inside the blank |
| `underSpot` | features narrower than one spot: `raisedPixels`, `recessedPixels`, `share` of the blank, `areaMm2`, `tallestMicrons` (the tallest such feature, in depth) |
| `underTwoSpots` | features between one and two spots wide - a separate band, not including the above: `raisedPixels`, `recessedPixels`, `share` |
| `seconds` | time taken |

---

## `depthview.noise/1`

Pixel noise on surfaces that should be smooth (added 1.10.0). In tiles about a quarter of
a millimetre across, the median size of the pixel-to-pixel second difference, which a median
keeps from reading real edges and texture as noise. Compared with one layer step: noise under
half a step cuts away; above it the surface comes out rough.

| Field | Meaning |
|---|---|
| `ok`, `path`, `overlay` | as above |
| `width`, `height`, `passes`, `pixelsPerMm`, `targetDepthMm`, `blankRadiusPx` | as above |
| `stepLevels` | one layer step, in levels: the full range over `passes - 1` |
| `windowPx` | the tile size used |
| `blankPixels` | pixels inside the blank |
| `noisyPixels`, `veryNoisyPixels` | pixels whose tile has noise of at least half a step, and of at least two steps |
| `shareNoisy`, `shareVeryNoisy` | the same as shares of the blank |
| `medianNoiseLevels`, `medianNoiseMicrons` | median noise where it was found, in levels and in microns of depth |
| `seconds` | time taken |

---

## `depthview.aliasing/1`

Jagged edges (added 1.10.0). A map built at two or three times its final size and reduced
carries in-between levels along its edges; one rendered at its final size jumps a whole step in
one pixel, and cuts every diagonal and curve as a staircase. Only diagonal and curved step edges
are judged (a wall along the pixel grid jumps in one pixel whatever made it), inside 96% of the
blank radius so a rim never decides the answer.

| Field | Meaning |
|---|---|
| `ok`, `path`, `overlay` | as above; the overlay marks the stairs red |
| `width`, `height`, `blankRadiusPx`, `blankPixels` | as above |
| `minStepLevels` | a change at least this many levels across four pixels counts as an edge |
| `edgePixels`, `aliasedPixels`, `share` | step-edge pixels judged, those that jump in one pixel, and the share |
| `judged` | `true` when there were enough edges (200 px) to say anything |
| `jagged` | `true` when judged and `share` is at least `jaggedShare` (0.6; art reduced from 2-4x measures 0.14-0.35) |
| `seconds` | time taken |

---

## `depthview.gcode/1`

```
DepthView --gcode <job.gc> --json [--out result.json]
```

What a G-code file actually sends the machine - the check that closes the loop after an
export, because it shows what survived the trip. Reads MakeIt's staged job and LightBurn
G-code, gzipped or not, streaming: a 349 MB MakeIt relief job reads in about three seconds.
Exit code 0 when read, 2 when it could not be.

| Field | Meaning |
|---|---|
| `generator`, `headerComments` | e.g. `"wecreat 3.0.6"` from MakeIt's first comment line |
| `moves` | `g0`, `g1`, `burning` (G1 with S &gt; 0), `z`, `burnLengthMm`, `travelLengthMm` |
| `burnArea` | `minX`, `maxX`, `minY`, `maxY` of everything that burned |
| `powerLevelCount`, `powerLevels` | every distinct S the burning uses, as `[S, moves]` - the depth resolution the machine really receives |
| `alongLine` | `stepModeMm`: the commonest step between power changes along a line (the sample pitch of a raster), `stepModeShare`, `stepCount`, `stepBinUm` |
| `directions[]` | per scan direction: `angleDeg` (0-179), `passes`, `linesPerPass`, `linePitchMm`, `lineDensityPerCm`, `burnMoves`, `burnLengthMm`. Spacing is measured within one pass |
| `layers`, `zLevels[]` | layers cut, and each cutting height with its `layers`, `burnMoves`, `burnLengthMm`, `mainAngleDeg` |
| `undefinedOperandLines` | lines with a non-numeric operand such as MakeIt's `Zundefined`, ignored rather than read as zero |
| `settingsGroups[]` | one per combination of frequency, pulse width and speed: `minS`, `maxS`, `powerLevelCount`, `feedMmPerMin`, `frequencyKHz`, `pulseWidthNs`, `angleDeg`, `rasterAnglesDeg`, `linePitchMm`, `linesPerPass`, `burnMoves`, `burnLengthMm`, `firstLine`, and `makeIt` - the same in MakeIt's units: `powerPercentMin`/`Max`, `speedMmPerS`, `frequencyKHz`, `pulseWidthNs`, `lineDensityPerCm` |
| `layerChecks` | (added in 1.7.0) repeated layers and cleaning. Layer numbers count Z blocks that burn, in cut order. `mainGroup`; `repeatedLayers` - layers that are exact copies of the layer before them at the same height, with `repeatAfterEvery` (how many layers typically fall between them, or null); `otherSettingsLayers` and `otherSettingsAfterEvery` - layers that start in a group other than the main one; `emptyLayers`, `emptyLayersAtEnd` - Z moves at or below the first cutting height that burn nothing; `truncated`; and `blocks[]`, every Z block with `layer` (null if it burns nothing), `z`, `firstLine`, `burnMoves`, `burnLengthMm`, `group`, `angleDeg`, `repeatsLayer` |
| `settingsSwitches`, `runs[]` | how often, and in what order, the job changes group |
| `mCodes` | every M code seen, as `[code, count]` |
| `decoding` | how far the decoding below is confirmed, as text to show a user |

**Cleaning layers have two possible signatures, and MakeIt 3.0.6 was seen to use the less
obvious one.** A cleaning layer with settings of its own would show as `otherSettingsLayers`
recurring at an interval. With Cleaning Layer on (every 10 layers, 33%, 9494 mm/s, line density
175), MakeIt 3.0.6 instead wrote each cleaning layer as an exact copy of the engraving layer
before it, at the engraving settings - so it shows as `repeatedLayers` with `repeatAfterEvery`
10, and the cleaning settings appear nowhere in the file. Seen in one job; report what the
fields say rather than assuming either form.

**How sure the decoding is.** Power as S/10 percent, frequency from `M38F`, pulse width from
`M39P`, speed from `G1 F` and line density as lines per centimetre were confirmed against
MakeIt 3.0.6 twice: a Color Test (Fine Color Marking) checked against the physical part, and a
10-layer Relief (Emboss) job read back against its settings panel. Other job types and MakeIt
versions are assumed to encode the same way. Line spacing and the step along a line are
measured from the moves themselves.

`layers` counts every Z move followed by burning, and can exceed the number of `zLevels`:
MakeIt has been seen cutting two layers at the same height.

---

## Suggested integration for a laser program

1. **Export the depth map at full source precision.** This is the requirement everything else
   rests on. If the exported file has already been reduced to the precision the engraving path
   uses, DepthView is analysing something whose detail has already gone - the exact problem it
   exists to catch. Test it directly: run `--report --json` on the user's original image and on
   the host's export of it, and compare `content.bitDepth`, `content.uniqueGreyLevels` and
   `levels.step`. They should match.
2. **Run `--report --json`** with `--passes` set to the job's real pass count, and show
   `verdict` and the matching `passCounts` row.
3. **Optionally run `--tune --json`** into a new file and offer it back to the user, with
   `before` and `after` side by side.
4. **Import the tuned PNG** the way the host imports any image. Before relying on any round
   trip, check that exporting and re-importing with no changes leaves the project identical.

---

## Designed, not built: exchanging settings

Once DepthView can predict depth, a map and the settings it is cut with stop being independent:
changing one changes which values of the other are right. The intended shape is a settings
document the host writes and DepthView reads, and one DepthView writes back for the host to
validate as it would a user's own edit. The host stays the authority on what is a legal
setting.

Fields such a document would carry: power, speed, pass or layer count, line density or
interval, pulse frequency, pulse width, focus offset, per-layer Z descent and material; and for
the image, its physical size on the workpiece, its bit depth, and which operation it belongs
to. It will get its own schema name when it exists. Until then, nothing in the outputs above
depends on it.

---

Questions and problems: [GitHub issues](https://github.com/LeeGillie/DepthView/issues).
