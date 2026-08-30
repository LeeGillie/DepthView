# DepthView

![DepthView](artwork/banner/depthview-banner-1280x640.png)

A depth-map candidate inspector. Feed it an image and it tells you what the file
*claims* to be, what its pixels *actually* contain, and whether those two things
agree.

Built for one specific frustration: a 16-bit PNG that only carries 8 bits of real
depth information. DepthView calls those **imposters** and names the exact
mechanism behind each one.

---

## What it reports

**Container** (from the file header)
format, colour model, declared bit depth, channels, palette size, alpha,
compression, filtering, interlacing, gamma, sBIT, ICC profile, resolution.

**Content** (from every pixel)

| Measurement | Why it matters |
|---|---|
| Unique grey levels | Distinct values where R = G = B. The real information content of the map. |
| Greyscale vs non-greyscale pixel counts | A depth map should be 100% neutral. Anything else is contamination. |
| Unique non-grey colours | A handful means compression artefacts. Thousands means it is a colour-encoded map, not a grey one. |
| Per-channel unique counts (R/G/B) | These match exactly in genuine greyscale-stored-as-RGB. |
| Full grey-level histogram | Hover for the exact level and pixel count under the cursor. |

**Endpoints and clipping**

| Measurement | Why it matters |
|---|---|
| Pure white pixel count | Pixels on the container maximum. In a laser workflow these get zero passes and are left as bare surface. A large flat count can also mean clipped highlights. |
| Pure black pixel count | Pixels on zero — full depth. A large flat count usually means clipped shadows, with relief detail past that depth already thrown away. |
| Lightest / darkest level present | When an endpoint has no pixels at all, DepthView reports how close the map actually got and with how many pixels, rather than just saying "none". |
| Unused headroom | Levels wasted at each end. Every unused level at the top is a laser pass that does nothing; every one at the bottom is depth you paid for and did not use. |

**Level structure** — where imposters give themselves away

| Measurement | Reading it |
|---|---|
| Level step (GCD) | 1 = native data. **257** = byte replication. **256** = left shift. Anything else > 1 = quantised then stretched. |
| Uniform ladder | Every used level sits exactly on the same step. Real depth data essentially never does this. |
| Effective bits | log2 of the unique level count. The honest bit depth. |
| Level occupancy | Unique levels / container size. Exactly 0.3906% means 256 of 65,536. |
| Range utilisation | How much of the container's range is used at all. |
| Histogram gaps | A regular comb of gaps is the visual form of the level step. |

### Imposter classes detected

| Verdict | Signature |
|---|---|
| `Replicated257` | Every level is `v x 257` — an 8-bit map saved as 16-bit by byte replication. |
| `HighByteOnly` | Every level has a zero low byte (`v x 256`) — an 8-bit map left-shifted into 16 bits. |
| `QuantisedLadder` | Levels form a perfectly even ladder with step > 1 — catches 10-bit, 12-bit and any other depth hiding in a larger container, not just the 8-in-16 case. |
| `SparseLevels` | Far fewer levels than the container holds, with no clean pattern. Usually rescaled or filtered 8-bit source. |

---

## Running it

Nothing to install. The published binary is a single self-contained executable
with the .NET runtime inside it.

* **Windows** — double-click `DepthView.exe`
* **macOS** — `chmod +x DepthView && ./DepthView`
* **Linux** — `chmod +x DepthView && ./DepthView`

### Loading an image

Three ways, all equivalent:

* **Drag and drop** onto the panel at the left
* **Click** the panel to open a file browser
* **Ctrl+V** to paste

> **Clipboard warning.** If you copy the *file* in your file manager, DepthView
> reads the original bytes and the bit-depth reading is trustworthy. If you copy
> the *picture* out of a viewer, the OS clipboard has almost certainly already
> flattened it to 8 bits per channel. DepthView detects which path was used and
> warns you rather than reporting a wrong answer.

### Preview modes

| Mode | Use |
|---|---|
| Raw values | Samples scaled straight to screen. |
| Auto-stretch | min..max mapped to full black..white, so shallow depth ranges become visible. |
| **Low byte only** | Shows just the bottom 8 bits of each 16-bit sample. A genuine 16-bit map shows structure. An imposter shows flat black. |
| Colour mask | Greyscale pixels dimmed, non-grey pixels flagged red. |

### 3D relief preview

A raw grey ramp tells you almost nothing about how a relief will actually look. The
**3D preview** button opens a lit render of the height field:

* **Material presets** — polished and brushed brass, stainless, copper, black anodised
  aluminium, slate, cherrywood, maple, oak, and a neutral plaster for judging pure shape.
  Each carries a separate finish for the untouched field and for the engraved floor, because
  that difference is what makes it read as laser work: brass frosts and darkens as it
  deepens, while black anodising and slate come out *brighter* than the surface around them.
* **Imported textures** — point a material at your own images and they are sampled in
  workpiece space, so they stay locked to the material as you zoom and pan. Two separate
  maps, because they do different jobs:
  * **Colour image** — a photograph of the actual board or plate, supplying the field colour.
  * **Surface relief image** — read as a fine height field that tilts the surface underneath
    the relief. This, *not* colour, is what makes a brushed finish look brushed or a hammered
    one look hammered: the effect is a stretched, broken-up highlight that moves with the
    light. Loading a photo of brushed brass as a colour map alone would render flat.

  Both fade with depth by the material's *survival* setting, because the laser destroys the
  surface texture in the engraved floor — wood grain chars away, a brushed finish is replaced
  by a frosted one. Edits are saved to `materials.json` next to the executable and reloaded
  automatically, so a texture you point at once stays pointed at.
* **Generated textures** — the wood, slate and brushed-metal presets carry grain, mottling and
  scratches out of the box, so a wood preset actually looks like wood without hunting for a
  photograph first. Wood and stone take their colour from the material itself, which is why
  maple comes out pale, oak mid and cherry red-brown from one generator. Generated textures
  only fill slots you haven't loaded an image into, so pointing a material at a real
  photograph always wins.
* **Movable light** — drag on the preview, or use the azimuth and elevation sliders. Sweeping
  the light is how you check that a shape reads from every direction.
* **Ambient occlusion** — computed from the height field itself. This is what makes relief
  read as depth rather than as a grey gradient.
* **Vertical exaggeration** — 1.1 mm on a 25 mm coin is a 4% aspect ratio, so an honest render
  looks nearly flat. Exaggerate to judge form, drop back to check reality.
* **Quantise to steps** — the useful one. Quantises the height field to a fixed number of
  depth steps, which is what a layered engraving actually produces. Slide the step count and
  watch for the point where visible contour lines appear. That is your terracing threshold,
  found without burning a blank.

**Camera.** Left-drag orbits, right-drag pans, the wheel zooms, middle-click resets. Hold
Shift and left-drag to move the light instead of the camera. *Top view* snaps back to a
straight-down, pixel-exact render — that path is slightly crisper and faster, so it is the
better one for judging fine detail, while the orbit view is the one for judging form.

The light stays fixed to the workpiece rather than to the camera, so orbiting shows how the
relief reads from different sides under the same lighting. A low camera elevation is how you
find out whether a shallow engraving actually stands up: plenty of reliefs look fine from
directly above and vanish at a grazing view.

It is **software rendered on purpose** — no OpenGL. GL is the first thing to fail over a
remote desktop session, which is often exactly where the machine driving the laser lives.

This is a preview, not a process simulation. It shows form, banding, and how light will catch
the relief. It does not predict slag, heat accumulation, edge rounding from beam width, or how
pulse settings change the surface finish.

### Histogram

Hover anywhere for the exact level and pixel count. Mouse wheel zooms, right-click
resets. The green strip under the plot lights up every level that occurs at least
once — **evenly spaced teeth there are the fastest visual tell for an imposter.**

Every UI element has a tooltip explaining what it means.

---

## Command line

DepthView also works headlessly, so you can screen a folder of candidates without
opening a window.

```
DepthView                            open the window
DepthView <image>                    open the window with that image loaded
DepthView --report <image>           full text report (also written beside the image)
DepthView --report <folder>          every image in the folder
DepthView --report <folder> --summary --out results.txt
```

There's a headless render mode too, so previews can be scripted across a folder of
candidates or a sweep of materials, light angles and slice counts:

```
DepthView --render depth.png --material Oak --albedo oak.jpg --micro oak-bump.png
DepthView --render depth.png --material "Brushed brass" --brushed --micstr 1.3
DepthView --render depth.png --slices 40 --light 300 25 --exag 2 --out terrace-check.png
```

Exit codes: `0` all clean, `1` at least one file flagged as an imposter, `2` a
file failed to load — so it drops straight into a build script or a batch check.

> **Windows scripting note.** DepthView is built as a windowed executable so that
> launching it normally never flashes a console. The side effect is that Windows
> shells do not wait for it. In report mode it attaches to the calling console and
> prints there, but to block and read the exit code use
> `start /wait DepthView.exe --report ...` in cmd, or
> `Start-Process -Wait -NoNewWindow DepthView.exe -ArgumentList ...` in PowerShell.
> `--out` always writes the file regardless. On macOS and Linux there is no
> subsystem distinction and it behaves like any other CLI tool.

Summary output:

```
OK      640x480   16bit  56,299 levels  step 1          0 non-grey  true16.png
FAIL    640x480   16bit   1,024 levels  step 64         0 non-grey  imposter_ladder10bit.png
FAIL    640x480   16bit     256 levels  step 256        0 non-grey  imposter_shift256.png
FAIL    640x480   16bit     256 levels  step 257        0 non-grey  imposter_x257.png
```

---

## Formats

| Format | Decoder | Bit-exact |
|---|---|---|
| PNG (1/2/4/8/16-bit, grey / RGB / palette / alpha, Adam7 interlace) | DepthView's own | **Yes** |
| PGM / PPM / PBM (P1–P6, any MAXVAL incl. 12-bit) | DepthView's own | **Yes** |
| PFM (float32 depth) | DepthView's own | **Yes** |
| TIFF (8/16-bit) | ImageSharp + DepthView's own header sniffer for declared depth | Yes for 8 and 16 |
| JPEG, BMP, WebP, GIF, TGA, QOI | ImageSharp | 8-bit sources only |

**Why hand-written decoders?** Every general-purpose imaging stack — Windows WIC,
macOS CoreGraphics, GTK/GDK, and browser canvas — will happily hand back an 8-bit
buffer for a 16-bit PNG. That would silently destroy the exact thing this tool
exists to measure. So the formats that matter for depth work are decoded here,
byte by byte. Anything decoded by a third party is flagged in the report when its
precision cannot be guaranteed.

---

## Building

Requires the .NET 8 SDK or newer. Open `DepthView.slnx` in Visual Studio 2026, or:

```
dotnet build src/DepthView/DepthView.csproj -c Release
```

### Publishing single-file binaries

```
powershell -ExecutionPolicy Bypass -File publish.ps1        # Windows
./publish.sh                                                # macOS / Linux
```

Both produce self-contained, single-file executables in `publish/<rid>/` for:
`win-x64`, `win-x86`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`,
`osx-arm64`. All seven cross-compile from any one host — no .NET install needed on
the target machine.

### Why Avalonia

The requirement was the widest possible reach with a simple install, developed and
maintained in Visual Studio 2026. Avalonia renders its own UI rather than wrapping
each platform's native toolkit, so one C#/XAML codebase runs identically on
Windows 10 and 11, macOS on Intel and Apple Silicon, and Linux under both X11 and
Wayland. WPF and WinUI are Windows-only; MAUI has no Linux desktop target. Avalonia
can additionally target Android, iOS and WebAssembly later without a rewrite.

---

## Project layout

```
DepthView.slnx
src/DepthView/
  Program.cs              entry point, CLI report mode
  App.axaml               application shell
  Views/MainWindow        UI, input handling, preview rendering
  Views/ReliefWindow      lit 3D relief preview
  Controls/               HistogramControl - hover readout, wheel zoom, comb strip
  Imaging/                PngDecoder, PnmDecoder, PfmDecoder, TiffSniffer, ImageLoader
  Analysis/               DepthAnalyzer, AnalysisResult, ReportWriter
  Rendering/              ReliefRenderer (software height-field shading), MaterialPreset
  Assets/                 icon files consumed by the build
artwork/
  make_icon.py            generates the icon at every size, plus the .ico
  make_hero.py            generates the banner hero's depth map
  make_banner.py          composites and screenshots the banner
  depthview-icon-*.png    16 to 1024 px
  depthview.ico           multi-resolution, 16 to 256
  banner/                 hero renders and the finished banners
tests/
  make_fixtures.py        generates test images with known-correct answers
  make_textures.py        generates sample material textures and a demo relief
  fixtures/               the generated images
  textures/               the generated material textures
```

## Artwork

The icon is a terraced dome on a dark plate with a black-to-white grey ramp along the
bottom. The dome is smooth on the left and quantised into terraces on the right — the same
depth map before and after it becomes a finite number of layers, which is what the program
is for.

It is generated by `artwork/make_icon.py`, shaded with the same lighting model the relief
preview uses: height-field normals, Blinn-Phong, and ambient occlusion sampled from the
height field. So the icon and the app agree, and re-running the script regenerates every
size from one source. `<ApplicationIcon>` picks up the `.ico` for the Windows executable and
both windows use the 256 px PNG.

The banner carries the same motif at size, and its hero is **rendered by DepthView itself** —
`make_hero.py` writes a 16-bit depth map of that smooth-then-terraced dome, `--render` turns
it into a lit brass plaque, and `make_banner.py` keys out the flat background and composites
it under an HTML layout screenshotted with headless Chromium. So the art advertising the
program is produced by the program, and the icon and banner cannot drift apart.

Two sizes are built: `1920x1080` and a `1280x640` for READMEs and social previews. Regenerate
with `python make_hero.py`, the `--render` line quoted at the top of `make_banner.py`, then
`python make_banner.py`. It needs `numpy`, `pillow`, `playwright` (plus `playwright install
chromium`) and the Inter font unpacked into `artwork/fonts/` via `npm pack @fontsource/inter@5`.

## Tests

`tests/make_fixtures.py` writes eleven images whose correct answer is known by
construction — a genuine 16-bit map, the three imposter classes, an interlaced
copy, a 16-bit PGM, a 12-bit PGM, a float PFM, and a colour-contaminated map. It
uses a hand-rolled PNG writer so the fixtures cannot inherit a bug from the same
kind of library DepthView is built to distrust.

```
python tests/make_fixtures.py
DepthView --report tests/fixtures --summary
```

Unique-level counts, non-grey pixel counts and non-grey colour counts have been
verified to match NumPy computed on the source arrays, exactly, for every fixture.
The interlaced and PGM copies of the same data produce identical numbers to the
plain PNG, which cross-checks the Adam7 and Netpbm paths against the baseline.

## Roadmap

`TODO.md` carries everything discussed and consciously deferred, with enough context to pick
it up cold — the LightBurn slicing and calibration services, the orbiting 3D view, colour-map
decoding, and the remaining format work.

## Licence

DepthView is **MIT** licensed — see [LICENSE](LICENSE).

Dependency licences are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Almost
everything is MIT. The one worth knowing about is
[SixLabors.ImageSharp](https://github.com/SixLabors/ImageSharp), which uses the Six Labors
Split License: that grants plain Apache 2.0 terms to software licensed under an open source
or source-available licence, which DepthView is. It is used only for TIFF, JPEG, BMP, WebP,
GIF, TGA and QOI, plus PNG encoding on the `--render` path — the bit-exact decoders that
matter for depth work are DepthView's own and carry no third-party dependency.
