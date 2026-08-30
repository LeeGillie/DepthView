# Third-party notices

DepthView itself is MIT licensed — see [LICENSE](LICENSE).

A published DepthView binary is self-contained, so it carries its dependencies inside the
executable. Those components remain under their own licences, listed here. Licence
identifiers below were read from each package's own NuGet metadata rather than assumed.

---

## SixLabors.ImageSharp 3.1.12 — Six Labors Split License 1.0

<https://github.com/SixLabors/ImageSharp> · Copyright (c) Six Labors

The only dependency here whose terms are conditional, so it is worth stating plainly.

The Six Labors Split License grants **Apache License 2.0** terms when any of the following
applies. DepthView satisfies the first outright, and its author independently satisfies the
third:

1. *"You are consuming the Work for use in software licensed under an Open Source or Source
   Available license."* — DepthView is MIT licensed.
2. *"You are consuming the Work as a Transitive Package Dependency."*
3. *"...in the capacity of a For-profit company/individual with less than 1M USD annual gross
   revenue."*
4. *"...in the capacity of a Non-profit organization or Registered Charity."*

A Six Labors Commercial License is required only outside those cases — in practice, a
for-profit entity at or above 1M USD annual gross revenue consuming it as a direct
dependency in software that is **not** open source. Anyone redistributing a modified,
closed-source DepthView should check their own position against the licence text.

ImageSharp is used only for TIFF, JPEG, BMP, WebP, GIF, TGA and QOI decoding, and for PNG
encoding on the `--render` path. The decoders that matter for depth-map work — PNG,
PGM/PPM/PBM and PFM, all bit-exact — are DepthView's own and have no third-party dependency.

Full text: <https://raw.githubusercontent.com/SixLabors/ImageSharp/main/LICENSE>

---

## Avalonia 11.3.20 — MIT

<https://github.com/AvaloniaUI/Avalonia> · Copyright (c) The Avalonia Project

Covers the UI framework and every `Avalonia.*` package DepthView pulls in: `Avalonia`,
`Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Themes.Simple`, `Avalonia.Skia`,
`Avalonia.Win32`, `Avalonia.X11`, `Avalonia.Native`, `Avalonia.FreeDesktop`,
`Avalonia.Controls.ColorPicker`, `Avalonia.Remote.Protocol`, `Avalonia.BuildServices` and
`Avalonia.Fonts.Inter`.

`Avalonia.Diagnostics` is referenced only in Debug builds and is not redistributed.

### Inter typeface

`Avalonia.Fonts.Inter` is an MIT-licensed package that embeds the **Inter** typeface, which
its upstream project distributes under the **SIL Open Font License 1.1**. The font ships
inside the DepthView binary as part of that package.

<https://github.com/rsms/inter> · Copyright (c) The Inter Project Authors

---

## SkiaSharp 2.88.9 — MIT

<https://github.com/mono/SkiaSharp> · Copyright (c) Microsoft Corporation

Includes `SkiaSharp.NativeAssets.Win32`, `.Linux`, `.macOS` and `.WebAssembly`. The managed
binding is MIT; the underlying **Skia** graphics library it wraps is a Google project under
the BSD 3-Clause licence.

<https://skia.org>

---

## HarfBuzzSharp 8.3.1.1 — MIT

<https://github.com/mono/SkiaSharp> · Copyright (c) Microsoft Corporation

Includes `HarfBuzzSharp.NativeAssets.Win32`, `.Linux`, `.macOS` and `.WebAssembly`. The
managed binding is MIT; the underlying **HarfBuzz** text shaping engine is distributed by its
authors under the "Old MIT" licence.

<https://github.com/harfbuzz/harfbuzz>

---

## ANGLE (Avalonia.Angle.Windows.Natives) — BSD 3-Clause

<https://chromium.googlesource.com/angle/angle> · Copyright 2018 The ANGLE Project Authors

Native OpenGL ES translation layer used by Avalonia on Windows. Redistribution requires the
copyright notice, this condition list and the disclaimer to be retained, and forbids using
the project's contributors' names to endorse derived products without permission.

---

## MicroCom.Runtime 0.11.0 — MIT

<https://github.com/kekekeks/MicroCom> · COM interop runtime used by Avalonia on Windows.

---

## Tmds.DBus.Protocol 0.21.3 — MIT

<https://github.com/tmds/Tmds.DBus> · Copyright (c) Tom Deseyn

D-Bus protocol support used by Avalonia on Linux.

---

## System.IO.Pipelines 8.0.0 — MIT

<https://github.com/dotnet/runtime> · Copyright (c) .NET Foundation and Contributors

---

## .NET runtime — MIT

<https://github.com/dotnet/runtime> · Copyright (c) .NET Foundation and Contributors

A self-contained publish embeds the .NET runtime and base class libraries in the executable.

---

## Build-time only, not redistributed

These are used to produce the artwork in `artwork/` and the test fixtures in `tests/`. They
are not dependencies of the application and are not present in a published binary.

- **NumPy** — BSD 3-Clause · <https://numpy.org>
- **Pillow** — MIT-CMU · <https://python-pillow.org>
- **Playwright for Python** and **Chromium** — Apache 2.0 and BSD 3-Clause respectively
- **Inter** (via `@fontsource/inter`) — SIL Open Font License 1.1, used to typeset the banner
