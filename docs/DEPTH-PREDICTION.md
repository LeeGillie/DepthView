# Predicting engraving depth and appearance

Research notes and design for the largest unbuilt thing in this project: predicting what a
set of laser parameters will actually do to a piece of metal, and showing it before anyone
presses start.

Status: **design and evidence only. None of this is implemented.** Everything below is
either established physics with a citation, arithmetic anyone can check, or a clearly
labelled unknown. Where a number is missing it is left missing.

---

## 1. It is three problems, not one

They get conflated constantly, and they have very different odds. Keeping them apart is
what stops the tool being trusted about one thing and caught lying about another.

| Problem | Approach | Tractability |
|---|---|---|
| **Depth** — how deep will these settings cut | Physics, two measured constants | Good. Extrapolates. |
| **Visible layering** — will the slices show as contour lines | Pure geometry, once depth is known | Already in reach |
| **Slag, discolouration, texture** | Empirical only. No usable model exists | Interpolation inside tested ground only |

A tool that reports "0.9 mm deep" and "probably won't slag" with the same confidence has
lied about the second one. The confidence must be visibly different.

---

## 2. Settled physics

### 2.1 The logarithmic ablation law

Depth removed per pulse on a metal, above threshold:

```
d = δ · ln(F / F_th)
```

- `d`    depth per pulse
- `δ`    effective penetration depth — a fit parameter, not a handbook value
- `F`    applied fluence, J/cm²
- `F_th` ablation threshold fluence, J/cm²

Two regimes appear as two slopes on a semi-log plot: a shallow **optical** branch
(`l_α`, order 10 nm) at low fluence and a deeper **thermal** branch (`l_th`, order 1 µm)
above it. Nanosecond metal engraving lives in the thermal branch.

### 2.2 Fluence from the settings you actually set

```
E_pulse = P_avg / f_rep                    pulse energy, J
A_spot  = π · (d_spot / 2)²                spot area, cm²
F       = E_pulse / A_spot                 fluence, J/cm²
```

### 2.3 Pulses per spot, and depth per pass

```
pulses_per_spot ≈ (d_spot / v) · f_rep     v = scan speed
lines_per_spot  ≈ d_spot / interval        raster overlap
depth_per_pass  ≈ d · pulses_per_spot · lines_per_spot
```

**This does not stay linear.** Depth per pass falls as the pocket deepens — the beam
defocuses, debris shields the floor, heat accumulates. Expect a curve that rolls over.
Fit the roll-off; do not assume it away.

That roll-off is why Z-step error is asymmetric: commanding a step *smaller* than actual
removal self-corrects, commanding one *larger* runs away and cannot recover inside the job.
**Bias low, never high.**

### 2.4 Liu's D² method — spot size and threshold from one coupon

Fire single pulses at a range of energies. Measure each crater diameter. Plot D² against
ln(E):

```
D² = 2·w₀² · ln(E / E_th)
```

- slope       → `2w₀²`, giving the **true spot size**
- x-intercept → `E_th`, giving the **ablation threshold**

Two of the three constants the whole model needs, from one experiment, measured laterally
rather than in depth — which matters enormously for cost (§5).

> Liu, *Simple technique for measurements of pulsed Gaussian-beam spot sizes*,
> Optics Letters 7(5) 196, 1982. An extension for imperfect (non-Gaussian) beams was
> published in 2021 and is the one to use for a fibre source.

### 2.5 Incubation — the threshold moves

Threshold falls with accumulated pulses on the same spot:

```
F_th(N) = F_th(1) · N^(S−1)        S ≈ 0.8–0.9 for many metals
```

Multi-pass engraving lives **entirely** in the incubated regime, so a single-pulse
threshold is not the number that governs a real job. Most published incubation work is
ultrafast rather than nanosecond; treat ns values as thinly supported.

---

## 3. Constants we have, and exactly how much they are worth

### 3.1 Published, nanosecond, our wavelength

Nd:YAG, **4.5 ns** pulses, polished bare samples, 20 consecutive pulses:

| Metal | F_th @ 1064 nm | F_th @ 532 nm |
|---|---|---|
| Aluminium | ~4.0 J/cm² | ~2.0 J/cm² |
| Titanium | ~4.5 J/cm² | ~1.0 J/cm² |
| Copper | ~5.5 J/cm² | ~8.0 J/cm² |

Logarithmic coefficients **1.1–2.8 µm per ln unit**; optical depths `l_α` **6.7–14 nm**;
thermal depths `l_th` **0.40–1.43 µm**.

> *The dependence of the ablation rate of metals on nanosecond laser fluence and
> wavelength*, Journal of Optoelectronics and Advanced Materials.

**Evidence grade: C.** Right physics, right wavelength, right pulse regime — wrong alloy,
wrong pulse duration, wrong surface, and two orders of magnitude shallower than our target
depth.

### 3.2 First-principles estimate — available for any material

Where nothing is published, estimate from tabulated thermophysical properties:

```
δ_th  ≈ √(D · τ)                                   D = thermal diffusivity, τ = pulse duration
F_th  ≈ ρ · δ_th · (c·ΔT + L_melt + L_vapour)
```

Worked example, brass at τ = 100 ns:

```
D     = 3.4e-5 m²/s      →  δ_th = √(3.4e-5 × 1e-7) = 1.84 µm
ρ     = 8500 kg/m³
c·ΔT  = 380 × 1000       = 380,000 J/kg
L_m   = 170,000 J/kg
L_v   = 4,700,000 J/kg
F_th  ≈ 8500 × 1.84e-6 × 5,250,000 = 82,300 J/m²  =  8.2 J/cm²
```

Against a measured 5.5 J/cm² for copper, that is the right order and wrong by roughly half.
**Evidence grade: D.** Good enough to centre an experiment. Not good enough to predict a job.

### 3.3 What cannot be looked up at all

- **Alloys.** Copper is tabulated. C360 free-machining brass with 2.5–3.7 % lead is not,
  and the lead changes both the thermal behaviour and what comes off the surface.
- **Black anodized aluminium — the material that matters most to us.** This is not bulk
  metal ablation. It is removal of a thin dielectric oxide layer from a metal substrate: a
  different physical problem, very sparsely published. For the tonal work on anodized,
  measuring our own grids is not a fallback, it is the only route there has ever been.
- **The >0.5 mm nanosecond regime.** Academic ns work is nearly all sub-100 µm. A 1.1 mm
  coin pocket is 10–100× beyond it, straight through the roll-off nobody publishes.
- **Our own spot size.** WeCreat state the Lumos Ultra UV spot as **6–8 µm**. Fluence goes
  as 1/d², so that range alone is a **1.8× uncertainty** in every prediction — larger than
  any other error in the model. This is the single most valuable thing to measure, and
  §2.4 measures it.

---

## 4. How the program learns

The tool must never be a lookup table pretending to be a model, nor a model pretending to
be measured. The loop:

```
   tabulated properties  ──►  computed prior      (grade D)
   published literature  ──►  literature prior    (grade C)
                                    │
                                    ▼
                          prediction + wide band
                                    │
                              user cuts a coupon
                                    │
                                    ▼
                          measurement (grade A)
                                    │
                                    ▼
                       refit δ and F_th for THIS
                    material × source × lens × τ
                                    │
                                    ▼
                        prediction + narrow band
```

Rules that keep it honest, all of which already exist in the LaserTuner schema:

1. **Grades never blend.** An A-grade measurement supersedes a C-grade prior; it is not
   averaged with it.
2. **Published settings are never recipes.** They enter as graded evidence with an
   applicability score against a specific machine, source and lens.
3. **Refuse to extrapolate on appearance.** Inside the tested envelope: "this slagged."
   Outside it: "no evidence" — never a number.
4. **Predictions carry their provenance.** Every figure on screen can say which grade it
   came from, and the uncertainty band must visibly widen for a seeded prediction.
5. **Two constants, not a table.** Fitting `δ` and `F_th` lets the model predict
   combinations that were never tested. Collecting a table only ever reproduces what was.

---

## 5. Measuring a test engraving without a laboratory

The obstacle for every contributor. A stylus profilometer is the right instrument and
costs more than the laser. These are the routes that work, cheapest first, with the
arithmetic so nobody has to take it on trust.

### 5.1 Mass loss — the best value by a wide margin

Weigh the coupon before and after. Depth follows from density and engraved area:

```
depth = Δmass / (ρ · area)
```

Sensitivity on a 20 × 20 mm brass pocket:

| Scale resolution | One count equals | ≥1 % precision above |
|---|---|---|
| 0.1 g | 29.4 µm | — |
| 0.01 g | 2.94 µm | 294 µm |
| **0.001 g** (jeweller's, ~$25) | **0.29 µm** | **29 µm** |
| 0.0001 g | 0.03 µm | 2.9 µm |

A £20 jeweller's scale resolves sub-micron average depth and reaches 1 % precision by
about 30 µm — **better than any affordable depth gauge**, and it is immune to the burr on
the pocket edge that ruins indicator readings.

What it costs you:
- It measures **average** depth over the pocket, not a profile. For fitting δ and F_th from
  a step wedge that is exactly what you want.
- Debris and recast must be completely removed. Ultrasonic cleaner, dried, weighed cold.
- Oxide **adds** mass and will under-report depth. Weigh promptly.
- Engraved area must be known accurately — it enters linearly.

### 5.2 Dial test indicator — step height, with a hard ceiling

A lever-type DTI on a stand against a reference surface, measuring the step between
untouched surface and pocket floor.

Worked example — **Mitutoyo 513-402-10E**, a horizontal lever type, and the instrument this
project actually has to hand:

| | |
|---|---|
| Graduation | 0.0005″ = **12.7 µm** |
| Repeatability | 0.0002″ = **5.1 µm** |
| Hysteresis | 0.0002″ = **5.1 µm** |
| Range | 0.03″ = **0.76 mm** |
| Dial | 0-15-0 |
| Stylus length | 16.4 mm |
| Measuring force | ≤ 0.3 N |

**Read that table carefully: 0.0002″ is repeatability and hysteresis, not accuracy.** Retail
listings print it under a "Measurement Accuracy" heading and it is not the same claim. That
distinction happens to fall in our favour. A depth measurement here is a *difference* of two
readings taken minutes apart on the same instrument in the same setup — untouched surface,
then pocket floor. What limits that difference is repeatability and hysteresis, both 5.1 µm,
not the instrument's absolute accuracy over its full travel, which is looser. The DTI is a
comparator, and we are using it comparatively.

Four limits worth knowing before relying on it:

1. **The range is shorter than a coin pocket.** 0.76 mm against a 1.1 mm target. It cannot
   measure a finished coin depth in one reading — only steps within its travel.
2. **12.7 µm per division against 10–30 µm removed per pass** means a single pass is one to
   two divisions. Fine for a step wedge of 10 or 50 passes, marginal for one.
3. **The contact ball cannot enter narrow features.** Make test pockets at least ten times
   the ball diameter — measure the ball you actually have, since contact points are a
   consumable and yours may not be the one it shipped with. Fine on a 20 × 20 mm pocket,
   useless inside engraved lettering.
4. **Cosine error.** A lever indicator reads the component along its stylus arc. At 10° off
   perpendicular you lose 1.5 %; at 20°, 6 %. Always the same sign — it under-reads — so it
   biases a fit rather than just scattering it.

Verdict: keep it, use it for step wedges and for cross-checking the scale. Do not make it
the primary instrument.

### 5.3 USB microscope — and why Liu's method is the cheap experiment

Liu's D² needs crater **diameter**, not depth — which is fortunate, because lateral
measurement is far cheaper than vertical.

The class of instrument in question is the ~$40 LCD "coin microscope" — the Elikliv EDM4 and
its many clones. **Ignore every number on the box.** The "1000×" claim is meaningless: it is
screen-diagonal magnification, it depends on how far the stand is racked, and reviews of the
EDM4 say plainly that it is "significantly overstated" without anyone being willing to state
what the real figure is. Nobody publishes working distance or field of view either.

None of that matters, and this is the point worth internalising: **a calibrated instrument
does not need honest specifications.** Photograph a stage micrometer once at a fixed stand
height, count pixels between rulings, and you have µm/pixel measured. Every claim on the box
is then irrelevant. Change the stand height and you recalibrate — which is why the stand
height must be locked and recorded for a measurement session.

So **a calibration slide (~$10) is not an accessory, it is the thing that converts a toy into
an instrument.** Without it the scope produces pictures; with it, measurements.

#### What a calibration slide is

A **stage micrometer**: a standard 25 × 75 mm glass microscope slide with a precisely ruled
scale on it, normally 1 mm divided into 100 divisions of 0.01 mm (10 µm). Sold as "stage
micrometer" or "microscope calibration slide 0.01mm", $8–20. The ruling is lithographic and
accurate to a fraction of a micron — better than everything else in the chain by a wide
margin, which is what makes it the reference.

Using it takes five minutes, once per stand height:

1. Set and **lock** the stand height you will use for the session.
2. Photograph the slide through the PC connection.
3. In ImageJ, count pixels spanning a known number of divisions. Twenty divisions across
   256 px is 200 µm ÷ 256 px = **0.78 µm/px**.
4. Record that figure alongside the stand height. Change the height, repeat step 1.

Two practical traps, both specific to a top-lit coin scope:

- **Lighting.** Most cheap stage micrometers are chrome ruling on clear glass, intended for
  *transmitted* light. A coin scope lights from above with an LED ring, so the glass goes
  dark and the chrome glares. Put black card underneath and take the LEDs off-axis; it works,
  it just is not the intended mode. A reflective micrometer (chrome on an opaque substrate)
  avoids the problem if one can be found at a sane price.
- **Calibrate at the centre, then check the edge.** A $40 lens has barrel distortion, so the
  scale near the frame edge is not the scale in the middle. Photograph the ruling centred and
  again pushed to the edge. If they differ by more than a percent or two, confine every real
  measurement to the middle third of the frame. **This is the step people skip**, and skipping
  it biases every subsequent measurement in one direction rather than merely scattering it.

The one specification that does constrain the result is sensor resolution. These units are
**720p — 1280 pixels across, not 1920.** Resolution in the only units that matter:

| Field of view (1280 px across) | Resolution |
|---|---|
| 1.0 mm | 0.78 µm/px |
| 2.0 mm | 1.56 µm/px |
| 4.0 mm | 3.13 µm/px |
| 8.0 mm | 6.25 µm/px |

Two consequences:

- **Measure on the PC, never off the LCD.** The 4.3″ screen cannot be measured; you need
  pixel coordinates in something like ImageJ. "PC view compatible" is the load-bearing
  feature on that listing, not the screen.
- **The smallest achievable field of view is the unknown that decides everything**, and it is
  not published. First thing on arrival: photograph a ruling at maximum zoom and measure the
  FOV. That single number determines whether the instrument is fit for a given laser.

Against expected crater sizes, assuming ~1 mm FOV is reachable:

| Source | Spot | Craters near threshold | At 0.78 µm/px | Verdict |
|---|---|---|---|---|
| Fibre MOPA | 20–60 µm | 20–180 µm | 26–230 px | Comfortable |
| Diode | 60–100 µm | 60–300 µm | 77–385 px | Easy |
| UV galvo (Lumos) | 6–8 µm | 6–24 µm | 8–31 px | **Marginal** |

**The awkward finding is that the marginal case is the one doing the coin work.** An 8-pixel
crater cannot be measured to the few percent that a D² fit wants, and edge definition on a
recast rim is poor at the best of times.

#### The fix for a small spot: measure a line, not a crater

Scan a single line instead of firing single pulses, and measure its **width**. The same
functional form holds — the ablated width is where local peak fluence crosses threshold, so
`W² = 2w₀²·ln(F₀/F_th)` — but a line gives you a long straight edge instead of one small
circle. Sample the width at fifty points along it and average; fit the edge sub-pixel. A
measurement that is hopeless on an 8-pixel disc is straightforward on an 8-pixel-wide,
2000-pixel-long stripe.

One caveat that must be carried through, because it changes what the number means:
**a scanned line overlaps pulses, so the threshold it yields is `F_th(N_eff)`, not
`F_th(1)`.** Recover the single-pulse value through the incubation relation
`F_th(N) = F_th(1)·N^(S−1)`, where `N_eff` follows from spot size, pulse repetition rate and
scan speed. Better still, vary the overlap deliberately and **fit `S` from the same
experiment** — incubation is needed anyway for multi-pass depth prediction, so this is a
measurement we would otherwise have to go and get separately.

#### Getting depth out of it — focus stepping against the indicator

A microscope measures depth only through focus, and this one has no Z readout. It can borrow
one: mount the dial test indicator to read the stage's vertical travel, focus on the
untouched surface, note the reading, rack up until the pocket floor is sharpest, note it
again. The difference is the depth.

- Resolution is set by **depth of field**, not by the indicator. At the modest real
  magnification of a coin scope the DOF is large — expect tens of microns even with a
  focus-measure algorithm (variance of Laplacian) picking the sharpest frame.
- So it resolves a whole pocket, not individual terraces.
- Worth knowing because it makes the indicator and the scope together do something neither
  does alone, and it needs no further purchase. It is a cross-check on mass loss, not a
  replacement for it.

### 5.4 Known-depth reference plate — the cheapest comparator

Engrave a plate with steps at known depths and compare new work against it by eye under
magnification. Practitioners report discriminating about 0.003″ (76 µm) this way. Coarse,
qualitative, needs a calibrated plate to begin with — but it costs nothing and it is how
most people actually judge depth today.

**This is what our `--calibrate` coupon already is.** It just needs measuring once with one
of the methods above, after which it becomes a transfer standard.

### 5.5 Cross-section — the referee

Cut the coupon, mount, polish, measure the profile under the microscope. Destructive and
slow, and the only method that shows the pocket *shape* — wall angle, floor flatness, taper.
Do it once per material to validate the cheap methods, not routinely.

### 5.6 Recommended contributor kit

| Item | ~Cost | Buys you |
|---|---|---|
| Jeweller's scale, 0.001 g | $25 | Depth to sub-micron average |
| LCD/USB microscope + **calibration slide** | $50 | Liu's D²: spot size and threshold |
| Ultrasonic cleaner | $40 | Trustworthy mass readings |
| Dial test indicator + stand *(optional)* | $60–150 | Step height, independent cross-check |

**Under $120 gets a contributor to a real measurement.** That is the number to put in front
of anyone who thinks this needs a metrology lab. The DTI is the one line that can be skipped
entirely — it is the most expensive item and the least capable of the three, and a project
already owning one should treat it as the cross-check rather than the reason not to buy the
scale.

---

## 6. What the tool does with all of it

- **3D preview driven by settings.** The existing relief renderer already quantises to a
  slice count and draws in milliseconds. Feed it depth-per-pass and the surface genuinely
  changes shape as power or speed is dragged, because the realisable level count changes.
- **Terrace prediction.** `terrace width = level step / |∇depth|`, compared against spot
  size, says *where* the slicing will show as contour lines rather than merely that it
  might. Pure geometry — no constants needed beyond depth per level.
- **The seam must be visible.** Geometry can be rendered faithfully. Slag, discolouration
  and heat-affected zone cannot. If both arrive in the same confident render, the tool has
  lied about one. Mark the regions where settings sit outside anything tested.

---

## 7. Related

- `CLAUDE.md` — established file-format facts, and the corrections already made.
- LaserTuner (`D:\DevHome\LaserTuner`) — the Recipe → Run → Result store this model would
  draw its measured constants from. `Documentation/Evidence_Sources.md` there carries the
  A–E grading scale referenced throughout this document.
