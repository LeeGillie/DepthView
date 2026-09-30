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
| 1 | analysed, and at least one file's verdict is `alert` (an imposter) | - |
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
| `--fit [content\|canvas]` | grow the canvas so the design clears the rim; nothing is resampled |
| `--depth-mm <mm>` `--passes <n>` | the job's target depth and pass count, for the figures in the output |

`--help` lists everything. **`--out` may not name the input file**: DepthView refuses, exits 2,
and leaves the input untouched. It never writes over an original.

---

## Conventions

- **Black is deepest, white is untouched.** Level 0 gets every pass, the maximum gets none.
- **Paths in the output are absolute.**
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
| `verdict.severity` | `good`, `info`, `warn` or `alert`. `alert` means the file is not what it claims |
| `verdict.imposter` | `none`, `replicated257` (8-bit bytes doubled into 16), `highByteOnly` (8-bit shifted into the high byte), `quantisedLadder` (evenly spaced levels, e.g. 10-bit), `sparseLevels` |
| `verdict.title`, `verdict.detail` | plain-English explanation, ready to show a user |
| `container.*` | what the file declares: `format`, `colorModel`, `declaredBitDepth`, `declaredChannels`, `hasAlpha`, `isPalette`, `bitExactDecode`, `dpiX`, `dpiY`, `fileBytes` |
| `content.*` | what the pixels contain: `width`, `height`, `channels`, `bitDepth`, `maxValue`, `isFloat`, `uniqueGreyLevels`, `greyPixels`, `nonGreyPixels`, `greyStoredAsColor` |
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
| `applied` | the settings actually used: `blackPoint`, `whitePoint`, `stretch`, `invert`, `slices`, `dither`, `bits`, `fit`, `pad`, `rim` |
| `changedPixels`, `flattenedToBlack`, `liftedToWhite` | what moved |
| `fit` | `canvasPx`, `artAcrossMm`, `pixelsPerMm`, or `null` |
| `rim` | `widthMm`, `rampMm`, `radiusPx`, `rampPx`, `pixelsPainted`, `contentPixelsClipped`, `contentClippedFraction`, `summary`, or `null` |
| `physical` | with `--blank`: `blankDiameterMm`, `pixelsPerMm`, `dpi`, `micronsPerPixel`, `spotMicrons`, `resolutionNote`; else `null` |
| `target` | with `--depth-mm`: `depthMm`, `passes`, `targetMicronsPerPass`; else `null`. **This is the target divided by the passes, not a prediction** of what each pass will cut |
| `passes` | the pass count the figures were computed for |
| `before`, `after` | full report entries, as above, for the input and for the file written. `after` is measured by reading the new file back, not predicted |

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
