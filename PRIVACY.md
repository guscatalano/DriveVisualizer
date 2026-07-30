# DriveVisualizer Privacy Policy

*Last updated: July 30, 2026*

DriveVisualizer is a local disk-usage analyzer. Your privacy model is simple: **everything stays on your machine.**

- **No data collection.** DriveVisualizer does not collect, transmit, or share any personal data, telemetry, analytics, or usage statistics. It contains no advertising and no third-party SDKs that phone home.
- **No network access.** The app makes no network requests. Scanning, snapshots, history, and reports are all computed and stored locally.
- **What it reads:** file and folder metadata (names, sizes, timestamps, attributes) on the drives and folders you choose to scan. File *contents* are never read, except when you explicitly use "Compress and delete original", which reads the file to produce the zip you asked for.
- **What it stores locally:** optional scan snapshots (folder sizes, category totals, and the names of your largest files) in the app's private data folder, so features like the Change column and Size history work. You can view, limit, or delete this data at any time from Settings (Open folder / Clear / retention options), and turning off auto-save deletes it.
- **Optional background task:** if you enable "Snapshot even when the app is closed", a Windows scheduled task runs the same local scan on your machine. It can be turned off from Settings, which removes the task.
- **Reports you export** (HTML reports, snapshot files) are ordinary local files under your control; they are only shared if you share them.

Questions: gus@guscatalano.com · [guscatalano.dev](https://guscatalano.dev)
