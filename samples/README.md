# Sample depth maps

Eight files. **One picture.**

Every image in this folder is the same coin-style relief — a rim, a bead ring, a central
cabochon and three chamfered bars. Open them side by side and they look identical, because
they are identical to the eye. What differs is how the depth was encoded, and that is the
difference between a relief that comes off the machine looking like the render and one
that comes off terraced, shallow, or flat.

Drag them onto DepthView in order and watch the verdict change while the picture does not.
That is the entire argument for the program in one folder.

| File | DepthView says | Why it is here |
|---|---|---|
| `01-genuine-16bit.png` | **OK** — 45,664 levels, step 1 | The reference. A real 16-bit map: smooth everywhere, full 0–65535 range, no headroom wasted. This is what the other seven are being compared against. |
| `02-imposter-x257.png` | **FAIL** — 255 levels, step 257 | The classic imposter. An 8-bit map widened to 16 bits by replicating the byte (`v → v×257`). The header says 16-bit and the file is twice the size; the depth information is unchanged. |
| `03-imposter-high-byte.png` | **FAIL** — 255 levels, step 256 | The other widening: `v → v×256`, low byte left at zero. Switch **Preview mode → Low byte only** on this one and on `01` — a genuine map shows detail there, this shows flat black. |
| `04-quantised-1024.png` | **WARN** — 1,014 levels, step 64 | The subtle one, and the one people miss. A 10-bit ladder stretched across a 16-bit range. It is genuinely better than 8-bit, and still far short of what it claims. |
| `05-grey-stored-as-rgb.png` | **OK**, with a finding | Neutral grey in three identical channels. Three times the data for no extra information — and **LightBurn reads a 24-bit image as 8-bit**, so this wrapper can silently cost precision as well as bytes. |
| `06-honest-8bit.png` | **OK** — 255 levels, 8-bit | Not a fault. An 8-bit file that says it is 8-bit. Worth loading right after `02` to see that the numbers are identical: the imposter was never more than this. |
| `07-wasted-headroom.png` | **OK**, with a warning | Genuine 16-bit, but the data sits in the middle third of the range. At 100 passes, roughly two-thirds of them do nothing. **Unused headroom** in the report is what you would reclaim by remapping. |
| `08-colour-contaminated.png` | **OK**, 897 non-grey px | 897 pixels out of 810,000 are not neutral — the residue of a JPEG round trip or a stray brush. Invisible on screen. Switch **Preview mode → Colour mask** and they light up red. |

## A suggested five minutes

1. Load `01-genuine-16bit.png`. Read the verdict, then open the **3D preview** and look at
   the dome. Set **Quantise to steps** to 256 and then to 32, and watch where the contours
   land — that is terracing, before it costs you a workpiece.
2. Load `02-imposter-x257.png`. Same picture. Different verdict. Look at the green comb
   strip under the histogram: the evenly spaced teeth are the signature.
3. Load `03-imposter-high-byte.png` and switch the preview to **Low byte only**. Flat black.
   Do the same to `01` and the relief is still there.
4. Load `07-wasted-headroom.png` and read **Unused headroom**. That number is passes you
   are paying for and not using.
5. Load `08-colour-contaminated.png` and switch to **Colour mask**.

## Where these came from

`make_samples.py` generates all eight from one computed height field. It needs `numpy` and
nothing else — the PNG writer is hand rolled, so a sample cannot inherit a bug from the same
kind of imaging library DepthView exists to distrust. That is also why the level counts in
the table above can be quoted as ground truth: they are what the generator put in, not what
DepthView reported back.

```
python samples/make_samples.py
DepthView --report samples --summary
```

Regenerating is deterministic and will reproduce these files byte for byte.

## These are synthetic

They are built to isolate one property each, which makes them good for learning what the
findings mean and poor for judging how DepthView behaves on real work. Real depth maps are
messier: a photographic background, an AI upscaler's fingerprints, a signature in a corner,
a mask edge that is not quite clean. If you have a real map that DepthView reports something
surprising about, that is worth an issue.
