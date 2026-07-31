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

> Your disk is full. Again. See exactly why in seconds — then watch what changes, day after day.

## Description

```
Your disk is full. Again. DriveVisualizer shows you exactly why in seconds — and
unlike every other space analyzer, it keeps watching, so next time you'll know
exactly what grew, when, and by how much.

See your whole drive at once
A parallel scanner tears through a million files in seconds while the map builds
live in front of you. Explore the result as a classic cushion-shaded treemap,
a sunburst of rings, or a flame-style icicle view — every shape sized by what it
actually costs you on disk. Hover for details, click to jump to the folder tree,
double-click to dive deeper.

Colors that actually mean something
Video, pictures, code, archives, documents, temp & logs, apps, disk images — nine
categories, one legend, one-click filtering, and a colorblind-validated palette.
One glance tells you whether that mystery folder is your vacation footage or a
node_modules graveyard.

Catch what changed — the feature nobody else has
Every scan saves a snapshot. From then on:
• A red/green Change column shows what grew and what shrank, folder by folder.
• One click opens a then/change/now report naming the exact folders and files
  that moved.
• Size history charts weeks of growth, stacked by category, snapshot by snapshot.
• Snapshots take themselves — hourly, daily, or weekly, even while the app is
  closed — with retention limits you control. Come back from vacation and see
  precisely what your PC did without you.

Clean up with confidence
Flip on "Cleanup candidates" to highlight only the safe wins: temp folders,
caches, logs, recycle bin, node_modules. Right-click anything to send it to the
Recycle Bin, delete it permanently, or compress it to a zip and drop the
original. Sizes update instantly as you go.

Know your drive's health
Full S.M.A.R.T. detail without running as administrator: temperature, wear,
power-on hours, lifetime data written, unsafe shutdowns, media errors — plus
model, bus (NVMe/SATA/USB), and SSD vs HDD. Health is stamped into every
snapshot, so you can watch wear and free space trend over months, not just
read today's numbers.

Accurate where it counts
Junctions and symlinks are never double-counted. 260+ character paths just work.
Compressed, sparse, and cloud-placeholder files report their true size on disk,
so the numbers add up to what Windows really uses.

Yours, privately
No accounts, no ads, no telemetry, no network access — your file names never
leave your machine. Free and open source (MIT) at
github.com/guscatalano/DriveVisualizer.
```

## Product features (Store bullet list)

- Scan a full drive in seconds with a parallel Win32 scanner
- Treemap, sunburst, and icicle visualizations with live build during the scan
- Nine color-coded file categories with legend and one-click filtering
- Red/green Change column shows what grew or shrank since your last scan
- Size history charts your drive over time, per category, file-level detail
- Scheduled background snapshots that run even when the app is closed
- S.M.A.R.T. drive health: temperature, wear, power-on hours, lifetime writes
- Drive health is saved with every snapshot, so wear and free space trend over time
- Delete to Recycle Bin, delete permanently, or compress-to-zip from a right-click
- "Cleanup candidates" highlights temp files, caches, logs, and node_modules
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

- Screenshots (1920×1080, demo data): `docs/store/` — upload all seven under
  "Desktop" screenshots: treemap, sunburst, settings, Change column, size
  history, then/change/now report, Drive info dialog.
- Store logos are generated from the MSIX package (Assets\*.png) automatically.
- Upload package: build artifact `DriveVisualizer-store-upload` (.msixupload)
  from the latest CI run, or `artifacts\store\` from a local Release build.
