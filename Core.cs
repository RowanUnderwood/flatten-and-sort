using System.IO;
using System.Text.Json;

namespace MediaFlattener;

public record ScanOptions(bool DateFolders, int FilesPerFolder = 1000, bool DeleteEmptyFolders = false, bool SortVrVideos = false);
public record FileStamp(long Length, DateTime CreatedUtc, DateTime ModifiedUtc);
public record MoveItem(string Source, string Destination, string Category, FileStamp Stamp)
{
    public string SourceDisplay { get; init; } = Source;
    public string DestinationDisplay { get; init; } = Destination;
    public string Detection { get; init; } = "Filename extension";
    public string Created => Stamp.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string Size => Engine.FormatBytes(Stamp.Length);
}
public record ScanPlan(string Root, ScanOptions Options, List<MoveItem> Items, List<string> Warnings, int Skipped)
{
    public List<string> SourceDirectories { get; init; } = [];
    public string? ProtectedDirectory { get; init; }
    public bool HasWork => Items.Count > 0 || (Options.DeleteEmptyFolders && SourceDirectories.Count > 0);
}
public record WorkProgress(string Phase, int Count, int Total, string Detail);
public record MoveResult(int Moved, int Failed, bool Cancelled, string LogPath, List<string> Errors)
{
    public int RemovedFolders { get; init; }
    public int CleanupFailed { get; init; }
}

public static class Engine
{
    public static readonly string[] Categories = ["image", "video", "other", "vr-video"];
    private static readonly HashSet<string> Images = new(
        ".jpg .jpeg .jpe .jfif .png .gif .bmp .dib .tif .tiff .webp .heic .heif .avif .ico .svg .raw .cr2 .cr3 .nef .nrw .arw .dng .orf .rw2 .pef .raf .psd .exr .jxl"
        .Split(' '), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Videos = new(
        ".mp4 .m4v .mov .avi .mkv .wmv .webm .mpg .mpeg .mpe .mts .m2ts .ts .3gp .3g2 .flv .vob .ogv .mxf .asf .divx .rm .rmvb"
        .Split(' '), StringComparer.OrdinalIgnoreCase);
    private const string Logs = ".flattener-logs";
    public static string Category(string path) => Images.Contains(Path.GetExtension(path)) ? "image" :
        Videos.Contains(Path.GetExtension(path)) ? "video" : "other";
    public static string FormatBytes(long value)
    {
        double n = value;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        while (n >= 1024 && i < units.Length - 1) { n /= 1024; i++; }
        return $"{n:0.#} {units[i]}";
    }
    public static bool IsWithin(string path, string parent) =>
        path.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool IsLink(FileAttributes attributes) => (attributes & FileAttributes.ReparsePoint) != 0;
    // Check all existing path components, including ancestors above the selected root.
    public static void ValidatePath(string path)
    {
        var cursor = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(cursor))
        {
            try
            {
                if (IsLink(File.GetAttributes(cursor)))
                    throw new IOException($"Links, junctions, symlinks, and cloud placeholders are skipped: {cursor}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            var next = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(cursor));
            if (next == cursor) break;
            cursor = next;
        }
    }
    private static FileStamp Stamp(string path)
    {
        var f = new FileInfo(path);
        f.Refresh();
        if (!f.Exists) throw new FileNotFoundException("The source file no longer exists.", path);
        if (IsLink(f.Attributes)) throw new IOException("The source is a link, symlink, or cloud placeholder.");
        return new FileStamp(f.Length, CreatedUtc(f), f.LastWriteTimeUtc);
    }
    // Linux birth times are often missing or reset by copying, so prefer whichever is earlier.
    public static DateTime CreatedUtc(FileInfo f) => OperatingSystem.IsWindows() || f.CreationTimeUtc <= f.LastWriteTimeUtc
        ? f.CreationTimeUtc : f.LastWriteTimeUtc;
    private static bool Reserved(string name) => Categories.Contains(name, StringComparer.OrdinalIgnoreCase) ||
        name.Equals(Logs, StringComparison.OrdinalIgnoreCase);

    public static ScanPlan Scan(string selectedRoot, ScanOptions options, IProgress<WorkProgress>? progress,
        CancellationToken token, string? protectedDirectory = null)
    {
        if (options.FilesPerFolder < 1 || options.FilesPerFolder > 1_000_000)
            throw new ArgumentException("Files per folder must be between 1 and 1,000,000.");
        string root = Normalize(selectedRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Choose an existing folder.");
        ValidatePath(root);
        string? protectedPath = protectedDirectory is null ? null : Normalize(protectedDirectory);
        if (protectedPath is not null && IsWithin(root, protectedPath))
            throw new IOException("Choose a data folder outside the application's own folder.");
        foreach (var name in Categories.Where(c => c != "vr-video" || options.SortVrVideos).Append(Logs))
        {
            var output = Path.Combine(root, name);
            ValidatePath(output);
            if (File.Exists(output)) throw new IOException($"A file named '{name}' blocks the output folder. Rename it first.");
        }
        string? probePath = options.SortVrVideos ? VrVideoDetector.FindFfprobe() : null;
        var found = new List<(string Path, string Category, FileStamp Stamp, string Detection)>();
        int untaggedVideos = 0;
        var warnings = new List<string>();
        var sourceDirectories = new List<string>();
        int skipped = 0;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var folder = stack.Pop();
            try
            {
                ValidatePath(folder);
                if (!folder.Equals(root, StringComparison.OrdinalIgnoreCase)) sourceDirectories.Add(folder);
                foreach (var path in Directory.EnumerateFileSystemEntries(folder))
                {
                    token.ThrowIfCancellationRequested();
                    if (folder.Equals(root, StringComparison.OrdinalIgnoreCase) && Reserved(Path.GetFileName(path)))
                    { skipped++; continue; }
                    if (protectedPath is not null && IsWithin(path, protectedPath)) { skipped++; continue; }
                    try
                    {
                        var attributes = File.GetAttributes(path);
                        if (IsLink(attributes))
                        { skipped++; warnings.Add($"Skipped link or cloud placeholder: {path}"); continue; }
                        if ((attributes & FileAttributes.Directory) != 0) stack.Push(path);
                        else
                        {
                            var stamp = Stamp(path);
                            string category = Category(path), detection = "Filename extension";
                            if (category == "video" && probePath is not null)
                            {
                                progress?.Report(new("Probing", found.Count, 0, path));
                                var result = VrVideoDetector.Probe(path, probePath, token);
                                detection = result.Reason;
                                if (result.IsVr) category = "vr-video";
                                else if (result.Warning is null) untaggedVideos++;
                                if (result.Warning is not null)
                                    warnings.Add($"VR metadata check for {path}: {result.Warning}");
                                if (Stamp(path) != stamp)
                                    throw new IOException("File changed while reading metadata. Scan again.");
                            }
                            found.Add((path, category, stamp, detection));
                            if (found.Count % 100 == 0)
                                progress?.Report(new("Scanning", found.Count, 0, path));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { warnings.Add($"Cannot read {path}: {ex.Message}"); skipped++; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { warnings.Add($"Cannot scan {folder}: {ex.Message}"); skipped++; }
            progress?.Report(new("Scanning", found.Count, 0, folder));
        }
        token.ThrowIfCancellationRequested();
        if (untaggedVideos > 0)
            warnings.Add($"{untaggedVideos:N0} video(s) have no recognized VR projection metadata and remain in video. Untagged VR cannot be reliably identified by aspect ratio alone.");
        progress?.Report(new("Planning", found.Count, found.Count, "Sorting creation dates and resolving duplicate names…"));
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reservedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<MoveItem>(found.Count);
        foreach (var category in Categories)
        {
            token.ThrowIfCancellationRequested();
            var group = found.Where(f => f.Category == category)
                .OrderBy(f => f.Stamp.CreatedUtc).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
            int chunkSize = options.DateFolders ? options.FilesPerFolder : Math.Max(1, group.Count);
            for (int start = 0; start < group.Count; start += chunkSize)
            {
                token.ThrowIfCancellationRequested();
                int end = Math.Min(start + chunkSize, group.Count);
                var destinationFolder = Path.Combine(root, category);
                if (options.DateFolders)
                {
                    string range = $"{group[start].Stamp.CreatedUtc.ToLocalTime():yyyy-MM-dd} to {group[end - 1].Stamp.CreatedUtc.ToLocalTime():yyyy-MM-dd}";
                    string baseFolder = Path.Combine(destinationFolder, range);
                    destinationFolder = baseFolder;
                    int suffix = 2;
                    while (Path.Exists(destinationFolder) || reservedFolders.Contains(destinationFolder))
                    {
                        token.ThrowIfCancellationRequested();
                        destinationFolder = baseFolder + $" ({suffix++})";
                    }
                    reservedFolders.Add(destinationFolder);
                }
                for (int index = start; index < end; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var f = group[index];
                    string name = Path.GetFileName(f.Path);
                    string destination = Path.Combine(destinationFolder, name);
                    int suffix = 2;
                    while (Path.Exists(destination) || planned.Contains(destination))
                    {
                        token.ThrowIfCancellationRequested();
                        destination = Path.Combine(destinationFolder,
                            Path.GetFileNameWithoutExtension(name) + $" ({suffix++})" + Path.GetExtension(name));
                    }
                    planned.Add(destination);
                    items.Add(new(f.Path, destination, category, f.Stamp) {
                        SourceDisplay = Path.GetRelativePath(root, f.Path),
                        DestinationDisplay = Path.GetRelativePath(root, destination),
                        Detection = f.Detection
                    });
                }
            }
        }
        progress?.Report(new("Ready", items.Count, items.Count, "Preview ready. No files have been moved."));
        return new(root, options, items, warnings, skipped)
        { SourceDirectories = sourceDirectories, ProtectedDirectory = protectedPath };
    }

    public static MoveResult Execute(ScanPlan plan, IProgress<WorkProgress>? progress, CancellationToken token)
    {
        ValidatePath(plan.Root);
        var logFolder = Path.Combine(plan.Root, Logs);
        ValidatePath(logFolder);
        Directory.CreateDirectory(logFolder);
        var logPath = Path.Combine(logFolder, $"moves-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
        using var stream = new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream);
        void Log(object entry) { writer.WriteLine(JsonSerializer.Serialize(entry)); writer.Flush(); stream.Flush(true); }
        Log(new { type = "session", root = plan.Root, planned = plan.Items.Count, options = plan.Options, utc = DateTime.UtcNow });
        int moved = 0, failed = 0, processed = 0;
        var errors = new List<string>();
        var preparedDateFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in plan.Items)
        {
            if (token.IsCancellationRequested) break;
            // Write-ahead journal: do not move anything if its intent cannot be recorded.
            Log(new { type = "pending", source = item.Source, destination = item.Destination, stamp = item.Stamp });
            string? error = null;
            try
            {
                if (!IsWithin(item.Source, plan.Root) || !IsWithin(item.Destination, plan.Root) ||
                    item.Source.Equals(item.Destination, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The planned path is outside the selected root or points to itself.");
                ValidatePath(item.Source);
                ValidatePath(item.Destination);
                if (Stamp(item.Source) != item.Stamp)
                    throw new IOException("Source changed after scanning. Scan again.");
                if (Path.Exists(item.Destination))
                    throw new IOException("Destination appeared after scanning. Scan again.");
                string folder = Path.GetDirectoryName(item.Destination)!;
                if (plan.Options.DateFolders && !preparedDateFolders.Contains(folder))
                {
                    if (Path.Exists(folder)) throw new IOException("Date folder appeared after scanning. Scan again.");
                    Directory.CreateDirectory(folder);
                    preparedDateFolders.Add(folder);
                }
                else Directory.CreateDirectory(folder);
                ValidatePath(folder);
                // File.Move with overwrite=false never replaces an existing destination.
                // No copy/delete fallback: a filesystem move failure leaves the source intact.
                File.Move(item.Source, item.Destination, false);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                error = ex.Message; failed++;
                errors.Add($"{item.Source}: {error}");
            }
            // If the result cannot be logged, stop the session; pending identifies the in-flight move.
            Log(new { type = error is null ? "moved" : "failed", source = item.Source, destination = item.Destination, error });
            processed++;
            if (processed % 25 == 0 || processed == plan.Items.Count)
                progress?.Report(new("Moving", processed, plan.Items.Count, item.Source));
        }
        int removedFolders = 0, cleanupFailed = 0, checkedFolders = 0;
        var cleanupFolders = plan.Options.DeleteEmptyFolders
            ? plan.SourceDirectories.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(p => p.Length).ToList()
            : [];
        foreach (var candidate in cleanupFolders)
        {
            if (token.IsCancellationRequested) break;
            string? cleanupError = null;
            bool empty = false;
            string folder = candidate;
            try
            {
                folder = Normalize(candidate);
                string root = Normalize(plan.Root);
                // Resolve and constrain every target before deletion. Never delete recursively.
                if (!IsWithin(folder, root) || folder.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                    Categories.Append(Logs).Any(name => IsWithin(folder, Path.Combine(root, name))) ||
                    (plan.ProtectedDirectory is not null && IsWithin(folder, plan.ProtectedDirectory)))
                    throw new IOException("Folder is outside the permitted source directories.");
                ValidatePath(folder);
                empty = Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { cleanupError = ex.Message; }

            if (empty && cleanupError is null)
            {
                if (token.IsCancellationRequested) break;
                Log(new { type = "folder_pending", path = folder });
                try
                {
                    ValidatePath(folder);
                    // The nonrecursive API refuses to remove a folder if a new file has appeared.
                    Directory.Delete(folder, recursive: false);
                    removedFolders++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { cleanupError = ex.Message; }
                Log(new { type = cleanupError is null ? "folder_removed" : "folder_failed", path = folder, error = cleanupError });
            }
            else if (cleanupError is not null)
                Log(new { type = "folder_failed", path = folder, error = cleanupError });

            if (cleanupError is not null)
            {
                cleanupFailed++;
                errors.Add($"Cannot remove empty folder {folder}: {cleanupError}");
            }
            checkedFolders++;
            progress?.Report(new("Cleaning", checkedFolders, cleanupFolders.Count, folder));
        }
        bool cancelled = token.IsCancellationRequested &&
            (processed < plan.Items.Count || checkedFolders < cleanupFolders.Count);
        Log(new { type = "complete", moved, failed, removedFolders, cleanupFailed, cancelled, utc = DateTime.UtcNow });
        return new(moved, failed, cancelled, logPath, errors)
        { RemovedFolders = removedFolders, CleanupFailed = cleanupFailed };
    }
}
