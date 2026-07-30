# Microsoft Store listing — DriveVisualizer

Everything Partner Center asks for, ready to paste. Product identity is already
reserved: **GusCatalano.DriveVisualizer**, Store ID **9NRTBGF2T3B6**.

## Basics

| Field | Value |
|---|---|
| Display name | DriveVisualizer |
| Category | Utilities & tools |
| Subcategory | File managers |
| Pricing | Free |
| Privacy policy URL | https://github.com/guscatalano/DriveVisualizer/blob/main/PRIVACY.md |
| Website | https://guscatalano.dev |
| Support contact | gus@guscatalano.com |
| Copyright | © 2026 Gus Catalano. MIT licensed. |
| Age rating (IARC) | Answer "no" to everything — utility app, no user content, no data collection |

## Short description / promo line

> See what's eating your disk — scan in seconds, explore it as a treemap, sunburst, or icicle, and clean up safely.

## Description

```
Wondering where all your disk space went? DriveVisualizer scans an entire drive in
seconds and turns it into a picture you can actually read.

EXPLORE IT THREE WAYS
Classic cushion-shaded treemap, DaisyDisk-style sunburst rings, or a flame-graph
icicle view. Watch the map assemble live while the scan runs. Hover for details,
click to locate anything in the folder tree, double-click to dive in.

COLORS THAT MEAN SOMETHING
Nine semantic categories — apps, archives, pictures, documents, temp & logs, code,
disk images, video & audio — with a legend, a one-click category filter, and a
colorblind-validated palette. Folders are tinted by what's inside them.

CLEAN UP WITH CONFIDENCE
Right-click anything to open it, reveal it in Explorer, send it to the Recycle Bin,
delete it permanently, or compress it to a zip and remove the original. A "Cleanup
candidates" switch highlights only the safe stuff: temp folders, caches, logs,
recycle bin, node_modules. Sizes update instantly as you delete.

KNOW WHAT CHANGED
Every scan can save a snapshot. A red/green Change column shows exactly which
folders grew or shrank since last time, one click opens a full then/change/now
report, and Size history charts your drive over days and weeks — down to the
individual files that moved. Snapshots can run on a schedule even when the app
is closed, with retention limits you control.

FAST AND HONEST
A parallel Win32 scanner covers a million files in seconds. Junctions and symlinks
are never double-counted, 260+ character paths just work, and compressed, sparse,
and cloud-placeholder files report their true size on disk.

PRIVATE BY DESIGN
No accounts, no ads, no telemetry, no network access. Everything stays on your
machine. Open source (MIT) at github.com/guscatalano/DriveVisualizer.
```

## Product features (Store bullet list)

- Scan a full drive in seconds with a parallel Win32 scanner
- Treemap, sunburst, and icicle visualizations with live build during the scan
- Nine color-coded file categories with legend and one-click filtering
- Red/green Change column shows what grew or shrank since your last scan
- Size history charts your drive over time, per category, file-level detail
- Scheduled background snapshots that run even when the app is closed
- Delete to Recycle Bin, delete permanently, or compress-to-zip from a right-click
- "Cleanup candidates" highlights temp files, caches, logs, and node_modules
- Drive details at a glance: SSD or HDD, NVMe/SATA/USB bus, health, capacity
- Junction-safe, long-path-safe, allocated-size accurate — no double counting
- Exportable HTML reports and shareable snapshot files, dark/light aware
- Light and dark themes, adjustable layouts, no ads, no telemetry, open source

## Search terms (max 7)

disk space · disk usage · treemap · WinDirStat · storage analyzer · disk cleanup · folder sizes

## What's new in 1.0

```
Initial release: fast parallel scanning, treemap/sunburst/icicle views, semantic
category colors, cleanup actions, change tracking, size history with scheduled
background snapshots, HTML reports, light/dark themes.
```

## Assets

- Screenshots (1920×1080, demo data): `docs/store/` — upload all of them under
  "Desktop" screenshots.
- Store logos are generated from the MSIX package (Assets\*.png) automatically.
- Upload package: build artifact `DriveVisualizer-store-upload` (.msixupload)
  from the latest CI run, or `artifacts\store\` from a local Release build.
