# Contributing to DepthView

Contributions are welcome — code, measurements, bug reports, or a file that DepthView reads
wrongly. This document covers what the project is strict about and why, because most of the
rules here exist for a reason that is not obvious from the outside.

DepthView is MIT licensed. You are free to use any part of it in your own work, including
commercial and closed-source work, with attribution. **If you are a vendor whose format or
software DepthView reads, you are especially welcome** — nobody knows your format better than
you do, and a correction from you outranks anything derived by inspection.

---

## Build and run

Requires the **.NET 8 SDK**. No other toolchain.

```
dotnet build                  # from the repository root
dotnet run --project src/DepthView
```

`publish.ps1` / `publish.sh` produce the self-contained single-file builds for the seven
supported runtime identifiers. **The platform list in `BuildInfo.Platforms` is duplicated
knowledge** — it must change in the same commit as any RID change in the publish scripts, or
the About box starts advertising builds that do not exist.

Stop any running `DepthView.exe` before building. A running instance locks the build output
and the build fails with MSB3027.

---

## The rules that are not negotiable

### 1. Never commit somebody else's depth map

`samples/` is an **allow-list** in `.gitignore`, not a deny-list: everything in it is ignored
except the generated files named explicitly. This is deliberate. `samples/` is the natural
place to drop a file somebody sent you to test, and `git add -A` would otherwise publish their
artwork under this project's MIT licence.

Adding a real sample means adding its name to `.gitignore` deliberately, after checking the
rights. Generated samples are fine — `--samples` writes a full set.

### 2. Run the privacy check before you push

```
./tests/privacy-check.ps1
```

It scans the staged diff for strings that must not reach a public repository. It exists
because a screenshot, a beta device name and a private build link have each nearly been
committed. **It cannot see inside images** — a product-page screenshot usually carries the
buyer's delivery address, which is why `docs/images/Screenshot*` is ignored outright.

### 3. Keep file claims and material claims apart

This is the one that is easy to violate by accident, and it has been violated by accident.

DepthView analyses **files**. It does not know what your laser will do. So "this map contains
88 distinct depths" is a file claim and is fine; "you are wasting 168 passes" is a material
claim dressed as a file claim, and is not. Words like *wasted*, *fixing* and *reclaiming*
smuggle in an assumption about the machine.

The tool **reports and does not prescribe**. A warning that fires because a change would
produce a different result — rather than because something is provably wrong — is a bug.

### 4. Every factual claim carries its source

Especially in documentation. A number in `docs/DEPTH-PREDICTION.md` is expected to say where
it came from and how well established it is. An authoritative-sounding figure is not a source,
and **an authoritative source can still be describing an old version** — check what release a
claim describes before rewriting anything around it.

### 5. Corrections are recorded, not deleted

When something here turns out to be wrong, the fix goes in **with a note saying what was wrong
and why**, rather than being quietly edited away. Several such notes are in the README and in
`CLAUDE.md`. They are some of the most useful text in the repository.

One refinement learned the hard way: **state the correct claim first and in full, then the
retraction underneath, short and clearly subordinate.** A correction that opens by quoting the
error gets read as the error.

---

## Bug reports

The most valuable report is **a file DepthView gets wrong** — but see rule 1. If the file is
not yours to share, a description of what it declares and what DepthView said about it is
usually enough to reproduce with a generated equivalent.

Include the DepthView version from the About box, your platform, and the exact output.

---

## Contributing a format reader

`src/DepthView/Integrations/` holds these:

- `Common/` — the format-neutral job model. `CutLayer` and `LaserJob` are what every reader
  produces, and **every field is nullable on purpose.**
- `LightBurn/LbrnProjectReader.cs` — the reference implementation, and the one to read first.
- `WeCreat/WwsProjectReader.cs` — recognises the container, does not parse it.

Two conventions matter more than the rest:

**Absent is not zero.** Project formats routinely omit a parameter sitting at its default. A
missing pass count is not a layer that runs zero passes. Readers return `null`, the report
prints `-`, and no figure is invented. A pass count is what every depth claim gets quoted
against, so a fabricated one is worse than no answer.

**Unmapped fields are preserved, not dropped.** `CutLayer.Vendor` is a dictionary that keeps
anything the common model has no home for, so information survives a round trip through a
reader that predates it.

Facts about a format belong in the reader's own comments, established by **opening a real
file** rather than from memory or documentation alone. Where documentation and the file
disagree, the file wins and the comment says so.

---

## A note on formats that are not documented

DepthView reads `.wws` far enough to recognise it and no further. The container opens with a
`WWS2` magic number and everything after it is opaque, so it is compressed, encrypted, or
both.

**DepthView will not attempt to defeat that**, and that decision is written into the reader
rather than merely promised here. Support depends on the vendor documenting the parts a
third-party tool needs. Pull requests that reverse-engineer a protected format will be
declined regardless of how well they work.
