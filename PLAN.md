# DriveVisualizer — Plan

A WinDirStat-style disk usage analyzer built on WinUI 3. Scan a drive or folder, see where the space went via a sortable directory tree and a zoomable treemap, and act on it (open, reveal in Explorer, recycle, delete).

## Tech stack

| Concern | Choice | Why |
|---|---|---|
| UI framework | WinUI 3 (Windows App SDK 2.3), C#, .NET 10 | Modern Windows look, the stack the user asked for |
| MVVM | CommunityToolkit.Mvvm | Source-generated observables/commands, no ceremony |
| Treemap rendering | Win2D (`CanvasControl`) | A treemap has 10k–500k rectangles; XAML elements can't do that. Win2D gives immediate-mode GPU drawing + easy hit-testing |
| Scanning | Raw Win32 `FindFirstFileEx` (`FIND_FIRST_EX_LARGE_FETCH`) via P/Invoke, parallelized | 2–5x faster than `Directory.EnumerateFileSystemEntries`; one syscall gets name+size+attributes together |
| Packaging | MSIX (packaged) from day one | Clean install/uninstall, Store-ready, identity for future features |

## UI layout (the WinDirStat trio)

```
┌─────────────────────────────────────────────────────────┐
│  Toolbar: [Select target ▾] [Scan/Stop] [Refresh] [⚙]   │
├───────────────────────────────┬─────────────────────────┤
│ Directory tree (grid columns) │ File types panel        │
│ Name | Size | % | Items | ... │ ext | color | size | %  │
│  ▸ C:\                        │ .mp4 ■ 41 GB  22%       │
│    ▸ Users        ██████ 61%  │ .dll ■ 12 GB   6%       │
│    ▸ Windows      ███ 24%     │ ...                     │
├───────────────────────────────┴─────────────────────────┤
│ Treemap (Win2D, cushion-shaded, colored by extension)   │
│                                                         │
└─────────────────────────────────────────────────────────┘
```

- Selection is synchronized three ways: click a treemap tile → tree expands/scrolls to it; click a tree row → tile highlights; click an extension → all its tiles highlight.
- Tree rows show an inline percentage bar like WinDirStat's, plus columns: Size, Allocated, % of parent, Files, Last modified.
- WinUI's `TreeView` doesn't do columns, so the tree pane is a flattened-tree `ListView`/`ItemsRepeater` (indent by depth, expand/collapse re-flattens). This is also what keeps 1M rows virtualized and fast.

## Architecture

Three projects in one solution:

- **DriveVisualizer.Core** (class library, no UI deps) — scan engine, data model, treemap layout algorithm. Unit-testable, benchmarkable from a console harness.
- **DriveVisualizer.App** (WinUI 3) — views, view models, Win2D treemap control.
- **DriveVisualizer.Tests** — xUnit tests for Core (layout math, aggregation, mock file trees).

### Data model

```csharp
class FsNode {
    string Name;               // no full path stored — derive by walking parents (saves ~100s of MB on big drives)
    FsNode? Parent;
    FsNode[]? Children;        // null for files; plain array, not List (memory)
    long LogicalSize;          // sum for dirs, aggregated bottom-up
    long AllocatedSize;        // size-on-disk (cluster-rounded / compressed)
    uint Attributes;
    long LastWriteTimeTicks;
}
```

Memory is the constraint: a full C:\ can be 2–5M nodes. Keep the node lean, intern nothing per-node that can be derived.

### Scan engine

- Producer/consumer over `Channel<FsNode>`: worker tasks (≈ CPU count) pull directories, enumerate with `FindFirstFileEx`, push subdirectories back. Classic parallel BFS.
- **Correctness rules** (this is where naive scanners lie):
  - Skip reparse points (junctions, symlinks) for size aggregation — otherwise `C:\Users\...\AppData` junctions double-count.
  - Use `\\?\`-prefixed paths for >260-char support.
  - Allocated size: round up to cluster size; use `GetCompressedFileSize` for compressed/sparse files.
  - Access denied → record the node as unscannable, keep going; show a count of skipped items.
- Progress: scanner mutates the tree off-thread; UI polls a snapshot every ~250 ms via `DispatcherQueue` (dirs found, files found, bytes so far) — no per-file UI events.
- Cancellation via `CancellationToken`; Stop keeps partial results.
- **Later (v2): NTFS MFT fast mode** — read the Master File Table directly like WizTree does (whole drive in ~2 s, needs admin + NTFS). Designed behind an `IScanner` interface from day one so it slots in.

### Treemap

- **Squarified treemap** layout (Bruls/Huizing/van Wijk) — pure function `(node, rect) → tile rects`, lives in Core, unit-tested.
- **Cushion shading** (the WinDirStat signature 3D look) — per-tile gradient in Win2D; start flat-with-borders in M3, add cushions in M5.
- Color by file extension, matching the file-types panel legend.
- Layout only down to tiles ≥ ~3px² (children of tiny tiles render as one blended tile) — keeps rect count bounded.
- Hit-testing: keep the laid-out rect list, binary/spatial lookup on pointer events. Hover shows a tooltip (full path + size); double-click zooms into that directory; breadcrumb/Escape zooms out.
- Re-render on: scan finish, selection change, zoom, resize (debounced).

### Actions (context menu on tree rows and treemap tiles)

- Open, Open containing folder (Explorer with item selected), Copy path.
- Delete to Recycle Bin and Delete permanently — via `IFileOperation` (shell), so we get the OS confirmation/progress/undo semantics. After delete: remove node, re-aggregate ancestors, refresh treemap. Big scary confirmation for permanent delete.
- Properties (shell properties dialog).

### Elevation

Run unelevated by default; count access-denied directories and show an info bar: "N folders couldn't be read — Relaunch as administrator". Relaunch preserves the selected target.

## Milestones

- **M1 — Scan engine (Core + console harness).** ✅ DONE. Parallel Win32 enumeration, correct sizes, junction handling, cancellation, 7 unit tests. Benchmark: full C:\ (1.04M files, 318K dirs, 510 GB) in 25.8 s.
- **M2 — App shell + directory tree.** Drive/folder picker, scan with progress, virtualized tree-grid with size/% columns and percentage bars, sorting. *Exit: usable "where did my space go" tool even without treemap.*
- **M3 — Treemap.** Squarified layout, Win2D rendering, extension colors, hover/click/zoom, selection sync with tree. *Exit: feels like WinDirStat.*
- **M4 — File types panel + actions.** Extension stats, legend-driven highlighting, open/reveal/recycle/delete with re-aggregation.
- **M5 — Polish.** Cushion shading, settings (colors, exclusions, treemap style), dark/light theme, remembered window state, skipped-items report, elevation relaunch.
- **v2 ideas.** MFT fast scan, compare two scans (what grew?), duplicate finder, scan export/import.

## Risks / gotchas to keep in mind

- WinUI `TreeView` can't do columns → flattened ListView approach decided above.
- XAML treemap would die at scale → Win2D decided above.
- Junctions/hardlinks double-counting → skip reparse points; hardlinks (rare) counted once per link like WinDirStat does, documented.
- OneDrive placeholder files: logical size ≠ on-disk 0 — allocated-size column makes this visible rather than wrong.
- Win2D + WinUI 3 requires the `Microsoft.Graphics.Win2D` package (not the UWP one).
