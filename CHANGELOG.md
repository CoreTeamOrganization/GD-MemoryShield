# Changelog

## 1.0.3 — 2026-08-31

- Letter grades removed from the window, HTML and Markdown — the headline is
  now the score plus the total recoverable estimate. The grade field stays in
  the JSON for CI gates.
- HTML charts label rows with the last two path segments (readable names);
  the full path moved into the hover tooltip. Wider label column.

## 1.0.2 — 2026-08-31

- Export HTML: one-page visual report (category score bars, top recoverable
  estimates, heaviest scenes, root map, atlas and texture tables) — a single
  self-contained file for producers and IBO updates.
- Grouped folder findings preview a 2x2 grid of the textures inside the folder
  instead of a gray folder icon.

## 1.0.1 — 2026-08-31

- Detail card shows an asset preview thumbnail (Project Auditor parity).
- Findings list ranks your own code and assets above third-party content
  (Assets/Plugins, ThirdParty, SDK and store folders sort below).
- Detail card headline shows the instance count on grouped rows.
- Category pills read as scores ("62/100"), not bare numbers.

## 1.0.0 — 2026-08-31

Initial release.

- Analyzers: Textures, Sprite Atlases (estimate mode + padding calibration),
  Audio, Scenes (Force Text required), Retention (Persistent Root Map,
  singleton census), Update Loops (per-frame cost).
- Mass findings grouped per rule per folder; scoring stays per-instance.
- Markdown + JSON export, memory budget tiers, UI Toolkit window.
