# GD MemoryShield v1.0.0

Static memory audit for Unity projects. Scans **assets, scenes, and code** and
reports what will cost memory at runtime. It never runs the game, never talks
to Play, never reads device metrics — Play Console already shows runtime
numbers; this tool tells you *why* they are what they are, from inside the
project.

Three questions it answers:

1. **What is resident and why** — which assets, how big, held by what, and what
   got padded up on the way in.
2. **What is misconfigured** — import settings that inflate footprint for no gain.
3. **What will leak** — code patterns that keep things alive past their scene.

## Install

**Via UPM git URL (recommended)** — Package Manager → `+` → *Add package from
git URL...*:

```
https://github.com/CoreTeamOrganization/GD-MemoryShield.git
```

Pin a release with a tag: `...GD-MemoryShield.git#v1.0.0`. Requires git on the
machine and access to the CoreTeamOrganization repo.

**Or from disk** — clone the repo into your project's `Packages/` directory as
`com.gamedistrict.memoryshield` (or Package Manager → Add package from disk).

Unity 2021.3+. No dependencies.

## Use

**Tools → GD MemoryShield** → **Rescan**.

- **Left rail** — categories with live score pills.
- **Main pane** — findings for the selected category; select a row for the fix
  and a **Ping** button that jumps to the asset or opens the script at the line.
- **Filters** — severity chips and a path search.
- **Footer** — Export Markdown / Export JSON / Copy Summary, plus atlas padding
  **Calibration** (packs a throwaway atlas per format to measure real page
  padding on your Unity version — run it once per Unity upgrade).

**Rescan** reuses the hash cache in `Library/GDMemoryShield/`; **Full Rescan**
clears it.

## v1.0 analyzers

| Category | What it checks |
|---|---|
| Textures | Read/Write, uncompressed formats, missing platform overrides, mipmaps on UI, oversize caps, non-multiple-of-4 sources, duplicates, ASTC block sizes, RenderTextures |
| Sprite Atlases | duplicated sprites, atlased+standalone double-loads, packing efficiency, **mixed-lifetime pages**, POT padding waste (calibrated), near-POT-boundary wins, loose sprite clusters |
| Audio | Decompress On Load on long clips, non-streaming music, preload on large clips, SFX compression, stereo/48kHz waste, unreferenced clips |
| Scenes | requires Force Text serialization; asset-reference bloat, **disabled objects still load their assets**, pre-instantiated UI panels, mega-scenes, per-scene heaviness rows (est. resident MB per scene), 2D projects with baked lighting or HDR cameras |
| Retention | persistent roots holding assets (**Persistent Root Map**), **singleton census** (count, DontDestroyOnLoad, duplicate guards, prefab retention), static Unity Object references, unbalanced event subscriptions, grow-only collections, undestroyed runtime-created native objects, `.material`/`.mesh` copies |
| Update Loops | frame cost, not footprint: scene-wide Find calls, GetComponent, Instantiate/Destroy, collection/LINQ allocation, asset loading, logging, SendMessage, transform.Find and empty Update methods — all per-frame |

Mass findings (e.g. hundreds of textures with no platform override) are grouped
into one row per rule per folder — the folder is the actionable unit. Grouped
rows carry their instance count, and the score still deducts per instance.

Planned v1.1: atlas deep-scan mode, Loading/Pool/Mesh/PlayerSettings analyzers,
Word export. v1.2: Animation/Shader/CodeFootprint, optional Project Auditor
ingestion.

## Scoring

Deduction from 100: HIGH −6 (capped at −30 per category), MEDIUM −2, LOW −0.5.
A/B/C/D/F at 85/70/55/40. Single-issue project killers (20+ Read/Write
textures, a 60s+ decompressed clip, no variant stripping, 3x+ atlas padding,
atlased+standalone sprites, 5+ native leaks) cap the grade at C regardless.

The **memory budget** (`Assets/Editor/MemoryShield/MemoryBudget.asset`) sets
per-tier footprint ceilings shown beside the grade. The shipped numbers are a
starting proposal — calibrate against device captures of shipped titles, then
tick `calibrated` on the asset.

## What it does not see

Native memory held by ad SDKs, Firebase/Adjust/AppMetrica allocations, runtime
peak, fragmentation and GC behaviour. Every report says this explicitly —
being clear about what wasn't measured is more credible than a clean bill of
health.

## Standalone by design

Shares no assemblies with CodeShield. Everything is namespaced
`GameDistrict.MemoryShield` and confined to this package, so merging into
CodeShield later is a folder move plus deleting one duplicated brand file.
