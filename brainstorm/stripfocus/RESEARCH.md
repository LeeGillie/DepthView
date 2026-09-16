# What people do today

Research gathered September 2026. Products and software change, so each claim is tied to its
source and date. Check the source before relying on a claim.

## 1. Focus at mid-height, rely on depth of field

The most common approach for flasks, and where most flask guides stop.

- **Rule of thumb** (ComMarker): focus on the middle of the slope or curve so the design stays
  sharp above and below it. For a convex surface, lower focus by about half the height
  difference; for a concave one, raise it.
  [ComMarker, curved surfaces](https://blog.commarker.com/archives/50313)
- **Flat-bed jigs:** a 3D-printed jig sold for the original WeCreat Lumos holds 6–12 oz
  flasks (about 95 mm wide) so either face can be marked on the flat bed. It ships with SVG
  alignment files, and the seller warns it's PLA and melts if the beam touches it.
  [Etsy listing](https://www.etsy.com/listing/4379821252)
- **Design advice:** keep the art narrow across the curve (3–4 in of usable width on a
  typical flask) and use bold fonts, not fine script.
  [OMTech, hip flasks](https://omtech.com/blogs/tips/laser-engraving-hip-flask)
- **Limit for color:** stain and color marking by defocus "doesn't work very well" around a
  radius. As the part curves, the beam either refocuses (burning the surface) or drifts
  further out (losing heat). Jimani solves this with an inverted beam expander, which is
  optics, not software.
  [Jimani, color marking](https://www.jimani-inc.com/blog/color-marking-with-fiber-lasers)

## 2. Rotary

- **Flask guides mostly recommend a rotary** to keep the laser in focus.
  [OMTech](https://omtech.com/blogs/tips/laser-engraving-hip-flask),
  [LaserPecker](https://us.laserpecker.net/blogs/how-to/engrave-flasks)
- **Diode gantry makers** point to rotaries rather than mapping for curved items.
  [Creality Falcon](https://www.crealityfalcon.com/blogs/laser-academy/laser-engraving-curved-surface)
- **Lumos Ultra rotary:** takes parts up to 100 mm in diameter, and up to 380 mm long with
  the slide. MakeIt! provides a 3D preview for rotary work.
  [WeCreat product page](https://wecreat.com/products/lumos-ultra-6w-uv-60w-100w-mopa-laser-engraver),
  [MakeIt! software](https://wecreat.com/pages/software)
- **Limit:** a rotary only helps when the part's cross-section is close to a circle centered
  on the rotation axis. A buckle's gentle curve has a large radius, so the part would have to
  sit far off-axis.

## 3. Measure the surface, refocus automatically

This exists, but each version is tied to its maker's hardware.

- **xTool F1 Ultra**, in xTool Creative Space: a "curved surface" mode. The laser measures a
  grid of points, builds a mesh, and adjusts focus while it works. One side only; a full wrap
  still needs a rotary.
  [STEMtropolis walkthrough](https://stemtropolis.com/engraving-curved-surfaces-with-the-f1-ultra/)
  - The F1 Ultra can do curved surfaces or relief, but not relief on a curved surface. A
    community member proposed a workaround: scan the part externally, subtract the surface
    from the design's depth map, and engrave the result flat.
    [xTool community](https://support.xtool.com/community-support/detail/6316)
- **Bambu Lab H2D**, in Bambu Suite: a built-in laser height probe scans a region into a
  point cloud and 3D contour.
  [Bambu wiki, surface engraving](https://wiki.bambulab.com/en/software/bambu-suite/manual/surface-engraving)
  - Its published limits are useful as a reference for any design: at most 15 mm of height
    change, slopes up to 40°, and no sharp corners or steps.
  - Transparent, mirror-like and hollow parts give bad readings, which matters for polished
    brass and stainless.
- **EZCAD3** (industrial, JCZ controllers): imports STL or DXF and focuses automatically
  across 3D surfaces. It needs a dynamic-focus (3-axis) galvo.
  [Linxuan](https://www.linxuanlaser.com/ezcad3-3d-application-3d-surface-processing/)
- **LightBurn:**
  - **Cylinder Correction** (galvo only) warps the output so it isn't distorted on a
    cylinder. It does **not** change focus, and it relies on a lens with enough depth of
    field. [LightBurn docs](https://docs.lightburnsoftware.com/galvo/CylinderCorrection.html)
  - **Surface mapping** is an open feature request.
    [LightBurn Fider](https://lightburn.fider.io/posts/3096/surface-mapping-for-curved-surface-engraving)
  - **Per-layer Z Offset and Z Step per pass** are documented as gantry-only.
    [LightBurn docs](https://docs.lightburnsoftware.com/UI/CutSettings/CutSettings-Line.html)
- **CNC auto-levelling** (for example OpenCNCPilot): probes a height map with a touch probe
  and rewrites G-code to follow it, but only for GRBL gantry machines.
  [OpenCNCPilot](https://github.com/martin2250/OpenCNCPilot)

## Where the Lumos Ultra stands

- **What it has:** autofocus with fine distance measurement and a motorized Z, with 130 mm
  maximum working height for surface work. It also has a rotary, and MakeIt! has a
  layer-by-layer Z mode for engraving inside crystal.
  [WeCreat](https://wecreat.com/products/lumos-ultra-6w-uv-60w-100w-mopa-laser-engraver),
  [review](https://hobbylasercutters.com/wecreat-lumos-ultra-review/)
- **What's missing:** no published curved-surface or height-map mode for surface marking was
  found in MakeIt!, and LightBurn doesn't offer one either. The ingredients are there; the
  software isn't.

## Belt buckles specifically

Published buckle guides treat the blank as flat
([ComMarker, belt buckles](https://blog.commarker.com/archives/57138)). No buckle-specific
curved-surface method was found. That gap is what StripFocus is aimed at.
