# Media Flattener

Native Windows desktop application built with C# and WPF.

## Run

Double-click **Launch Media Flattener.cmd**, or open **App\MediaFlattener.exe**.
The included executable is self-contained for 64-bit Windows; no separate Python or .NET installation is needed.
Keep the app in a separate folder from the data you want to organize.

## Workflow

1. Browse to the root folder, or paste its path.
2. Optionally enable **Group into folders by creation date** and set **Maximum files per folder** (default: 1000).
3. Optionally enable **Sort videos with VR metadata into vr-video** (off by default). This adds the fourth category and inspects video metadata during scanning. Optionally tick **Delete empty source folders after moving** (off by default), then click **Scan folder**. The animated indicator and live file count remain responsive during recursive scanning.
4. Review the category counts, file sizes, scan notes, and the full move table. Filter by category if helpful. Source and destination paths are relative to the selected root; hover for full paths.
5. Click **Continue → Move files** to execute the entire preview, including categories hidden by the table filter. The move progress bar shows completed operations.

A scan only reads files and builds a plan. It does not create output folders or move anything.
Changing the root or options invalidates the plan and requires another scan.

## Output

Files move under the selected root into:

- **image** — common images and camera RAW formats, including JPG, PNG, HEIC, TIFF, WebP, SVG, AVIF, CR2, CR3, NEF, ARW, DNG and more.
- **video** — common video formats, including MP4, MOV, MKV, AVI, WebM, MTS, M2TS and more.
- **vr-video** (optional) — videos with recognized spherical/projection metadata, including supported 180°/360° formats.
- **other** — every other extension, including audio, documents, and files without extensions.

Image/video/other classification uses the filename extension, case-insensitively. With VR sorting enabled, video candidates are additionally inspected using ffprobe. Files are never converted. The full extension lists are in Core.cs.

### Optional VR detection

Enable **Sort videos with VR metadata into vr-video** before scanning. Tagged VR videos get their own count, filter, and destination. Hover over the preview's **Type** cell for the detection reason. Date grouping, file limits, duplicate handling, journals, and empty-folder cleanup apply to VR videos too. The **vr-video** folder is created only when a matching file is moved there.

The detector checks video streams for ffprobe's spherical mapping/projection metadata and recognized projection tags. It supports equirectangular, tiled equirectangular and cubemap output, plus mesh when exposed by the installed ffprobe version. Attached cover images are ignored. Supported MP4/MOV spherical metadata and Matroska/WebM projection metadata are read through ffprobe.

**There is no universal aspect-ratio test for VR.** A 2:1 image is common for monoscopic 360° equirectangular video, but ordinary video can have the same dimensions. Stereo layouts also change the stored frame ratio. Side-by-side/top-bottom metadata alone may describe a flat 3D movie, so it is not sufficient either. Filenames, dimensions, and stereo layout alone never trigger VR sorting.

Untagged VR, stripped metadata, unsupported camera-specific formats and projections unsupported by the installed ffprobe can remain in **video**. Scan notes summarize videos without recognized VR metadata; a negative result does not prove a file is non-VR. Review the preview. Previously sorted videos in the reserved **video** folder are not reclassified automatically.

**Dependency:** install FFmpeg with **ffprobe.exe** on PATH, or place a working ffprobe distribution beside **App/MediaFlattener.exe**. The app checks its own directory first, then absolute PATH directories. ffprobe is already installed on the development computer but is not bundled in this app. If it is missing, an enabled VR scan stops with setup instructions; ordinary sorting works without it.

Probing reads files locally and starts no visible console windows. Each file has a 15-second deadline. Cancellation stops the probe and discards the incomplete scan plan. Corrupt/unreadable files and timed-out probes stay in **video** with scan notes; probe warnings are also reported. Large collections and network shares take longer with metadata scanning enabled.

Metadata references: [Spherical Video specification](https://github.com/google/spatial-media/blob/master/docs/spherical-video-v2-rfc.md), [VR180 specification](https://github.com/google/spatial-media/blob/master/docs/vr180.md), and [ffprobe documentation](https://ffmpeg.org/ffprobe.html).

Existing top-level **image**, **video**, **other**, **vr-video**, and **.flattener-logs** directories are reserved and excluded from scans. This makes subsequent scans process new files elsewhere under the root without repeatedly moving organized files. Choose a different root or relocate previously organized files if you want to reorganize them.

### Date folders and file limits

Within each category, files are sorted by Windows filesystem creation timestamp, oldest first, and divided into groups of at most the selected limit. Dates displayed in names use the computer's local time zone. This is filesystem creation time, not camera EXIF capture time or video recording time.

For example, a group of 1000 images might go into:

    image\2024-01-03 to 2024-06-18\

The first and last dates come from the oldest and newest creation timestamps in that group. A remaining group may contain fewer files. Each category has its own grouping. When multiple groups share a date range, names receive suffixes such as ` (2)`. Existing date folders are not refilled; each scan plans fresh groups, preserving the per-folder limit for that run. Keep the folder tree unchanged while scanning and moving; the app cannot control files added by another program.

## File handling

- Duplicate filenames receive a numeric suffix during planning, such as `photo (2).jpg`. Existing files are never intentionally overwritten.
- Files changed since the scan (creation time, modification time, or length), missing sources, and newly occupied destinations are skipped with an error. Changes that preserve all of those metadata values cannot be detected.
- Directory junctions, symbolic links, and other reparse points (including some cloud placeholders) are skipped. Linked output folders are rejected.
- The application's own executable directory is excluded if it is under the chosen root. Selecting that directory or its descendants as the root is rejected.
- Unreadable files and folders appear in Scan notes. Readable files can still be previewed and moved.
- Empty source directories remain in place unless **Delete empty source folders after moving** is ticked. Cleanup runs after file moves, deepest folders first, and includes folders already empty at scan time. Only scanned source folders are considered; the selected root, output/log folders, and application folder are kept. Nonempty folders are never recursively deleted. Cleanup can also run when there are no files to move. Changing the checkbox requires a fresh scan.
- The preview shows how many source folders cleanup will check. Completion reports how many were deleted, and the journal records deletions and errors. Cancellation stops cleanup between folder operations; completed deletions remain. Empty folders are permanently removed, not sent to the Recycle Bin.
- Cancel stops between filesystem operations; the current operation may need to finish. Completed moves stay in their destinations. Closing while busy requests cancellation; close again after it stops.
- Files are moved, not backed up. There is no automatic undo or rollback.

## Journals and interrupted runs

Each move session writes a JSON-lines journal in **.flattener-logs** under the selected root. The journal includes source and destination paths, a pending record before each attempted move, a moved/failed result, and final counts. Use **Open log folder** after completion.

If writing the journal fails, the session stops. A pending entry without a matching result means the app was interrupted around that move: inspect both paths before acting. Completed files remain in their destinations, and scanning again finds remaining sources. Journals are audit records; they are not an automatic recovery tool.

## Build and verify

Install a .NET SDK with Windows desktop build support (the project targets .NET 8 for Windows).

    powershell -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File .\Test.ps1

Build.ps1 publishes a self-contained Windows x64 executable to App. The first build may download .NET runtime packages from NuGet. Source builds use the locally available .NET SDK.

Tests use disposable sample directories under the Windows temporary folder. Test groups cover recursion and classification, duplicate preservation, date ranges and limits, stale previews, cancellation, reserved paths, junction protection, and the actual WPF scan/filter/continue workflow. Tests also render preview.png for layout inspection. VR tests require ffprobe and cover real synthetic MP4 V1/V2 and MKV metadata, untagged 2:1 video, flat stereo, read failures, deadlines, cancellation, missing dependencies, VR grouping/collisions, and the optional WPF controls. Tiny generated clips are checked in under Tests/Fixtures; regenerate them with Python using Tests/GenerateFixtures.py if needed (requires ffmpeg).
