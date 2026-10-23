using System.IO;
using MediaFlattener;

// Engine tests shared by the Windows (WPF) and Linux (Avalonia) test runners.
internal static partial class Program
{
    private static int passed;
    private static readonly string TestParent = Path.Combine(Path.GetTempPath(), "MediaFlattener.Tests");
    private static readonly List<string> CreatedRoots = [];
    private static string Root()
    {
        string root = Path.Combine(TestParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); CreatedRoots.Add(root); return root;
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static string FileAt(string root, string relative, string content = "sample", int day = 1)
    {
        string path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content); File.SetCreationTime(path, new DateTime(2024, 1, day, 12, 0, 0)); return path;
    }
    private static ScanPlan Scan(string root, bool dates = false, int cap = 1000) =>
        Engine.Scan(root, new(dates, cap), null, CancellationToken.None);
    private static void Test(string title, Action run) { run(); passed++; Console.WriteLine("PASS " + title); }
    // Windows junctions need no symbolic-link privilege; Unix symlinks need none either.
    private static void MakeLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe") {
            UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "-NoProfile", "-Command", $"New-Item -ItemType Junction -Path '{link}' -Target '{target}' | Out-Null" }
        })!;
        process.WaitForExit(); Check(process.ExitCode == 0, "Create test link.");
    }
    private static void CoreTests()
    {
        Test("recursive classification, read-only scan, duplicate preservation and repeat scan", () => {
            string root = Root();
            FileAt(root, "a/photo.JPG", "first"); FileAt(root, "b/deep/photo.jpg", "second");
            FileAt(root, "movie.MP4", "movie"); FileAt(root, "docs/readme.txt", "text");
            FileAt(root, "no-extension", "other"); FileAt(root, "image/photo.jpg", "existing");
            var plan = Scan(root);
            Check(plan.Items.Count == 5, "Expected five candidates.");
            Check(plan.Items.Count(i => i.Category == "image") == 2, "Image classification.");
            Check(plan.Items.Count(i => i.Category == "video") == 1, "Video classification.");
            Check(plan.Items.Select(i => i.Destination).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 5, "Unique paths.");
            Check(plan.Items.All(i => File.Exists(i.Source)), "Scan must not move sources.");
            Check(!Directory.Exists(Path.Combine(root, "video")), "Scan must not create folders.");
            var result = Engine.Execute(plan, null, CancellationToken.None);
            Check(result.Moved == 5 && result.Failed == 0, "Move counts.");
            Check(result.RemovedFolders == 0 && Directory.Exists(Path.Combine(root, "b/deep")), "Cleanup is off by default.");
            Check(File.ReadAllText(Path.Combine(root, "image/photo.jpg")) == "existing", "Existing file must survive.");
            Check(plan.Items.All(i => File.Exists(i.Destination) && !File.Exists(i.Source)), "Move postconditions.");
            Check(Scan(root).Items.Count == 0, "Outputs and logs excluded on repeat.");
            Check(File.ReadLines(result.LogPath).Count(l => l.Contains("\"type\":\"moved\"")) == 5, "Journal result count.");
        });
        Test("creation-date order, range names, repeated ranges, caps and existing buckets", () => {
            string root = Root();
            for (int i = 0; i < 5; i++) FileAt(root, $"nested/{i}.jpg", $"image {i}", 1);
            FileAt(root, "v/late.mp4", "video", 12); FileAt(root, "v/early.mp4", "video", 3);
            Directory.CreateDirectory(Path.Combine(root, "image/2024-01-01 to 2024-01-01"));
            var plan = Scan(root, true, 2);
            Check(plan.Items.GroupBy(i => Path.GetDirectoryName(i.Destination)).All(g => g.Count() <= 2), "Every bucket respects cap.");
            Check(plan.Items.Where(i => i.Category == "image").Select(i => Path.GetDirectoryName(i.Destination)).Distinct().Count() == 3, "Three distinct same-date buckets.");
            Check(plan.Items.First(i => i.Category == "video").Source.EndsWith("early.mp4"), "Creation order.");
            Check(plan.Items.First(i => i.Category == "video").Destination.Contains("2024-01-03 to 2024-01-12"), "Date range.");
            Check(plan.Items.First().Destination.Contains("(2)"), "Existing date bucket not reused.");
            var result = Engine.Execute(plan, null, CancellationToken.None);
            Check(result.Moved == 7, "Date moves.");
        });
        Test("changed sources and newly occupied destinations stay intact", () => {
            string root = Root(); FileAt(root, "a/photo.png"); FileAt(root, "b/video.mov");
            var plan = Scan(root);
            File.AppendAllText(plan.Items[0].Source, "changed");
            Directory.CreateDirectory(Path.GetDirectoryName(plan.Items[1].Destination)!);
            File.WriteAllText(plan.Items[1].Destination, "new arrival");
            var result = Engine.Execute(plan, null, CancellationToken.None);
            Check(result.Moved == 0 && result.Failed == 2, "Both stale entries must fail.");
            Check(plan.Items.All(i => File.Exists(i.Source)), "Sources retained.");
            Check(File.ReadAllText(plan.Items[1].Destination) == "new arrival", "New arrival retained.");
        });
        Test("newly occupied date folders are rejected", () => {
            string root = Root(); FileAt(root, "a/photo.jpg");
            var plan = Scan(root, true, 1);
            Directory.CreateDirectory(Path.GetDirectoryName(plan.Items[0].Destination)!);
            var result = Engine.Execute(plan, null, CancellationToken.None);
            Check(result.Moved == 0 && result.Failed == 1, "Stale bucket must fail.");
        });
        Test("cancellation preserves unprocessed files", () => {
            string root = Root(); FileAt(root, "a/photo.jpg");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            bool cancelled = false;
            try { Engine.Scan(root, new(false), null, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Cancelled scan must not return a partial plan.");
            var plan = Scan(root);
            var result = Engine.Execute(plan, null, cancel.Token);
            Check(result.Cancelled && result.Moved == 0 && File.Exists(plan.Items[0].Source), "Cancelled move leaves source.");
        });
        Test("invalid caps, blocked output names and protected application directory", () => {
            string root = Root();
            bool blocked = false; try { Scan(root, true, 0); } catch (ArgumentException) { blocked = true; }
            Check(blocked, "Invalid cap rejected.");
            FileAt(root, "image");
            blocked = false; try { Scan(root); } catch (IOException) { blocked = true; }
            Check(blocked, "File blocking output rejected.");
            root = Root(); Directory.CreateDirectory(Path.Combine(root, "app"));
            FileAt(root, "app/program.exe"); FileAt(root, "data/file.txt");
            var plan = Engine.Scan(root, new(false), null, CancellationToken.None, Path.Combine(root, "app"));
            Check(plan.Items.Count == 1, "Application subtree excluded.");
        });
        Test("junctions and symlinks cannot escape the chosen root", () => {
            string root = Root(), outside = Root(); FileAt(outside, "keep.jpg");
            string link = Path.Combine(root, "linked");
            // Junction creation does not require symbolic-link privileges.
            MakeLink(link, outside);
            try {
                var plan = Scan(root); Check(plan.Items.Count == 0 && plan.Warnings.Count > 0, "Junction skipped.");
                Directory.Delete(link); Directory.CreateDirectory(Path.Combine(root, "a")); FileAt(root, "a/test.jpg");
                plan = Scan(root);
                MakeLink(Path.Combine(root, "image"), outside);
                var result = Engine.Execute(plan, null, CancellationToken.None);
                Check(result.Moved == 0 && result.Failed == 1, "Destination junction rejected.");
                Check(File.Exists(Path.Combine(outside, "keep.jpg")), "Outside file preserved.");
            } finally {
                foreach (string path in new[] { link, Path.Combine(root, "image") })
                    if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) Directory.Delete(path);
            }
        });
        if (!OperatingSystem.IsWindows())
            Test("Linux dates use the earlier of birth and modified time", () => {
                string root = Root(), path = Path.Combine(root, "old.jpg");
                File.WriteAllText(path, "copied today");
                File.SetLastWriteTimeUtc(path, new DateTime(2019, 5, 4, 0, 0, 0, DateTimeKind.Utc));
                var plan = Scan(root, true);
                Check(plan.Items.Single().Stamp.CreatedUtc.Year == 2019, "Modified time wins when earlier.");
                Check(plan.Items.Single().Destination.Contains("2019-05-0"), "Date folder uses the earlier time.");
            });
    }

    private sealed class DirectProgress(Action<WorkProgress> action) : IProgress<WorkProgress>
    { public void Report(WorkProgress value) => action(value); }
    private static ScanPlan CleanupScan(string root, string? protectedDirectory = null) =>
        Engine.Scan(root, new(false, DeleteEmptyFolders: true), null, CancellationToken.None, protectedDirectory);
    private static void CleanupTests()
    {
        Test("cleanup removes nested empty sources and preserves root, reserved and application folders", () => {
            string root = Root();
            FileAt(root, "source/nested/photo.jpg");
            Directory.CreateDirectory(Path.Combine(root, "already-empty/child"));
            Directory.CreateDirectory(Path.Combine(root, "image/keep-empty"));
            Directory.CreateDirectory(Path.Combine(root, "video"));
            Directory.CreateDirectory(Path.Combine(root, "other"));
            Directory.CreateDirectory(Path.Combine(root, "app/keep-empty"));
            var plan = CleanupScan(root, Path.Combine(root, "app"));
            Check(plan.SourceDirectories.Count == 4, "Only four eligible source folders.");
            Check(Directory.Exists(Path.Combine(root, "already-empty/child")), "Scan must not delete.");
            var result = Engine.Execute(plan, null, CancellationToken.None);
            Check(result.RemovedFolders == 4 && result.CleanupFailed == 0 && result.Moved == 1, "Removed deepest folders and then parents.");
            foreach (var name in new[] { "", "image/keep-empty", "video", "other", "app/keep-empty", ".flattener-logs" })
                Check(Directory.Exists(Path.Combine(root, name)), "Protected folder retained: " + name);
            Check(File.ReadLines(result.LogPath).Count(l => l.Contains("\"type\":\"folder_removed\"")) == 4, "Folder journal count.");
        });
        Test("cleanup preserves newly nonempty folders and failed-move sources", () => {
            string root = Root();
            Directory.CreateDirectory(Path.Combine(root, "empty"));
            FileAt(root, "changed/photo.jpg");
            var plan = CleanupScan(root);
            FileAt(root, "empty/hidden.txt", "new content");
            File.SetAttributes(Path.Combine(root, "empty/hidden.txt"), FileAttributes.Hidden);
            File.AppendAllText(Path.Combine(root, "changed/photo.jpg"), "changed");
            var result = Engine.Execute(plan, null, CancellationToken.None);
            Check(result.Failed == 1 && result.RemovedFolders == 0, "Nonempty source folders survive.");
            Check(File.Exists(Path.Combine(root, "empty/hidden.txt")), "Hidden file preserved.");
            Check(File.Exists(Path.Combine(root, "changed/photo.jpg")), "Failed-move source preserved.");
        });
        Test("cleanup-only plans and cancellation before and during cleanup", () => {
            string root = Root();
            for (int i = 0; i < 3; i++) Directory.CreateDirectory(Path.Combine(root, "empty-" + i));
            var plan = CleanupScan(root);
            Check(plan.HasWork && plan.Items.Count == 0, "Cleanup-only plan is executable.");
            using var cancelBefore = new CancellationTokenSource(); cancelBefore.Cancel();
            var result = Engine.Execute(plan, null, cancelBefore.Token);
            Check(result.Cancelled && result.RemovedFolders == 0, "Cancellation skips cleanup.");
            using var cancelDuring = new CancellationTokenSource();
            result = Engine.Execute(plan, new DirectProgress(p => {
                if (p.Phase == "Cleaning") cancelDuring.Cancel();
            }), cancelDuring.Token);
            Check(result.Cancelled && result.RemovedFolders == 1, "Cancellation stops between folders.");
            result = Engine.Execute(CleanupScan(root), null, CancellationToken.None);
            Check(result.RemovedFolders == 2 && !result.Cancelled, "Remaining empty folders cleaned on rescan.");
        });
        Test("cleanup rejects folders replaced with links after scanning", () => {
            string root = Root(), outside = Root();
            string candidate = Path.Combine(root, "empty");
            Directory.CreateDirectory(candidate);
            var plan = CleanupScan(root);
            Directory.Delete(candidate, false);
            MakeLink(candidate, outside);
            try {
                var result = Engine.Execute(plan, null, CancellationToken.None);
                Check(result.RemovedFolders == 0 && result.CleanupFailed == 1, "Replaced folder skipped.");
                Check(Directory.Exists(candidate) && Directory.Exists(outside), "Junction and outside target kept.");
            } finally { Directory.Delete(candidate, false); }
        });
    }


    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    private static string FixtureAt(string root, string relative, string fixture)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(Fixture(fixture), path);
        return path;
    }
    private static void VrTests()
    {
        Test("projection metadata distinguishes spherical video from aspect ratio and flat stereo", () => {
            string probe = VrVideoDetector.FindFfprobe();
            foreach (string fixture in new[] { "spherical-v1.mp4", "spherical-v2.mp4", "spherical.mkv" })
            {
                var result = VrVideoDetector.Probe(Fixture(fixture), probe, CancellationToken.None);
                Check(result.IsVr && result.Warning is null, $"Spherical fixture: {fixture}: {result}");
            }
            foreach (string fixture in new[] { "untagged.mp4", "flat-stereo.mp4" })
            {
                var result = VrVideoDetector.Probe(Fixture(fixture), probe, CancellationToken.None);
                Check(!result.IsVr && result.Warning is null, $"2:1 and stereo alone are not VR: {fixture}: {result}");
            }
            var json = """{"streams":[{"codec_type":"video","disposition":{"attached_pic":1},"side_data_list":[{"side_data_type":"Spherical Mapping","projection":"equirectangular"}]},{"codec_type":"video","width":3840,"height":1920}]}""";
            Check(!VrVideoDetector.FromJson(json).IsVr, "Ignore spherical cover art and ordinary 2:1 stream.");
            json = """{"streams":[{"codec_type":"audio","tags":{"projection":"equirectangular"}},{"codec_type":"video","tags":{"title":"VR180 SBS 360"}}]}""";
            Check(!VrVideoDetector.FromJson(json).IsVr, "Audio tags and titles do not classify video.");
            json = """{"streams":[{"codec_type":"video"},{"codec_type":"video","side_data_list":[{"side_data_type":"Spherical Mapping","projection":"tiled equirectangular"}]}]}""";
            Check(VrVideoDetector.FromJson(json).IsVr, "Inspect all video tracks.");
            Check(VrVideoDetector.FromJson("""{"streams":[{"codec_type":"video","tags":{"GSpherical:ProjectionType":"equirectangular"}}]}""").IsVr, "Legacy projection tag.");
        });
        Test("VR sorting is opt-in, read-only, grouped, collision-safe and protected on repeat scans", () => {
            string root = Root();
            FixtureAt(root, "incoming/a.mp4", "spherical-v1.mp4");
            FixtureAt(root, "incoming/b.mp4", "spherical-v2.mp4");
            FixtureAt(root, "incoming/c.mkv", "spherical.mkv");
            FixtureAt(root, "incoming/plain.mp4", "untagged.mp4");
            FixtureAt(root, "incoming/stereo.mp4", "flat-stereo.mp4");
            Check(Scan(root).Items.All(i => i.Category == "video"), "Option defaults off.");
            var options = new ScanOptions(true, 2, DeleteEmptyFolders: true, SortVrVideos: true);
            var plan = Engine.Scan(root, options, null, CancellationToken.None);
            Check(plan.Items.Count(i => i.Category == "vr-video") == 3, "Three tagged VR videos.");
            Check(plan.Items.Count(i => i.Category == "video") == 2, "Untagged and stereo remain video.");
            Check(!Directory.Exists(Path.Combine(root, "vr-video")), "Read-only preview.");
            Check(plan.Items.Where(i => i.Category == "vr-video").All(i => i.Detection.Contains("projection")), "Reviewable metadata reason.");
            Check(plan.Items.GroupBy(i => Path.GetDirectoryName(i.Destination)).All(g => g.Count() <= 2), "VR grouping respects cap.");
            var result = Engine.Execute(plan, null, CancellationToken.None);
            Check(result.Moved == 5 && result.Failed == 0 && result.RemovedFolders == 1, "Moves and cleanup.");
            Check(Scan(root).Items.Count == 0 && Directory.Exists(Path.Combine(root, "vr-video")), "VR output protected even with option off.");
            FixtureAt(root, "new/a.mp4", "spherical-v1.mp4");
            FileAt(root, "vr-video/a.mp4", "keep");
            plan = Engine.Scan(root, new(false, SortVrVideos: true), null, CancellationToken.None);
            Check(plan.Items.Single().Destination.EndsWith("a (2).mp4"), "VR duplicate gets suffix.");
            root = Root(); FileAt(root, "vr-video");
            bool blocked = false;
            try { Engine.Scan(root, new(false, SortVrVideos: true), null, CancellationToken.None); }
            catch (IOException) { blocked = true; }
            Check(blocked, "Conflicting VR output file rejected when enabled.");
        });
        Test("probe failures and timeout keep video classification; cancellation discards plan", () => {
            string root = Root(), path = FileAt(root, "broken.mp4", "invalid media");
            string probe = VrVideoDetector.FindFfprobe();
            var result = VrVideoDetector.Probe(path, probe, CancellationToken.None);
            Check(!result.IsVr && result.Warning is not null, "Corrupt media reported.");
            var plan = Engine.Scan(root, new(false, SortVrVideos: true), null, CancellationToken.None);
            Check(plan.Items.Single().Category == "video" && plan.Warnings.Count > 0, "Corrupt video retained with note.");
            result = VrVideoDetector.Probe(path, Path.Combine(root, "missing-ffprobe.exe"), CancellationToken.None);
            Check(!result.IsVr && result.Warning is not null, "Cannot launch probe handled.");
            result = VrVideoDetector.Probe(Fixture("untagged.mp4"), probe, CancellationToken.None, TimeSpan.Zero);
            Check(!result.IsVr && result.Warning?.Contains("timed out") == true, "Probe deadline enforced.");
            using var cancel = new CancellationTokenSource();
            bool cancelled = false;
            try {
                Engine.Scan(root, new(false, SortVrVideos: true),
                    new DirectProgress(p => { if (p.Phase == "Probing") cancel.Cancel(); }), cancel.Token);
            } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Cancellation during metadata scan returns no partial plan.");
            string? oldPath = Environment.GetEnvironmentVariable("PATH");
            try {
                Environment.SetEnvironmentVariable("PATH", "");
                Check(Scan(root).Items.Count == 1, "No ffprobe required when VR sorting is off.");
                bool missing = false;
                try { Engine.Scan(root, new(false, SortVrVideos: true), null, CancellationToken.None); }
                catch (IOException ex) { missing = ex.Message.Contains("ffprobe"); }
                Check(missing, "Missing dependency gives actionable error.");
            } finally { Environment.SetEnvironmentVariable("PATH", oldPath); }
        });
    }

    private static void RunEngineTests() { CoreTests(); CleanupTests(); VrTests(); }
    private static void DeleteTestRoots()
    {
        foreach (var root in CreatedRoots) {
            string resolved = Path.GetFullPath(root);
            if (resolved.StartsWith(Path.GetFullPath(TestParent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved))
                Directory.Delete(resolved, true);
        }
    }
}
