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
- **Loose** debris must be removed, consistently, or the variation between weighings becomes
  the noise floor. Ultrasonic, dried thoroughly, weighed cold — a coupon still warm from the
  laser reads light, because convection lifts it off the pan.
- **Recast that is still bonded is part of the coupon and correctly stays.** It resolidified
  without leaving, so it was never removed, and the mass balance is right. Note that this
  makes mass loss and a profile measurement answer subtly different questions: mass gives net
  material gone, a profile gives pocket geometry, and **the difference between them is the
  recast** — which is informative rather than a problem.
- Engraved area must be known accurately — it enters linearly.

**Handle with tweezers or gloves.** A fingerprint deposits on the order of 0.1–1 mg, which is
one to ten scale counts. Against a 191 mg signal that is under a percent; against a shallow
20 µm cut it is several. It is free to avoid and impossible to correct afterwards.

**Oxide is a real effect but a small one**, and an earlier draft of this document overstated
it. A 50 nm tarnish over both faces of a 25 mm coupon is about 0.4 mg; a heavier heat-tint of
1 µm confined to the engraved zone is about 1.4 mg. So it sits at roughly the same scale as a
fingerprint — under 1 % of a 100 µm measurement. Weigh promptly and do not agonise.

#### Cleaning: the rule is remove loose material and nothing else

This is the governing constraint, and it disqualifies most of what is sold for ultrasonic
cleaning. **Any chemistry that attacks the substrate removes mass, and removed mass is
indistinguishable from engraved depth.** A cleaner that etches does not add noise — it adds a
systematic, invisible, in the same direction every time.

- **Use water with a few drops of plain dish detergent.** Surfactant drops the surface tension
  so cavitation reaches the surface. Neutral, non-attacking, effectively free.
- **Avoid ammoniated cleaners on brass.** Much jewellery ultrasonic solution is ammonia-based,
  and ammonia causes dezincification and stress-corrosion cracking in brass. It will attack
  the coupon and report the attack as depth.
- **Avoid acidic or descaling solutions** for the same reason — they strip oxide by dissolving
  metal along with it.
- **Never put isopropyl alcohol directly in the tank.** Cavitation, heat and a flammable
  solvent is a genuine fire hazard. If a solvent rinse is needed, stand a beaker of it in the
  water-filled tank; the water couples the ultrasound through.

**The control that proves the process, and it costs one coupon.** Take an unengraved blank,
weigh it, run the complete cleaning and drying cycle, weigh it again. **If the mass moved,
the process is removing substrate** and every depth figure derived from it is inflated. Run
this before trusting any measurement, and again whenever the solution or routine changes.

**Sizing the cleaner — and the risk runs the other way.** An earlier draft of this document
said to target 50–100 W/L of ultrasonic power. **That figure was wrong by roughly 5×.**
Industry guidance is **8–15 W/L of tank capacity**, and small benchtop units in practice run
around 18 W/L. Almost any consumer cleaner clears that bar, so adequacy is not the question.

The question is the opposite one. **Too much power causes cavitation erosion** — and on a
mass-loss measurement, eroded substrate is indistinguishable from engraved depth. Brass is
soft. So for this application:

- Ultrasonic power is a **non-constraint**: do not pay for more of it.
- **40 kHz is the right frequency.** Lower (25–28 kHz) cavitates more aggressively and erodes
  more; higher (80 kHz+) is gentler but costs more and is unnecessary for loose debris.
- **Keep cycles short** — minutes, not tens of minutes. We are lifting loose particulate off
  a flat open surface, which is about the easiest job an ultrasonic cleaner is ever given.
- **Do not use the heater.** Heat encourages oxide on brass and accelerates any chemical
  attack, for no benefit on contamination this loose. If it is used, let the coupon return to
  room temperature before weighing.
- **Prefer a unit with a reduced-power or "gentle" mode.** Since erosion rather than
  insufficiency is the failure mode here, the ability to turn the power *down* is worth more
  than a higher rating. A built-in degas mode is a minor convenience — it automates the
  pre-run described below, which can be done manually on any unit.
- **The blank-coupon control above detects erosion directly.** That is now its most important
  function, not a formality.

**Let the control decide the setting rather than guessing.** Run the blank coupon at full
power first. If its mass is stable, full power is fine and gentle mode is unnecessary. If mass
drops, switch to gentle and repeat until it does not. This is one coupon and twenty minutes,
and it replaces an unanswerable question about how much cavitation brass tolerates with a
measurement.

**What actually matters in the specification is the timer.** A digital timer means every
coupon gets an identical cycle, so debris removal is consistent between weighings. Consistency
is the whole game here; raw power is not.

Two points of technique worth more than any specification:

- **Clean each coupon in a small beaker of solution standing in the tank**, rather than in the
  tank itself. Fresh solution per coupon prevents debris from an earlier coupon redepositing
  on a later one — at 1 mg resolution that is a real effect, not a theoretical one. It also
  uses almost no detergent and keeps the tank as plain water. Suspend the beaker rather than
  resting it on the tank floor.
- **Run the filled tank for a few minutes before loading it.** Fresh water is saturated with
  dissolved gas, which damps cavitation. Degassing costs nothing and measurably helps.

A tank of 500 mL–1 L is all this task needs. A larger one is not worse for the measurement —
the beaker makes tank volume irrelevant — it is simply more cleaner than the job requires.

**Accuracy versus repeatability, again.** A £20 scale displaying 0.001 g typically has
linearity error of several milligrams across its range. That does not matter here for the same
reason it did not matter for the dial indicator: we take a **difference** of two weighings,
minutes apart, at nearly the same mass. Repeatability governs, and it is close to the display
resolution. Check it by weighing the same coupon five times, removing it from the pan between
each.

#### A step wedge cannot be weighed in one go

Easy to miss, and it changes how the coupon must be cut. Mass loss gives the **total** removed
from whatever you put on the pan. A wedge with ten zones at ten different powers, engraved in
one job, yields one number — and one number cannot fit two constants across ten levels.

Two ways out, and the choice is not close once the numbers are written down:

1. **One small coupon per power level, all cut from the same plate.** Weigh, engrave, clean,
   weigh. Ten coupons, twenty weighings, no ordering constraint. Cutting them from one plate
   keeps alloy, temper and surface finish common, which was the only real argument for the
   single-plate method.
2. **Sequential engrave-and-weigh on one large plate.** Weigh, cut zone 1, clean, weigh, cut
   zone 2… Works, but it is the weaker option.

**Why the small coupons win — signal against total mass.** The scale resolves 1 mg wherever
it sits, but noise, drift and linearity all scale with load, and the ratio being asked for is
what matters:

| Approach | Coupon mass | Mass removed at 100 µm | Ratio |
|---|---|---|---|
| 100 × 75 × 3 mm plate, 15 × 15 mm zone | 191 g | 191 mg | 1 : 1000 |
| **25 × 25 × 3 mm coupon, 15 × 15 mm zone** | **16 g** | **191 mg** | **1 : 83** |

The same 191 mg signal, against an eighth of the load. A cheap scale asked for one part in a
thousand will be fighting thermal drift across the session; one part in eighty-three is
comfortable. The single plate also serialises the whole experiment — one botched zone and the
ordering is compromised.

**Zone size is set by the measurement, not by convenience.** Depth resolved per scale count,
brass, 0.001 g:

| Zone | Per count | ≥1 % precision above |
|---|---|---|
| 20 × 20 mm | 0.29 µm | 29 µm |
| **15 × 15 mm** | **0.52 µm** | **52 µm** |
| 10 × 10 mm | 1.18 µm | 118 µm |
| 5 × 5 mm | 4.70 µm | 470 µm |

So **zones must be at least 10 × 10 mm, and 15 × 15 mm is the sensible default.** A 5 mm zone
throws away the scale's entire advantage over the dial indicator. This is a constraint on
`--calibrate`, which currently sizes the wedge to fit the blank rather than to the measurement
that has to be made from it.

#### Buying the scale: capacity is the spec that bites

Resolution alone does not choose a scale, and the obvious cheap one is the wrong one.

The common inexpensive milligram scale is **20 g × 0.001 g**, sold for reloading. Twenty grams
does not weigh a coupon:

| Coupon | Mass |
|---|---|
| 25 × 25 × 3 mm brass | 16 g |
| 40 mm × 3 mm brass coin blank | **32 g** |
| 40 mm × 4 mm brass blank | **43 g** |
| 50 × 50 × 3 mm brass | **64 g** |

**Specify 0.001 g resolution and at least 50 g capacity**, ~$30–40. 100 g is better if it does
not cost more. Also:

- **Confirm a calibration weight is included**, or add one (~$10). A scale that cannot be
  checked cannot be trusted.
- **A draft shield is not optional at 1 mg.** Room air currents swamp the last digit. Most
  scales at this resolution ship with a cover; use it.
- **Keep a check mass on the bench** and weigh it at the start and end of every session.
  Cheap scales drift with temperature, and a drift that develops mid-session otherwise looks
  exactly like a depth measurement.
- Read reviews for *repeatability* specifically. A displayed third decimal that is pure noise
  is common in this price bracket.

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

#### Better: put the scale in the same frame as the measurement

The procedure above ties the calibration to a stand height, which makes every later
measurement depend on that height being reproduced. There is a strictly better way.

**Lay the micrometer beside the coupon, shim whichever surface is lower until the two are
coplanar, and photograph them together.** The scale is then measured *in the very image being
measured from*.

This is worth doing even when nothing forces it, because it deletes whole classes of error at
once:

- Stand repeatability stops mattering — the scale travels with the picture.
- Focus drift stops mattering, including **autofocus**, which otherwise changes image scale
  between the calibration shot and the measurement shot in any non-telecentric system.
- Session-to-session drift stops mattering. An image taken a year later is still calibrated.

A 1 mm ruling fits inside any field of view small enough to be measuring craters in, so there
is rarely a reason not to. Check coplanarity by focus: if both surfaces are sharp together,
they are in the same plane.

**This is the answer to autofocus.** An autofocus microscope is otherwise a poor instrument
for calibrated work, and cheap ones rarely document a manual mode or a focus lock. Same-frame
calibration makes the objection go away rather than managing it.

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

#### The threshold needs no microscope at all

Worth stating plainly because it removes the microscope from the critical path: **`F_th` can
be measured with calipers.**

Engrave a ladder of short line segments at stepped power — say 50 segments two percent apart
— and find the first segment that marks the surface at all. That position *is* the threshold,
to within one step. The measurement is "which segment", read at millimetre scale, instead of
"how wide is this crater", read at micron scale.

This is the Vernier trick: **use a gradient to convert a measurement you cannot make into one
you can.** The information was always there; a ladder just spreads it across 100 mm of coupon
instead of compressing it into 8 µm of crater.

Consequences worth being explicit about:

- Resolution is set by step size, not by optics. Fifty steps gives 2 %; two hundred gives
  0.5 %, and the coupon is still only a few centimetres long.
- The result is a threshold **in commanded power**, not in J/cm². Converting needs the
  power-versus-command curve for that source, which is a separate calibration — but one that
  is needed for every other number in this document too, so it is not an extra debt.
- It does **not** give spot size. `w₀` still needs the D² slope or a line width.

So the microscope is required for one of the two constants, not both. A scope that turns out
mediocre costs precision on `w₀` rather than blocking step 7.1 outright.

#### Viewing angle: vertical for measuring, oblique for depth

Worth separating two things that get conflated, because they pull opposite ways.

**For the measurements that matter, vertical is correct and oblique is an error.** Crater
diameter and line width are lateral measurements in the plane of the surface. Tilt the camera
and a circle images as an ellipse, foreshortened by `cos θ` along the tilt axis. Every scope
in this class shoots straight down, and that is the right geometry — not a limitation.

**But tilting the sample deliberately turns depth into a lateral measurement.** A step of
depth `d`, viewed at `θ` from the surface normal, projects its riser to a lateral extent of
`d·sin θ` in the image:

| Tilt | Projected offset per 100 µm of depth | At 1.56 µm/px |
|---|---|---|
| 20° | 34 µm | 22 px |
| 30° | 50 µm | 32 px |
| 45° | 71 µm | 45 px |

This is the same manoeuvre as the power ladder in reverse: **convert a measurement the
instrument cannot make into one it makes easily.** No special microscope is needed — an angle
block, a sine bar or a cheap tilting vice puts the coupon at an angle under any vertical
scope. **A 3D-printed wedge is sufficient and costs nothing**, because the angle it actually
holds does not need to be accurate — see below.

And it is **self-calibrating**, which removes the need to trust the angle block at all. Tilt
the calibration slide along with the coupon and the geometry measures itself:

- A **circular dot of known diameter images as an ellipse** whose minor axis is `D·cos θ`.
  The WintopScope slide's 0.15 mm and 0.07 mm dots do this directly — measure the ellipse,
  get the tilt.
- Ruling spacing along the tilt direction shrinks by `cos θ` while spacing across it does
  not, so crossed scales give the same answer independently.

Combined with same-frame calibration, one photograph then carries the scale, the tilt angle
and the measurement together, and nothing about the rig has to be trusted or reproduced.

Caveats: the pocket must be wide enough that its own wall does not occlude the floor
(irrelevant at 20 mm wide and 0.1 mm deep, fatal inside lettering), and a tilted field runs
out of depth of field quickly — focus on the step edge and let the rest blur.

#### Lighting: even for metrology, grazing for seeing

Both geometries have a job, and they are not interchangeable.

- **Coaxial or ring illumination is what you want for measuring.** Even, symmetric light gives
  an edge that sits in the same place all the way round. Directional light shadows one side of
  a crater and blows out the other, **biasing the measured diameter** rather than merely
  adding noise.
- **Grazing side light is what you want for seeing** — topography, wall angle, recast rim,
  and a chrome ruling on clear glass under a top-lit scope.

So a scope with both is genuinely better, and two flexible side lights placed symmetrically
approximate a ring well enough for measurement. **Do not let lighting drive the purchase
anyway**: a clip-on LED ring is $10–15, and a paper diffuser ring costs nothing. Buy for the
specifications that cannot be retrofitted — sensor resolution, focus mechanism, stand rigidity
— and fix the lighting afterwards.

#### Getting depth out of it — focus stepping against the indicator

A microscope measures depth only through focus, and these have no usable Z readout. One can
be borrowed: mount the dial test indicator to read the stage's vertical travel, focus on the
untouched surface, note the reading, rack up until the pocket floor is sharpest, note it
again. The difference is the depth.

**A trap worth naming.** Some autofocus models advertise a **time-of-flight sensor** for
"accurate distance measurements". That sounds like the Z readout this method wants and it is
not. Consumer TOF ranging parts resolve about **1 mm** with accuracy of several millimetres —
they exist to tell the lens roughly where the subject is. Against a 1.1 mm pocket and 10–30 µm
terraces that is three to four orders of magnitude short, and the reading is internal to the
autofocus system rather than exposed. **No microscope in this class measures depth
electronically.** Depth comes from focus against an external scale, or it does not come.

- Resolution is set by **depth of field**, not by the indicator. At the modest real
  magnification of a coin scope the DOF is large — expect tens of microns even with a
  focus-measure algorithm (variance of Laplacian) picking the sharpest frame.
- So it resolves a whole pocket, not individual terraces.
- Worth knowing because it makes the indicator and the scope together do something neither
  does alone, and it needs no further purchase. It is a cross-check on mass loss, not a
  replacement for it.

#### Choosing one, when the deciding specification is unpublished

Surveyed across the price range in September 2026, and the result is consistent: **nobody
publishes field of view.** Reviews of the $40 coin scopes say the magnification claim is
"significantly overstated" and then decline to give a real figure. A $150 Andonstar ADSM302
review has no field-of-view measurement either, and its "12 MP, 4032 × 3024" stills come off
a stated **3 MP sensor** — interpolated pixels sold as resolution. Assume interpolation
anywhere the photo resolution exceeds the sensor.

Two conclusions follow.

**Do not buy up into a soldering microscope.** The ADSM302's headline feature is a 114 mm
working distance, so an iron fits underneath. Working distance trades directly against
magnification. For crater work the requirement is the opposite — get the lens as close as the
stand allows — so the cheap coin scope's short standoff is the feature, and the more
expensive instrument is *worse* at this particular job.

**Buy the cheap one and qualify it inside the return window.** Under unpublished specs, a
return policy is the specification. Acceptance test, in order:

1. Photograph the slide at maximum usable zoom, compute µm/px. **Target ≤1.5 µm/px; reject
   above ~3.**
2. Photograph the ruling centred, then at the frame edge. **Scale must agree within ~2 %**
   across the middle third, or confine all measurement to the centre.
3. Confirm PC capture delivers full sensor resolution rather than a downscaled preview
   stream — compare a PC frame against a card still.
4. Rack the stand away and back, re-shoot the slide. **Does the scale return?** A stand that
   does not repeat means recalibrating every session.

One thing relaxes the threshold: an edge can be fitted **sub-pixel**. Averaging a straight
edge along its length reaches roughly 0.1 px on edge position — the same principle as
slanted-edge MTF testing. A 5 px wide line is therefore measurable; a 2 px line is not. This
is the reason the line-width variant matters more than the crater for a small spot.

**Do not try to infer field of view from the "Real Angle of View" row.** An earlier draft of
this document did exactly that — the Elikliv EDM4 lists 16°, which if taken as a lens field
angle implies 2–4 µm/px at any plausible standoff. The inference is unsound: the sibling
EDM9 Max lists **178°** in the same field, which is an IPS panel viewing-angle specification
and cannot be optics. The field is populated with whatever the vendor had to hand. Field of
view remains simply **unknown**, and the acceptance test above is the only way to learn it.

#### Reading the model range — resolution is the one axis that matters

Sensor resolution is the spec that translates directly into measurement quality, and it is
the one most consistently obscured. A worked example of decoding it, from one vendor's range
in September 2026:

| Model | Claimed stills | Actual video | Pixels across |
|---|---|---|---|
| EDM4 (4.3″) | — | 720p | 1280 |
| EDM9 (7″) | 12 MP | **1080p** | 1920 |
| EDM9 Pro (7″) | 16 MP | **1080p** | 1920 |
| EDM9 Max (10.1″) | 20 MP | *unstated* | 1920 (inferred) |
| EM4K-AF (8″) | 52 MP | **4K / 3840×2160** | 3840 |

The pattern is plain once laid out. **The megapixel number escalates across a product line
whose actual video resolution does not move**, because it is an interpolation factor rather
than a sensor. The EDM9 Max declines to state a video resolution at all — but a 4K product
puts "4K" and "3840P" in its title, and this one does not, while its immediate siblings say
1080p outright.

Two rules generalise from this:

1. **A stated megapixel figure is worthless; find the video resolution.** Video cannot be
   faked upward the way a still can, so it reveals the sensor.
2. **Silence about a headline spec is evidence against it.** Vendors advertise 4K when they
   have it.

Consequence for spending: within such a range, the mid-price model is usually the worst value
*for measurement*, because its premium buys screen size, stand quality and lighting rather
than pixels. Those are real improvements to an instrument — but the ones that matter least
here, especially once same-frame calibration has removed the stand-repeatability requirement.

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

| Item | ~Cost | Buys you | |
|---|---|---|---|
| Scale, **0.001 g × ≥50 g** | $30–40 | **Depth.** The primary instrument | Essential |
| Stage micrometer | $16 | Scale, procedure check, tilt angle | Essential |
| LCD/USB microscope | $40–200 | Spot size `w₀` only | Essential |
| Brass/stainless stock for coupons | $20 | Something to cut | Essential |
| Ultrasonic cleaner, **any size, with a timer** | $40–65 | Repeatable mass readings | Strongly advised |
| Small glass beaker | $5 | Fresh solution per coupon | Strongly advised |
| Calibration weight, if not bundled | $10 | A scale you can check | Strongly advised |
| Dish detergent | — | The only cleaning chemistry allowed | Essential |
| Tweezers or nitrile gloves | $5 | A fingerprint is 0.1–1 mg | Essential |
| Clip-on LED ring | $15 | Even light for unbiased edges | Optional |
| 3D-printed tilt wedge | — | Depth as a lateral measurement | Optional |
| Dial test indicator + stand | $60–150 | Step height cross-check | Optional |

**The two that get forgotten are the scale and the stock.** It is easy to buy the microscope,
because it is the interesting object, and then discover that the microscope measures spot size
while the *scale* measures depth — and depth is what the model is for. A kit with a microscope
and no scale cannot complete step 7.2 at all. This is not a hypothetical failure mode; it
happened while assembling the kit below, twice, with the omission caught at the checkout page.

#### A worked example — one actual kit, September 2026

Specific products date quickly and the specification above is what matters. These are recorded
so the reasoning has something concrete attached to it, not as endorsements.

| Bought | Why this one |
|---|---|
| **Elikliv EM4K**, 8″, ~$200 | 4K (3840 px) is the only spec that converts to measurement quality. Manual focus wheel, not the `-AF` autofocus sibling — autofocus changes image scale between calibration and measurement. |
| **WintopScope stage micrometer**, ~$16 | 0.01 mm × 100 and 0.1 mm × 10 rulings, plus 0.07 and 0.15 mm dots. The dots validate the diameter-measuring procedure on a known circle, and measure tilt angle when the coupon is inclined. |
| **VEVOR 2L ultrasonic**, 60 W, 40 kHz, ~$50 | Chosen over a 120 W / 3 L unit **because it turns down.** Erosion, not insufficiency, is the failure mode. Digital timer for cycle repeatability; 40 kHz avoids the harsher low-frequency cavitation. |
| **Borosilicate beaker set**, 10–1000 mL, ~$17 | The 50 and 100 mL sizes take a 25 mm coupon. Fresh solution per coupon, suspended in the tank. |
| **Scale, 0.001 g × ≥50 g** | Not a 20 g reloading scale — a 40 mm brass blank is 32 g. |
| Mitutoyo 513-402-10E | Already owned. Cross-check only. |

Total for the instruments is roughly $300, against the ~$130 floor in the table above. The
difference is almost entirely the 4K microscope, which is the one place extra spending buys
measurable capability rather than convenience.

**About $130 of essentials gets a contributor to a real measurement** — scale, slide, the
cheapest qualifying scope, and stock. That is the number to put in front of anyone who thinks
this needs a metrology lab. The DTI is the line most safely skipped: the most expensive of the
instruments and the least capable, and anyone who already owns one should treat it as the
cross-check rather than as a reason not to buy the scale.

### 5.7 The procedure, end to end

Everything above is reasoning. This is the protocol, in order, so a contributor does not have
to reassemble it from prose. **Steps marked ◆ are controls — skipping them produces numbers
that look exactly like results.**

#### Once, before any measurement

1. **◆ Calibrate the scale** with its weight. Set aside a check mass — any stable object of
   roughly coupon weight — and keep it beside the scale.
2. **◆ Establish the noise floor.** Weigh one coupon five times, lifting it off the pan
   between each. The spread is your resolution, whatever the display claims. Every later
   result is quoted against this number.
3. **◆ Blank-coupon control.** Weigh an unengraved coupon, run the complete cleaning and
   drying cycle, weigh again. **The mass must not move.** If it drops, the process is eroding
   substrate — reduce ultrasonic power or shorten the cycle and repeat until it holds.
4. **◆ Qualify the microscope** against the four-point acceptance test in §5.3 — µm/px at
   maximum zoom, centre-versus-edge scale agreement, full-resolution capture path, stand
   repeatability. Do this inside the return window.
5. **Establish the capture path.** Determine whether USB gives full sensor resolution or a
   downscaled stream. If downscaled, use card stills or HDMI capture instead, and use that
   path for everything thereafter.

#### Preparing coupons

6. **Cut N coupons from one plate** — 25 × 25 × 3 mm, one per power level. Common plate means
   common alloy, temper and surface finish.
7. **Deburr and degrease.** Any loose edge material will leave during cleaning and read as
   depth.
8. **Label by scribed mark or edge notch — never marker pen.** Ink is 0.1–1 mg of mass, and
   detergent plus ultrasound will remove an unpredictable fraction of it between the two
   weighings. A scribe removes its mass once, before weighing, and then stays put.
9. **Weigh each coupon** and record as `m_before`. Tweezers or gloves from here on.

#### Engraving

10. **One power level per coupon**, a single 15 × 15 mm zone, centred with margin.
11. **Record every setting**: power %, speed, pass count, line interval, frequency, pulse
    width, focus offset, assist gas, ambient. A depth without its settings is not a
    measurement of anything.
12. Note that without a power meter the result is a threshold **in commanded power**, not in
    J/cm². That is still a usable model; converting to fluence needs the power-versus-command
    curve as a separate calibration.

#### Cleaning and re-weighing

13. **Degas** the filled tank for a few minutes before loading anything.
14. **Beaker method**: water plus two drops of plain dish detergent in a 50–100 mL beaker,
    coupon inside, beaker suspended in the tank rather than resting on its floor. Fresh
    solution per coupon.
15. **Fixed cycle time, identical for every coupon.** Three minutes is ample. Heater off.
16. **Rinse** in clean water. If a solvent rinse is wanted, isopropyl in a *separate* beaker —
    never in the tank.
17. **Dry thoroughly**, then **let it reach room temperature.** A warm coupon reads light,
    because convection lifts it off the pan. Fifteen minutes minimum.
18. **Weigh** and record as `m_after`.
19. **◆ Re-weigh the check mass** at the end of every session. Cheap scales drift with
    temperature, and drift that develops mid-session looks exactly like a depth measurement.

#### Computing

20. `depth = (m_before − m_after) / (ρ · area)`. Brass C360 ρ = 8.50 g/cm³; stainless 304
    ρ = 8.00; aluminium 2.70; copper 8.96.
21. Plot depth against `ln(fluence)` — or against `ln(commanded power)` if no meter — and fit
    `d = δ·ln(F/F_th)`. Slope gives `δ`, x-intercept gives `F_th`.
22. **Record where the fit breaks.** The log law fails as the pocket deepens. The depth beyond
    which it is not trusted is a result, not a failure.
23. Quote every constant with its R², pulse duration, source and lens. A number without those
    four qualifiers is not a result.

#### Cross-checks, at least once per material

24. **Tilt one coupon** on a wedge and measure a step laterally (§5.3). Independent geometry,
    independent failure modes. **A 3D-printed wedge is fine** — 30° is a good compromise
    between projection gain and occlusion. Print the sample face solid and flat, and do not
    measure or trust the printed angle: tilt the calibration slide alongside the coupon and
    read the angle off the ellipse the 0.15 mm dot images as. The wedge only has to be rigid.
25. **Dial indicator** on the same coupon, if one is to hand. Expect disagreement with mass
    loss — mass gives net material removed, a profile gives pocket geometry, and the
    difference between them is recast. That difference is informative.
26. **Cross-section** one coupon per material (§5.5) to validate the cheap methods against the
    referee.

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
