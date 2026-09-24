# Anchor on stainless steel, calibrate everything else

The literature supports a working engraving simulator for only one of the sixteen laser/material pairs, and even that one is incomplete. That pair is a **MOPA fibre laser (1064 nm, long pulses) on stainless steel**. It has measured, settings-complete removal data. These data converge on a specific energy of **about 370–450 J per mm³ removed (η ≈ 2.2–2.7 × 10⁻³ mm³/J)** at well-tuned long-pulse settings, and **about 1,300–1,500 J/mm³ at low-pulse-energy "quality" settings** ([Trotec](https://www.troteclaser.com/en-us/helpcenter/materials/material-usage-hints/deep-engraving-metal); [JMMP 2020](https://www.mdpi.com/2504-4494/4/4/110)). Brass, copper and aluminium on the same laser have **no credible depth-per-pass or removal-rate value with stated settings**. The widely repeated "brass ≈ 32 µm/pass on a Raycus 100 W" figure could not be traced to any document. Published nanosecond ablation constants split into two regimes that differ **about 10× in threshold (0.5–0.7 vs 4–5.5 J/cm²) and about 400× in the log-law slope (6 nm vs 2.4–2.8 µm)** ([Neuenschwander et al.](https://www.academia.edu/122095750/From_fs_ns_Influence_of_the_pulse_duration_onto_the_material_removal_rate_and_machining_quality_for_metals); [Vladoiu et al.](https://joam.inoe.ro/articles/the-dependence-of-the-ablation-rate-of-metals-on-nanosecond-laser-fluence-and-wavelength/fulltext)). Neither regime reproduces measured MOPA behaviour. The simulator should therefore predict depth from **area energy dose × an empirical removal efficiency**, not from per-pulse log laws. The **blue diode and CO2 lasers should be modelled as "no depth expected"** on bare brass, copper and aluminium. On 304 they should be modelled as "colour/oxide mark, ~0 depth", because cold absorptivity is 1–3 % at 10.6 µm and copper's conductivity overwhelms a 10–40 W blue spot. **UV 355 nm can remove metal, but no bulk-metal constants exist at all.** Defect flags have a handful of 316L-derived numeric onsets and otherwise rest on qualitative evidence. Every prior below is therefore a placeholder that a structured calibration sequence (Section 7) must replace with the user's own grade-A measurements.

**Grading used in every table.** A = the user's own measurement (reserved; nothing in this report is A). B = peer-reviewed paper or authoritative handbook. C = manufacturer note with a measured result. D = practitioner report with stated settings and a measurement. E = unsourced, marketing, or settings without a measurement. "X-derived" = my arithmetic on inputs whose weakest grade is X; a derived value never outranks its weakest input. "NONE" = the literature reviewed cannot support a number.

## Two of four lasers cannot cut depth into bare coin metal

The capability question matters most, because it decides whether the simulator predicts depth at all. Absorptivity sets it in the first instant, and thermal conduction sets it after that. Room-temperature absorptivity (A = 1 − R, computed from primary n,k data) shows why only short-wavelength or high-intensity pulsed sources couple usefully into these metals. **Copper absorbs 0.53–0.66 at 355 nm and 0.39–0.52 at 455 nm, but only 0.007–0.043 at 1064 nm and 0.006–0.022 at 10.6 µm** ([Johnson & Christy](https://github.com/polyanskiy/refractiveindex.info-database/blob/main/database/data/main/Cu/nk/Johnson.yml); [Querry bulk ingot](https://github.com/polyanskiy/refractiveindex.info-database/blob/main/database/data/main/Cu/nk/Querry.yml); [Babar & Weaver](https://github.com/polyanskiy/refractiveindex.info-database/blob/main/database/data/main/Cu/nk/Babar.yml)). **Cu70Zn30 brass absorbs 0.63 / 0.55 / 0.052 / 0.022** at the four wavelengths ([Querry Cu70Zn30](https://github.com/polyanskiy/refractiveindex.info-database/blob/main/database/data/other/alloys/Cu-Zn/nk/Querry-Cu70Zn30.yml)). **Aluminium stays reflective everywhere: 0.074–0.081 in UV and blue, 0.037–0.053 at 1064 nm, and about 0.01 at 10.6 µm** ([Rakić](https://github.com/polyanskiy/refractiveindex.info-database/blob/main/database/data/main/Al/nk/Rakic.yml)). **Austenitic stainless (18.5Cr/9.3Ni, the closest measured composition to 304) absorbs 0.47 / 0.40 / 0.30** at 355, 455 and 1064 nm. It has no 10.6 µm data. Iron, used as a proxy, gives 0.022–0.031 at 10.6 µm ([Karlsson & Ribbing](https://github.com/polyanskiy/refractiveindex.info-database/blob/main/database/data/other/alloys/stainless%20steel/nk/Karlsson-austenitic.yml); [Ordal Fe](https://github.com/polyanskiy/refractiveindex.info-database/blob/main/database/data/main/Fe/nk/Ordal.yml)).

High absorptivity is not enough if conduction drains the heat. A first-order steady-state estimate of centre temperature rise under a stationary disc source is ΔT ≈ A·P/(π·a·k). I applied it with a ≈ 45 µm for the blue diode's roughly 0.08 × 0.1 mm spot, using sourced conductivities of 391 W/m·K for C11000 ([CDA](https://alloys.copper.org/alloy/C11000)), 121 for C26000 ([CDA](https://alloys.copper.org/alloy/C26000)), 167 for 6061-T6 ([AA data](https://tayloredge.com/reference/Circuits/0123heater/6061T6.pdf)) and 12.97 for 304 ([Kim, ANL-75-55](https://www.osti.gov/servlets/purl/4152287)). The results:

| Metal, blue diode (ΔT, stationary) | 10 W | 40 W | Needed to melt |
|---|---|---|---|
| Copper | about 80 K | about 330 K | about 1,060 K |
| Aluminium | about 35 K | about 135 K | about 630 K |
| Brass | about 320 K | about 1,290 K | about 900 K |
| 304 | about 2,200 K | far above melting | about 1,400 K |

Brass at 40 W exceeds its melting rise only when the beam is stationary; scanning cuts the rise further. This matches the evidence. A diode vendor states its heads "cannot engrave bare aluminum, copper, or brass" ([Opt Lasers](https://optlasers.com/metal-laser-marking-versus-coloring-versus-engraving-including-stainless-steel-and-carbon-steel-and-titanium)). A practitioner reports that a 10 W diode "will not touch brass" ([LightBurn forum](https://forum.lightburnsoftware.com/t/settings-required-for-brass-on-10-w-ortur-diode-lazer/138947)). The kilowatt blue-copper literature describes conduction welding at 131.7 W over a 768 µm spot, not removal ([Hummel et al., Fraunhofer ILT](https://sciencedirect.com/science/article/pii/S2666330920300108)), and blue copper welding "rarely" reaches vaporisation ([OPN](https://www.optica-opn.org/home/articles/volume_31/october_2020/features/high-powered_diode_lasers%E2%80%94new_bright_and_blue/)). The vendor's claim that "30 W blue lasers can engrave copper" comes with no depth, speed or spot. It stays grade E.

CO2 is worse still. At 100 W on a 75 µm radius spot, the same estimate gives about 8 K for copper, 30 K for aluminium and 77 K for brass. For 304 it gives about 800–1,000 K, which is enough for heat tint but short of melting except when the beam is stationary at 150 W. Epilog states that "bare metals reflect the wavelength of a CO2 laser". It also states that marking sprays bond a layer that "will not actually remove any of the metal", leaving "a raised mark" ([Epilog](https://www.epiloglaser.com/how-it-works/applications/co2-metal-marking-spray/)). A CO2 job on bare metal also carries an optics-damage risk: one user burned "a $20 lens … almost through" marking uncoated stainless ([LightBurn forum](https://forum.lightburnsoftware.com/t/reflection-can-burn-the-laser-diode/151082)).

UV 355 nm is the opposite case. It is physically capable of removal, but the literature gives no numbers to model it with. **No bulk-metal F_th, δ, incubation coefficient or removal rate at 355 nm ns exists in the accessible literature for any of the four metals.** The closest data are thin films ([Bozsóki et al.](https://www.sciencedirect.com/science/article/abs/pii/S0030399211000624)) and 532/1064 nm results. Those show Cu's threshold rising from 5.5 J/cm² at 1064 nm to 8.0 J/cm² at 532 nm, the opposite of what absorptivity alone predicts, probably because the spot sizes differed about 5× ([Vladoiu et al.](https://joam.inoe.ro/articles/the-dependence-of-the-ablation-rate-of-metals-on-nanosecond-laser-fluence-and-wavelength/fulltext)). That is a warning against filling 355 nm cells by wavelength interpolation.

An energy balance still bounds UV removal. Heating to boiling plus latent heats costs about **53 J/mm³ for Cu, 36 J/mm³ for Al and 75 J/mm³ for 304**. These are my figures from the sourced ρ, c_p and T values above, plus [Kim's](https://www.osti.gov/servlets/purl/4152287) 304 latent heats and grade-D element latent heats ([nuclear-power.com Cu](https://www.nuclear-power.com/copper-specific-heat-latent-heat-vaporization-fusion/), [Al](https://www.nuclear-power.com/aluminum-specific-heat-latent-heat-vaporization-fusion/)). A 6 W UV source therefore cannot exceed roughly 5–10 mm³/min even with perfect coupling. The fibre data below reach about 19 % of the equivalent ceiling on stainless. Applying 1–20 % of the ceiling gives a UV placeholder of roughly **0.05–1.3 mm³/min**. At that rate, a 0.1 mm mean relief across a 38 mm face (about 113 mm³) takes hours. The UV source's value is its 6–8 µm spot, not bulk removal.

### Table F — capability matrix (drives the "no depth expected" branch)

| laser | material | depth_model | expected_visible_outcome | grade | key evidence |
|---|---|---|---|---|---|
| mopa_1064 | ss304 | energy_dose × η (calibrated) | removal; tint/blackening at high dose | C/B | [Trotec](https://www.troteclaser.com/en-us/helpcenter/materials/material-usage-hints/deep-engraving-metal), [JMMP 2020](https://www.mdpi.com/2504-4494/4/4/110) |
| mopa_1064 | brass | energy_dose × η (UNCALIBRATED) | removal; Zn loss; crosshatch floor | B (qualitative) | [Hribar 2022](https://www.mdpi.com/2079-4991/12/2/232) |
| mopa_1064 | copper | energy_dose × η (UNCALIBRATED) | removal, low first-pass coupling; back-reflection warning | NONE for rate | [Matterna 2019](https://www.wlt.de/lim/Proceedings2019/data/PDF/Contribution_317_final.pdf) |
| mopa_1064 | aluminium | energy_dose × η (UNCALIBRATED) | removal; rough floor | D (depth only) | [LightBurn 6061](https://forum.lightburnsoftware.com/t/deep-engraving-settings/106751) |
| uv_355 | all four | energy_dose × η (UNCALIBRATED; ceiling-bounded) | slow removal, fine detail | NONE for rate | [Bozsóki 2011](https://www.sciencedirect.com/science/article/abs/pii/S0030399211000624) |
| diode_455 | copper, aluminium | no_depth | none (Al); darkening/oxide (Cu) | E + derived | [Opt Lasers](https://optlasers.com/metal-laser-marking-versus-coloring-versus-engraving-including-stainless-steel-and-carbon-steel-and-titanium) |
| diode_455 | brass | no_depth | tarnish; possible local melt only if near-stationary at ≥40 W | D + derived | [LightBurn forum](https://forum.lightburnsoftware.com/t/settings-required-for-brass-on-10-w-ortur-diode-lazer/138947) |
| diode_455 | ss304 | no_depth (oxide mark) | colour/anneal mark; shallow melt at very slow speed | E + derived | [Opt Lasers](https://optlasers.com/metal-laser-marking-versus-coloring-versus-engraving-including-stainless-steel-and-carbon-steel-and-titanium) |
| co2_10600 | brass, copper, aluminium | no_depth | none; spray = additive raised mark | C | [Epilog](https://www.epiloglaser.com/how-it-works/applications/co2-metal-marking-spray/) |
| co2_10600 | ss304 | no_depth | possible heat tint; spray = additive | C + derived | [Epilog](https://www.epiloglaser.com/how-it-works/applications/co2-metal-marking-spray/), [Kwon 2012](https://www.sciencedirect.com/science/article/abs/pii/S0143816611002727) |

Any diode or CO2 job on polished Cu, brass or Al should also carry a back-reflection warning. High-reflectivity metals are a documented damage risk for fibre sources too ([nLIGHT via Laser Focus World](https://www.laserfocusworld.com/industrial-laser-solutions/article/14216489/fiber-laser-allows-processing-of-highly-reflective-materials)).

## Energy dose, not per-pulse log laws, should drive fibre depth

The MakeIt/LightBurn inputs reduce to a small set of physical quantities with standard definitions. The papers that supply the onset data use the same definitions ([JMMP 2020](https://www.mdpi.com/2504-4494/4/4/110); [Hribar 2022](https://www.mdpi.com/2079-4991/12/2/232)). **Area energy dose per pass, E_DA = P/(v·h), is the single most useful quantity.** Depth per pass then follows as **d_pass = η · E_DA**, where η is the volumetric removal efficiency in mm³/J.

This works on the best-documented case. Trotec's 20 W stainless recipe (250 mm/s, 0.03 mm hatch) gives E_DA = 2.67 J/mm². With η = 2.2–2.7 × 10⁻³ mm³/J that predicts 5.9–7.1 µm/pass, against the 6.3–7.5 µm/pass implied by 200–240 µm in 32 passes ([Trotec](https://www.troteclaser.com/en-us/helpcenter/materials/material-usage-hints/deep-engraving-metal)). The 108 W SPI study's 4.2 J/mm² engraving dose at 13 mm³/min implies about 8–9 µm/pass ([JMMP 2020](https://www.mdpi.com/2504-4494/4/4/110)). Two machines 5× apart in power give nearly the same η. That is the strongest physics anchor in the whole evidence base.

The per-pulse alternative fails in two ways. The two published ns constant sets disagree by an order of magnitude or more. Vladoiu et al. fit **Δh = 2.8 ln F − 3.8 (Al) and 2.4 ln F − 4.2 (Cu), µm/pulse with F in J/cm²**, at 1064 nm, 4.5 ns and 10 Hz with a roughly 1.1 mm spot. That gives F_th ≈ 4 and 5.5 J/cm² ([JOAM](https://joam.inoe.ro/articles/the-dependence-of-the-ablation-rate-of-metals-on-nanosecond-laser-fluence-and-wavelength/fulltext)). The Bern group, at 4 ns and 50 kHz with an 18 µm spot, fits **δ = 5.92 nm and φ_th = 0.51 J/cm² for Cu, and δ = 5.98 nm and φ_th = 0.69 J/cm² for 1.4301 (= 304)** ([Neuenschwander et al.](https://www.academia.edu/122095750/From_fs_ns_Influence_of_the_pulse_duration_onto_the_material_removal_rate_and_machining_quality_for_metals)). A 100 W MOPA putting 1 mJ into w₀ = 20 µm reaches F₀ = 2E/(πw₀²) ≈ 160 J/cm². That is 30–40× above the Bern optimum (e²·φ_th ≈ 4–5 J/cm²) and well beyond Vladoiu's fitted 5–30 J/cm² range, so both sets would be extrapolated.

Neither set reproduces what MOPA users see. Eiselen et al. measured trench depths of **2.3, 3.3 and 4.2 µm at 10, 20 and 30 ns** at a constant 6.3 J/cm² ([Eiselen et al.](https://www.sciencedirect.com/science/article/pii/S1875389213001478)). A fluence-only law with τ-independent constants cannot produce that trend. Trotec's quality recipe cut pulse energy from 0.33 to 0.11 mJ and lost about 4× in η. Both log laws predict η should rise or stay flat as pulse energy falls toward the optimum (my arithmetic; the spot size is assumed at w₀ ≈ 20 µm).

The evidence therefore points to a hybrid. Measured MOPA efficiency (about 0.15 mm³/min/W) sits about 10× above the Bern 4 ns optimum (0.019 mm³/min/W for Cu, 0.014 for 1.4301). Their log-law formula η_max = 2δ/(e²·φ_th) reproduces those Bern values exactly, which confirms the nm units. The measured efficiency sits well below my extrapolated high-fluence Vladoiu optimum (about 0.67 mm³/min/W). Long-pulse MOPA removal behaves like melt ejection with a steep pulse-energy dependence. Its efficiency must be measured, not derived from threshold constants. I recommend using the low-fluence Bern thresholds only as an "any removal at all" gate. Below roughly F₀ < φ_th, output marking only. The Bern values are already multi-pulse at 50 kHz, so an incubation factor must not be applied on top of them.

### Table S — settings-to-physics relations (all standard definitions unless graded)

| quantity | formula (P in W, v in mm/s, h in mm, f in Hz) | UI source | grade / note |
|---|---|---|---|
| effective power | P = power% × P_rated | power slider | linearity of % vs output is **unverified**; calibrate (T1) |
| hatch | h = line interval, or 1 / line density | LightBurn "line interval"; MakeIt "line density" | definition |
| pulse energy | E_p = P / f | frequency | definition |
| characteristic frequency | f₀ = P_rated / E_max(τ) | source datasheet | MRR peaks at f ≈ f₀ or slightly above ([Hribar 2022](https://www.mdpi.com/2079-4991/12/2/232), B); E_max(τ) for JPT/WeCreat sources **not found** |
| spot diameter at focus | d₀ = 4·M²·λ·f_lens / (π·D_beam) | lens focal length | standard optics; anchor: 38 µm (1/e²) at f = 160 mm, M² ≤ 1.6 ([JMMP 2020](https://www.mdpi.com/2504-4494/4/4/110)); 57 vs 38 µm at 5 vs 7.5 mm beam ([Hribar 2022](https://www.mdpi.com/2079-4991/12/2/232)) |
| Rayleigh range | z_R = π·w₀² / (M²·λ) | lens + source | fibre w₀ 15–30 µm gives about 0.4–2 mm; UV w₀ 3–4 µm gives about 0.08–0.11 mm (derived, M² ≈ 1.3 assumed) |
| beam radius at defocus z | w(z) = w₀·√(1 + (z/z_R)²) | focus offset, Z-step | standard optics |
| peak fluence | F₀ = 2·E_p / (π·w²) | derived | definition |
| pulse overlap | OL_p = 1 − v / (f·d) | speed, frequency | definition used in [Hribar 2022](https://www.mdpi.com/2079-4991/12/2/232) |
| line overlap | OL_l = 1 − h / d | hatch | definition |
| area energy dose per pass | E_DA = P / (v·h) [J/mm²] | power, speed, hatch | onset quantity in [JMMP 2020](https://www.mdpi.com/2504-4494/4/4/110) |
| line energy dose | E_DL = P / v [J/mm] | power, speed | same source |
| depth per pass | d_pass = η(material, laser, τ, f/f₀, OL) · E_DA | all | model form; η from Table R |
| crosshatch | each hatch direction counts as one pass of E_DA | crosshatch toggle | definition; angle rotation adds MRR (Table R) |
| inter-pulse diffusion length | L = √(4κ / f) | frequency | about 11 µm (304), 39 µm (brass), 53 µm (6061), 68 µm (Cu) at 100 kHz (derived from sourced D) |
| thermal diffusion length per pulse | l_th = 2·√(D·τ) | pulse width | Table M |
| vaporisation energy ceiling | η_max = 1 / [ρ(c_pΔT + L_m + L_v)] | material | about 0.013 (304), 0.019 (Cu), 0.027 (Al) mm³/J (derived) |

## Stainless 304 has the only credible removal priors

Pulse width, frequency, overlap and scan strategy all move η in consistent directions across peer-reviewed studies. That makes them usable as multipliers even where the absolute level is not known.

**Longer pulses remove more at a fixed dose.** Evidence on 304 across 4–200 ns ([Manninen et al.](https://link.springer.com/article/10.1007/s11663-015-0415-x)), on 316L where 280–500 ns gave 8–16 mm³/min but 60–150 ns gave less ([JMMP 2020](https://www.mdpi.com/2504-4494/4/4/110)), and on brass and 316L across 70–240 ns ([Hribar 2022](https://www.mdpi.com/2079-4991/12/2/232)) agrees on this. The same studies also agree that longer pulses cost melt and quality. Eiselen's 10–30 ns series implies depth ∝ τ^0.55. That exponent is the only quantitative pulse-width scaling available, and it is untested beyond 30 ns.

**MRR peaks at f ≈ f₀.** f₀ is where the source delivers both full pulse energy and full average power, and this holds for brass, 316L and aluminium ([Hribar 2022](https://www.mdpi.com/2079-4991/12/2/232); [Williams et al.](https://link.springer.com/article/10.1007/s00170-014-6038-6)). At fixed τ, Trotec's 60 kHz setting removed about 3.3× faster per unit time than 180 kHz ([Trotec](https://www.troteclaser.com/en-us/helpcenter/materials/material-usage-hints/deep-engraving-metal)).

**Overlap has an interior optimum.** Maximum MRR occurred at **25 % pulse overlap for steel and 37.5 % for brass**. The best MRR-to-roughness trade-off was at about 50 % for both ([Hribar 2022](https://www.mdpi.com/2079-4991/12/2/232)).

**Interlaced, angle-rotated scanning adds about 20 % MRR and cuts Sa by 84 % on 316L** ([JMMP 2020](https://www.mdpi.com/2504-4494/4/4/110)). TRUMPF reports that interlacing raised MRR on aluminium and brass at 100 W but not on stainless, where it improved only roughness ([Laser Focus World](https://www.laserfocusworld.com/industrial-laser-solutions/article/14221797/laser-engraving-plays-to-strengths-of-nanosecond-pulsed-fiber-lasers)). That conflicts with the JMMP result, so the multiplier is material-dependent and needs calibration.

**Power scaling is contested.** Raycus measured aluminium efficiency at 20:50:100:200 W = 1 : 3 : 9.5 : 25.5, which is superlinear with an exponent of about 1.4 ([Raycus](https://en.raycuslaser.com/view/1877.html)). Cloudray's 304 pair (88.8 vs 149.0 µm at 60 vs 100 W) is roughly linear ([Cloudray](https://www.cloudraylaser.com/blogs/machine-guide/what-is-the-difference-between-a-60w-and-100w-mopa-fiber-laser)).

**Depth per pass should default to constant with pass count.** No published depth-versus-passes curve exists for these metals. TRUMPF describes scanner multipass work as "self-limiting" below about 1 mm ([Laser Focus World](https://www.laserfocusworld.com/industrial-laser-solutions/article/14221958/pulsed-nanosecond-fiber-lasers-excel-as-versatile-cutting-tools)). The practitioner guidance to lower focus 0.05–0.1 mm every few layers is E-grade ([ComMarker](https://blog.commarker.com/archives/55878)). Physically, with fibre z_R of about 0.4–2 mm, fluence loss becomes plausible around 1 mm of depth without a Z-step. With UV z_R of about 0.1 mm it starts almost immediately. A defocus-decay term using w(z) is therefore the right structure. Its magnitude remains a calibration target.

### Table R — removal priors (per source, not averaged)

| param | laser | material | value | unit | conditions | grade | source |
|---|---|---|---|---|---|---|---|
| eta | mopa_1064 | ss (grade n/s) | 2.2–2.7e-3 (370–450 J/mm³) | mm³/J | 20 W, 200 ns, 60 kHz, 250 mm/s, h 0.03, 32 passes; up to 100 µm warp | C-derived | [Trotec](https://www.troteclaser.com/en-us/helpcenter/materials/material-usage-hints/deep-engraving-metal) |
| eta | mopa_1064 | ss (grade n/s) | 6.7–7.3e-4 (1,360–1,500 J/mm³) | mm³/J | 20 W, 200 ns, 180 kHz, 900 mm/s, h 0.03, 450 passes; no warp | C-derived | [Trotec](https://www.troteclaser.com/en-us/helpcenter/materials/material-usage-hints/deep-engraving-metal) |
| eta | mopa_1064 | 316L | 7.7e-4 – 2.5e-3 (best recipe 2.0e-3) | mm³/J | 108 W, 17–500 ns, 38 µm, h 13.3 µm, E_DA 2.7–5.3 J/mm²; 280–500 ns best | B-derived | [JMMP 2020](https://www.mdpi.com/2504-4494/4/4/110) |
| d_pass | mopa_1064 | ss | 6.3–7.5 / 0.44–0.49 | µm | the two Trotec recipes above | C-derived | [Trotec](https://www.troteclaser.com/en-us/helpcenter/materials/material-usage-hints/deep-engraving-metal) |
| d_pass | mopa_1064 | 304 | 5.9 (60 W) / 9.9 (100 W) | µm | 15 passes; f, τ, v, h not stated; implied 76 J/mm³ is suspicious | C (ratio only) | [Cloudray](https://www.cloudraylaser.com/blogs/machine-guide/what-is-the-difference-between-a-60w-and-100w-mopa-fiber-laser) |
| d_pass | mopa_1064 | ss | 10–30 | µm | 60 W, no settings, no measurement; exceeds every measured value | E (regraded from C) | [OMG Laser](https://omglaser.com/how-do-you-figure-out-depth-per-pass-for-relief-3d-engraving-lightburn-vs-ezcad/) |
| d_pass | mopa_1064 | ss | "a few tens of µm" | µm | no settings | E as a number | [Laser Focus World](https://www.laserfocusworld.com/industrial-laser-solutions/article/14221797/laser-engraving-plays-to-strengths-of-nanosecond-pulsed-fiber-lasers) |
| d_pass | mopa_1064 | brass | 32 (100 passes → 3.24 mm) | µm | Raycus RFL-100M; **original document not found** | E | NONE located ([Raycus page checked](https://en.raycuslaser.com/view/1877.html)) |
| d_pass | mopa_1064 | 6061 Al | about 8.5 (≈30 passes per 0.254 mm) | µm | 60 W JPT M7, 175 mm lens; v, h, f not all stated | D/E | [LightBurn forum](https://forum.lightburnsoftware.com/t/deep-engraving-settings/106751) |
| eta | mopa_1064 | brass | **NONE**; placeholder 0.15–1.0 × eta_ss | mm³/J | lower bound from cold-A scaling (A 0.052 vs 0.30); upper bound unconstrained | E-derived | [Querry Cu70Zn30](https://github.com/polyanskiy/refractiveindex.info-database/blob/main/database/data/other/alloys/Cu-Zn/nk/Querry-Cu70Zn30.yml) |
| eta | mopa_1064 | copper | **NONE**; placeholder 0.15–1.0 × eta_ss | mm³/J | cold A 0.03–0.045, but liquid about 0.14 and keyhole 0.4–0.7 raise coupling after the first pulse | E-derived | [Kaufmann 2024](https://sciencedirect.com/science/article/pii/S1526612524010697) |
| eta | mopa_1064 | aluminium | **NONE**; placeholder 0.25–1.0 × eta_ss | mm³/J | cold A 0.04–0.05; lowest vaporisation energy per volume (about 36 J/mm³) | E-derived | [Rakić Al](https://github.com/polyanskiy/refractiveindex.info-database/blob/main/database/data/main/Al/nk/Rakic.yml) |
| eta | uv_355 | all | **NONE**; placeholder 1–20 % of the vaporisation ceiling | mm³/J | ceiling about 0.013 (304) / 0.019 (Cu) / 0.027 (Al) mm³/J | E-derived | energy balance (Table M sources) |
| eta | diode_455, co2_10600 | all | 0 | mm³/J | see Table F | C/D/E + derived | Table F |
| mult_tau | mopa_1064 | ss (1.4404) | depth ∝ τ^0.55 (plausible range 0.3–0.9) | – | 10–30 ns, 6.3 J/cm², 22 µm spot; validity above 30 ns unknown | B-derived | [Eiselen 2013](https://www.sciencedirect.com/science/article/pii/S1875389213001478) |
| mult_prf | mopa_1064 | brass, 316L, Al | peak at f ≈ f₀ to slightly above; falls either side | – | shape only | B | [Hribar 2022](https://www.mdpi.com/2079-4991/12/2/232), [Williams 2014](https://link.springer.com/article/10.1007/s00170-014-6038-6) |
| overlap_opt_mrr | mopa_1064 | steel / brass | 25 % / 37.5 % | OL_p | 70–240 ns, 20–45 J/cm² | B | [Hribar 2022](https://www.mdpi.com/2079-4991/12/2/232) |
| mult_interlace | mopa_1064 | 316L | ×1.20 MRR, ×0.16 Sa | – | 0°/45°/18.43°/71.58° interlaced | B | [JMMP 2020](https://www.mdpi.com/2504-4494/4/4/110) |
| power_exponent | mopa_1064 | Al / 304 | 1.4 / about 1.0 | depth ∝ P^n | 20–200 W / 60–100 W; settings n/s | C | [Raycus](https://en.raycuslaser.com/view/1877.html), [Cloudray](https://www.cloudraylaser.com/blogs/machine-guide/what-is-the-difference-between-a-60w-and-100w-mopa-fiber-laser) |
| z_step | mopa_1064 | all | 0.05–0.1 mm every few layers; defocus −3 mm (Al, brass), −2 mm (SS) | mm | recipe advice, unmeasured | E | [ComMarker](https://blog.commarker.com/archives/55878) |

The E-grade brass coin recipes are useful only as UI starting points, not as depth priors. One is 60 W, 1000 mm/s, 80 %, 30 kHz, 200 ns, 0.04 mm, 400 layers. The other is 2000 mm/s, 95 %, 100 kHz, 200 ns, 0.025 mm, 256 layers, with a cleanup pass every 10 layers at 30 % and 80 kHz ([ComMarker](https://blog.commarker.com/archives/56496)). If brass behaved like stainless (η ≈ 2.5 × 10⁻³ mm³/J), the first recipe's 1.2 J/mm² per layer would give about 3 µm/layer and about 1.2 mm total. That is plausible coin-relief depth, but it is my arithmetic, not a measurement. A low-power counter-example: a 30 W source at 1000 mm/s for 128 passes produced only "surface etching" on brass coins ([LightBurn forum](https://forum.lightburnsoftware.com/t/deep-engraving-setting/181802)).

## Material constants are solid; ablation constants split tenfold

Thermophysical and optical properties are the best-supported inputs. Their grade-C/B values should go into the baseline file unchanged. Optical penetration depth is **6–29 nm** everywhere. Thermal diffusion length over 4–250 ns is **0.23–1.8 µm for 304 and up to 10.7 µm for copper**. Absorption is therefore always a surface heat source. 304's roughly 35× lower diffusivity than copper, combined with its roughly 10× higher 1064 nm absorptivity, explains why stainless engraves easily and copper does not.

Brass needs special handling. **Zinc boils at 907 °C** ([CRC via data page](https://en.wikipedia.org/wiki/Boiling_points_of_the_elements_(data_page))). That is essentially the C360 liquidus of 899 °C ([C36000 sheet](https://www.nationalbronze.com/pdfs/C36000.pdf)) and just below the C260 solidus of 916 °C ([CDA](https://alloys.copper.org/alloy/C26000)). Under 6 ns ablation, the Zn/Cu aerosol ratio fluctuated between 0.05 and 0.2, while femtosecond ablation held it constant at 0.11 ([Liu et al., LBNL](https://www.osti.gov/servlets/purl/842968)). A single "brass boiling point" is not physically meaningful. The simulator should treat brass removal as Zn-led and the residual surface as Cu-enriched.

### Table M — material constants

| param | ss304 | brass | copper | aluminium | unit | grade / source |
|---|---|---|---|---|---|---|
| density | 7.894 | 8.53 (C260) / 8.50 (C360) | 8.91 (C110) | 2.70 (6061) / 2.71 (1050A) | g/cm³ | B [Kim](https://www.osti.gov/servlets/purl/4152287); C [CDA C260](https://alloys.copper.org/alloy/C26000), C/D [C360](https://www.nationalbronze.com/pdfs/C36000.pdf); C [CDA C110](https://alloys.copper.org/alloy/C11000); B/C [6061](https://tayloredge.com/reference/Circuits/0123heater/6061T6.pdf), C [Aalco](https://www.aalco.co.uk/datasheets/Aluminium-Alloy-1050A-H14-Sheet_57.ashx) |
| c_p (RT) | 510 | 377 | 385 | 896 (6061) | J/kg·K | same |
| k (RT) | 12.97 | 121 (C260) / 116 (C360) | 391 | 167 (6061) / 222 (1050A) | W/m·K | same |
| D (RT) | 3.24e-6 | 3.77e-5 / 3.62e-5 | 1.14e-4 | 6.9e-5 / 9.1e-5 (mixed-source) | m²/s | same, derived |
| T_solidus–liquidus | 1427 °C (T_m) | 916–954 (C260); 888–899 (C360) | 1065–1083 | 582–652 (6061); 650 (1050A) | °C | same |
| T_boil | 3080 K | Zn 907 °C controls | 2562 °C | 2519 °C | – | B [Kim](https://www.osti.gov/servlets/purl/4152287); B [CRC via data page](https://en.wikipedia.org/wiki/Boiling_points_of_the_elements_(data_page)) |
| L_fusion | 268 | NONE (alloy); Zn 112 | 205 | 400 (pure Al) | kJ/kg | B Kim; D [nuclear-power.com](https://www.nuclear-power.com/copper-specific-heat-latent-heat-vaporization-fusion/) |
| L_vap | 7406 | NONE (alloy); Zn 1764 | 4726 | 10874 (pure Al) | kJ/kg | B Kim; D nuclear-power.com |
| E_vap per volume | about 75 | NONE | about 53 | about 36 | J/mm³ | derived (weakest input D) |
| A_355 | 0.47 | 0.63 | 0.53–0.66 | 0.074–0.081 | – | B via n,k ([RII sources](https://github.com/polyanskiy/refractiveindex.info-database/blob/main/database/data/main/Cu/nk/Johnson.yml)) |
| A_455 | 0.40 | 0.55 | 0.39–0.52 | 0.077–0.081 | – | B via n,k |
| A_1064 (cold) | 0.30 | 0.052 | 0.007–0.043 (use 0.03–0.045 for rolled sheet) | 0.037–0.053 | – | B via n,k; [Matterna 2019](https://www.wlt.de/lim/Proceedings2019/data/PDF/Contribution_317_final.pdf) |
| A_1064 (hot/liquid/keyhole) | NONE | NONE | about 0.05 (800 °C) / about 0.14 (liquid, 1030 nm) / 0.4–0.7 (keyhole) | NONE | – | B [Matterna](https://www.wlt.de/lim/Proceedings2019/data/PDF/Contribution_317_final.pdf), [Kaufmann](https://sciencedirect.com/science/article/pii/S1526612524010697) |
| A_10600 | 0.022–0.031 (Fe proxy) | 0.022 | 0.006–0.008 | 0.009–0.012 | – | B via n,k |
| l_alpha (1064) | 15.9 | 12.4 | 11.7–13.2 | 8.0–9.2 | nm | B-derived |
| l_th at 4 / 30 / 100 / 250 ns | 0.23 / 0.62 / 1.14 / 1.80 | 0.78 / 2.13 / 3.88 / 6.14 | 1.35 / 3.70 / 6.75 / 10.7 | 1.05 / 2.88 / 5.26 / 8.31 | µm | C-derived |

Copper's 1064 nm absorptivity varies 6× across grade-B sources, with smooth films at the bottom and a bulk ingot at the top. Oxide state can move it either way: one heat-cool cycle dropped a rolled sample from about 4.3 % to about 1.5 % ([Matterna & Ostendorf](https://www.wlt.de/lim/Proceedings2019/data/PDF/Contribution_317_final.pdf)). Coupling on copper over repeated passes is therefore not guaranteed to rise monotonically. REELS-derived constants ([Werner 2009](https://github.com/polyanskiy/refractiveindex.info-database/blob/main/database/data/main/Cu/nk/Werner.yml)) disagree with every optical source and should be excluded.

### Table B — ns ablation constants (for the removal-onset gate only)

| param | material | value | unit | conditions | grade | source |
|---|---|---|---|---|---|---|
| F_th_lowF | copper | 0.51 | J/cm² | 1064 nm, 4 ns, 50 kHz, w₀ 18 µm, multi-pulse | B | [Neuenschwander](https://www.academia.edu/122095750/From_fs_ns_Influence_of_the_pulse_duration_onto_the_material_removal_rate_and_machining_quality_for_metals) |
| delta_lowF | copper | 5.92 | nm | same | B | same |
| F_th_lowF | ss304 (1.4301) | 0.69 | J/cm² | same | B | same |
| delta_lowF | ss304 | 5.98 | nm | same | B | same |
| F_th_highF | copper / aluminium | 5.5 (fit gives 5.8) / 4.0 (fit 3.9); Al 3 in a separate study | J/cm² | 1064 nm, 4.5 ns, 10 Hz, 1.1 mm spot, 20-pulse average, log-fit extrapolation | B | [Vladoiu 2008](https://joam.inoe.ro/articles/the-dependence-of-the-ablation-rate-of-metals-on-nanosecond-laser-fluence-and-wavelength/fulltext); [Vladoiu 2007](https://www.academia.edu/88635879/The_Influence_of_Spot_Diameter_Fluence_and_Wavelength_of_the_Nanosecond_Laser_Pulses_on_the_Ablation_Rate_of_Aluminum) |
| delta_highF | copper / aluminium | 2.4 / 2.8 | µm | same; valid about 5–30 J/cm² | B | [Vladoiu 2008](https://joam.inoe.ro/articles/the-dependence-of-the-ablation-rate-of-metals-on-nanosecond-laser-fluence-and-wavelength/fulltext) |
| F_th, delta | brass, any regime | **NONE** | – | no ns 1064 nm value found | – | – |
| F_th, delta | aluminium, fibre / MOPA | **NONE** | – | – | – | – |
| F_th, delta | any metal at 10–500 ns | **NONE** | – | – | – | – |
| F_th, delta, S | any metal at 355 nm (bulk) | **NONE** | – | – | – | – |
| incubation S | any metal, ns | **NONE**; the "0.8–0.9" in circulation is ultrashort-derived | – | Vladoiu saw a constant rate over 20 pulses | B (negative) | [Vladoiu 2008](https://joam.inoe.ro/articles/the-dependence-of-the-ablation-rate-of-metals-on-nanosecond-laser-fluence-and-wavelength/fulltext) |
| tau_exponent (F_th ∝ τ^x) | any | **unverified**; x = 0.5 is theory; steels barely change from 520 ps to 4 ns | – | – | B (weak, against) | [Lauer 2014](https://www.sciencedirect.com/science/article/pii/S1875389214002612) |

The existing project values need three corrections. The **"δ = 1.1–2.8 µm" range mixes wavelengths**: at 1064 nm the values are 2.4 µm (Cu) and 2.8 µm (Al, Ti), and 1.1 µm is Ti at 532 nm. **"l_α = 6.7–14 nm" also mixes wavelengths.** **"l_th = 0.40–1.43 µm" is valid only at 4.5 ns** and must scale as √τ ([Vladoiu 2008](https://joam.inoe.ro/articles/the-dependence-of-the-ablation-rate-of-metals-on-nanosecond-laser-fluence-and-wavelength/fulltext)).

## Defect flags rest on a handful of 316L onset numbers

Only three setting-derived quantities have published ns onset values: **area dose (blackening above 5.3 J/mm²), repetition rate at a given pulse width (melt collapse), and overlap**. All three come from 316L or from mixed stainless/brass studies. 304 is close enough chemically that the 316L values are reasonable first-order proxies. For brass, copper and aluminium they are placeholders. Everything else is qualitative or absent.

**Heat tint on 304 is not a malfunction.** Nanosecond fibre lasers grow 300–800 nm interference oxides on 304 by varying speed, rep rate and pulse width ([Sci. Rep. 2017](https://www.nature.com/articles/s41598-017-07373-8)). Any low-speed, high-overlap final pass will therefore tint. The flag should key on the final pass, because deeper passes ablate earlier tint away.

**Heat accumulation is well modelled only for ultrashort pulses** ([Weber et al. 2014](https://opg.optica.org/oe/fulltext.cfm?uri=oe-22-9-11312&id=284367); [Bauer et al. 2015](https://opg.optica.org/oe/fulltext.cfm?uri=oe-23-2-1035&id=307810)). Its critical-speed concept can be used for ns only qualitatively. The inter-pulse diffusion length L = √(4κ/f) is about 11 µm for 304 but about 68 µm for copper at 100 kHz, so 304 accumulates heat locally far more readily at equal settings.

### Table D — defect flag rules

| flag | computable indicator | onset prior | scope | grade | source |
|---|---|---|---|---|---|
| blackening | E_DA per pass | > 5.3 J/mm² | 316L (proxy for 304); NONE for brass/Cu/Al | B | [JMMP 2020](https://www.mdpi.com/2504-4494/4/4/110) |
| heat_tint | final-pass speed, overlap, E_DA inside colour-marking window | oxide 300–800 nm; no numeric map accessible | 304 | B (qualitative) | [Sci. Rep. 2017](https://www.nature.com/articles/s41598-017-07373-8) |
| melt_collapse | f vs f_collapse(τ) | 350 kHz @ 280 ns; 300 @ 380 ns; 250 @ 500 ns (0.09 J/mm line dose) | 316L | B | [JMMP 2020](https://www.mdpi.com/2504-4494/4/4/110) |
| recast_cracks_pores | τ | ≥ 50 ns gave cracked, porous resolidified bottoms | 304, 20 W | B | [Manninen 2015](https://link.springer.com/article/10.1007/s11663-015-0415-x) |
| slag_accumulation | layers since last cleaning pass; E_DA vs about 4.2 J/mm² optimum | cleaning = shorter τ, higher f, lower P (e.g. 150 ns / 350 kHz / 3.5 J/mm²) | 316L (B); Al (D); brass (D/E) | B/C/D | [JMMP 2020](https://www.mdpi.com/2504-4494/4/4/110), [LFW TRUMPF](https://www.laserfocusworld.com/industrial-laser-solutions/article/14221797/laser-engraving-plays-to-strengths-of-nanosecond-pulsed-fiber-lasers), [LightBurn Al](https://forum.lightburnsoftware.com/t/deep-engraving-settings/106751) |
| roughness_high | OL_p, OL_l < about 50 %; sequential vs interlaced | Sa about 32 µm sequential, about 5 µm interlaced, 1.4 µm with clean + polish | 316L; overlap rule also brass | B | [JMMP 2020](https://www.mdpi.com/2504-4494/4/4/110), [Hribar 2022](https://www.mdpi.com/2079-4991/12/2/232) |
| roughness_min | OL_p | Ra about 2 µm at 64–79 % | Ti6Al4V only (not a coin metal) | B (off-material) | [Micromachines 2018](https://www.mdpi.com/2072-666X/9/7/324/htm) |
| visible_layering / crosshatch | fixed hatch angle across many layers; OL_l < 50 % | no numeric onset; mitigations: rotate 36° per layer, cleanup passes at smaller h, lower P, higher f | brass (D), steel (D), multi-metal (C) | C/D | [LightBurn brass](https://forum.lightburnsoftware.com/t/fiber-deep-engrave-cross-hatch-pattern-visible/158168), [LightBurn steel](https://forum.lightburnsoftware.com/t/deep-engraving-w-no-burr/99262), [LFW TRUMPF](https://www.laserfocusworld.com/industrial-laser-solutions/article/14221797/laser-engraving-plays-to-strengths-of-nanosecond-pulsed-fiber-lasers) |
| terracing | depth quantisation = d_pass per slice | geometric, not a defect threshold | all | definition | – |
| defocus_loss | cumulative depth − Z compensation > z_R | derived from w(z) | all | derived | standard optics |
| warp | per-pass dose at low f / high E_p on a small field | 100 µm warp at 0.33 mJ / 60 kHz; none at 0.11 mJ / 180 kHz | ss, 20 W, 5 × 5 mm | C (one pair) | [Trotec](https://www.troteclaser.com/en-us/helpcenter/materials/material-usage-hints/deep-engraving-metal) |
| plume_shielding | f, E_p | plasma 0.5–1.5 mm tall, rising with fluence; blocking lasts milliseconds | brass, 316L | B (qualitative) | [Hribar 2022](https://www.mdpi.com/2079-4991/12/2/232) |
| dezincification | melt-promoting settings (long τ, high E_DA) | **NONE** numeric | brass | B (qualitative) | [Liu et al.](https://www.osti.gov/servlets/purl/842968) |
| burr / rim | – | **NONE** | all | – | – |
| cone_structures | – | **NONE** for ns (all evidence is fs/ps) | all | – | – |
| back_reflection | polished Cu/Al/brass, first pass, normal incidence | **NONE** numeric | Cu, Al, brass | C/D | [nLIGHT](https://www.laserfocusworld.com/industrial-laser-solutions/article/14216489/fiber-laser-allows-processing-of-highly-reflective-materials), [LightBurn](https://forum.lightburnsoftware.com/t/reflection-can-burn-the-laser-diode/151082) |
| additive_not_removal | CO2 or diode with marking spray | raised mark, depth ≤ 0 | all | C | [Epilog](https://www.epiloglaser.com/how-it-works/applications/co2-metal-marking-spray/) |

Wall-angle error in ns layer-by-layer ablation averaged about 38–40 % (range 15–86 %) on Ti6Al4V ([Micromachines 2018](https://www.mdpi.com/2072-666X/9/7/324/htm)). Taper therefore matters for relief fidelity, but no law for coin metals exists.

## What the literature cannot support

The gaps are structural, not incidental. **There is no same-settings comparison of the four metals on any ns source**, so every material ratio is a calibration target. There are no JPT or WeCreat datasheets giving E_max(τ), so f₀ must come from the user's source documentation or from test T2 below. The full texts most likely to fill specific cells were inaccessible. These include Hendow & Shakir (MOPA pulse-width thresholds), Lutey 2013 and Semerok 1999 (ns metal thresholds), the absolute brass MRR tables in Hribar 2022, Williams 2014's aluminium MRR values, the Bergström as-received absorptance papers, and Kwon's 10.6 µm absorptances.

Several contradictions should be surfaced to the user, not silently resolved. The two ns threshold regimes differ 10×. Stainless depth per pass is 10–30 µm (E) against 6–10 µm (C/B measured). Power scaling is superlinear on aluminium but linear on 304. Interlacing adds MRR on 316L in one study but not on stainless in another. Cloudray's implied 76 J/mm³ is about 5× better than every other stainless figure. At 30 % absorptivity it would mean only about 23 J/mm³ absorbed, a third of the vaporisation energy. That would need near-perfect melt ejection, so the figure is treated as a power ratio only. The overlap optima (about 50 % vs 64–79 %) are not a true conflict: they optimise different objectives on different metals.

| item | status | consequence for the baseline file |
|---|---|---|
| η for brass, Cu, Al on MOPA | NONE | ship as wide placeholders flagged "uncalibrated"; hide absolute depth until T3/T6 run |
| brass 32 µm/pass (Raycus) | untraceable | grade E; do not use as a prior |
| any 355 nm bulk-metal constant | NONE | ceiling-bounded placeholder only |
| F_th at 10–500 ns; τ-scaling exponent | NONE / unverified | fit from T2/T3 |
| incubation S at ns | NONE | omit; Bern thresholds already multi-pulse |
| depth vs passes roll-off; Z-step effect | NONE (E advice only) | constant d_pass plus defocus term; fit from T4 |
| focus-offset effect on MRR | E only | compute via w(z); fit from T0/T4 |
| power% linearity (MakeIt, LightBurn) | NONE | assume linear; flag; T1 |
| burr, dezincification, warp, back-reflection thresholds | NONE numeric | qualitative flags only |
| hot or oxidised absorptivity for brass, 304, Al | NONE | cold values only |

## Seven guided tests replace the priors with grade-A data

The published studies use a two-stage design. Single-line groove screening finds the stable window, and a small factorial on 3 × 3 or 5 × 5 mm pockets then measures volume ([JMMP 2020](https://www.mdpi.com/2504-4494/4/4/110); [Hribar 2022](https://www.mdpi.com/2079-4991/12/2/232); [Williams 2014](https://link.springer.com/article/10.1007/s00170-014-6038-6)). A hobby version of that design needs one change. Depth must be large enough to measure with a dial indicator or depth micrometer rather than a profilometer. Each pocket should therefore aim for **at least 50 µm of depth**, so that ±5 µm instrument error stays below 10 %. At 5 × 5 mm, volume = 25 mm² × depth, which gives η directly as volume / (P × time). The practitioner method of burning a 10 × 10 mm patch and measuring it is the same idea, E-graded ([OMG Laser](https://omglaser.com/how-do-you-figure-out-depth-per-pass-for-relief-3d-engraving-lightburn-vs-ezcad/)). Every result should be stored as grade A with the full settings vector, lens, source model, material grade and surface state, and should overwrite the matching prior row.

| test | purpose | geometry and variables | measurement | parameters replaced |
|---|---|---|---|---|
| T0 focus and spot | find true focus, spot width d, z_R for the fitted lens | single lines on a ramp or at Z steps of 0.1 mm (fibre) or 0.02 mm (UV); fixed low power | line width vs Z under magnification | d₀, z_R, M²-effective; the defocus term |
| T1 power linearity | map power % to delivered effect | 5 × 5 mm pockets at 20/40/60/80/100 %, all else fixed, on 304 | depth | P(%) curve; power_exponent |
| T2 PRF and pulse-width screen | locate f₀ and the collapse limit | single grooves at the longest and two shorter τ; f swept from about 0.5 f₀ to 4 f₀ at fixed pulse pitch (about 25–50 % overlap); classify grooves as stable, shallow, burr or collapsed as in [JMMP 2020](https://www.mdpi.com/2504-4494/4/4/110) | groove depth and class | f₀, mult_prf, f_collapse(τ), mult_tau |
| T3 η factorial | measure η and its multipliers | Taguchi L9 or 3³ over τ × f × h on 5 × 5 mm pockets, 20–40 passes, 304 first | depth, time, Ra by feel or comparator | eta_ss, overlap_opt, mult_tau |
| T4 depth-vs-passes ladder | fill the roll-off gap | best T3 cell at 10/20/40/80/160 passes, with and without a 0.05 mm Z-step every 10 layers | depth per rung | d_pass(N), defocus decay, z_step value |
| T5 defect map | locate blackening, tint, slag and banding onsets | E_DA ladder 1–8 J/mm²; fixed-angle vs rotated (36°) vs interlaced hatch; cleanup pass every 0/5/10 layers | photos under fixed lighting; colour; floor texture | blackening onset, heat_tint window, mult_interlace, layering flag |
| T6 material transfer | derive brass, Cu and Al η | repeat the T3 best cell plus two neighbours on each metal and surface state (polished vs as-received) | depth | eta_brass, eta_cu, eta_al; material ratios |
| T7 null and UV checks | confirm "no depth" and bound UV | diode/CO2: 5 × 5 mm at the slowest practical speed, 10 passes, expect 0 µm; UV: T3 reduced to 3 cells, with T0 first because z_R is about 0.1 mm | depth, discolouration | confirms the Table F branch; eta_uv |

Run T0 before anything else. Every other result depends on spot size and on where the surface sits relative to focus. Run 304 before the other metals because it has literature to check against. If the user's 304 η lands far outside 0.7–2.7 × 10⁻³ mm³/J, suspect the power calibration or focus before the physics.

## Conclusion

The key insight is structural. Engraving physics in the MOPA regime is dominated by melt ejection, whose efficiency depends steeply on pulse energy and pulse width. The published single-pulse threshold literature was not built to capture that. A simulator built on F_th and δ would look rigorous and be wrong. One built on area energy dose times a measured efficiency looks crude, but it already reproduces the two best stainless datasets from machines 5× apart in power. The practical design follows from that. Ship stainless as a working prior, ship brass, copper, aluminium and UV as explicitly uncalibrated, and ship diode and CO2 on bare metal as a "no depth" branch with oxide and back-reflection warnings.

Calibration is not a refinement layer on top of this model; it is the model for most of its cells. Each user's grade-A data will also be more valuable than any source in this report, because no published study matches a hobby galvo's lens, source waveform and coin-blank geometry. If the simulator logs results in a shared schema with full settings, it could produce the first same-setting, four-metal ns comparison, which is the dataset the literature lacks.
