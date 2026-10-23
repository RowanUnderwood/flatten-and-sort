using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace MediaFlattener;

public record VrDetection(bool IsVr, string Reason, string? Warning = null);

public static class VrVideoDetector
{
    // A bounded metadata probe, without scanning every frame or modifying the source.
    public static string FindFfprobe()
    {
        string executable = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
        var folders = new[] { AppContext.BaseDirectory }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        foreach (string folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            try
            {
                // Do not search the selected media folder or implicit current directory.
                string directory = folder.Trim().Trim('"');
                if (!Path.IsPathFullyQualified(directory)) continue;
                string candidate = Path.Combine(directory, executable);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        throw new IOException(OperatingSystem.IsWindows()
            ? "VR sorting requires ffprobe.exe (from FFmpeg). Place it beside MediaFlattener.exe or install FFmpeg on PATH, then scan again. You can also turn off VR sorting."
            : "VR sorting requires ffprobe (from FFmpeg). Install it with 'sudo apt install ffmpeg' or place it beside media-flattener, then scan again. You can also turn off VR sorting.");
    }

    public static VrDetection Probe(string path, string executable, CancellationToken token,
        TimeSpan? timeout = null) => ProbeAsync(path, executable, token, timeout ?? TimeSpan.FromSeconds(15))
            .GetAwaiter().GetResult();

    private static async Task<VrDetection> ProbeAsync(string path, string executable,
        CancellationToken token, TimeSpan timeout)
    {
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        using var process = new Process {
            StartInfo = new ProcessStartInfo(executable) {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        foreach (string argument in new[] {
            "-v", "warning", "-protocol_whitelist", "file", "-probesize", "5000000",
            "-analyzeduration", "5000000", "-select_streams", "v",
            "-show_streams", "-of", "json", "-i", Path.GetFullPath(path)
        }) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            process.Start();
            // Drain both pipes concurrently to prevent a full pipe from blocking the process.
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error);
            if (process.ExitCode != 0)
                return Unknown("Metadata could not be read; kept in video.");
            var result = FromJson(await output);
            if (!string.IsNullOrWhiteSpace(await error))
                return result with { Warning = "ffprobe reported metadata or read warnings. Review the classification in the preview." };
            return result;
        }
        catch (OperationCanceledException)
        {
            Stop(process);
            token.ThrowIfCancellationRequested();
            return Unknown("Metadata check timed out; kept in video.");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or JsonException or InvalidOperationException)
        {
            return Unknown($"Metadata unavailable ({ex.Message}); kept in video.");
        }
        finally { Stop(process); }
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    private static VrDetection Unknown(string reason) => new(false, reason, reason);
    private static string Text(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field)
            && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";

    public static VrDetection FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("streams", out var streams) ||
            streams.ValueKind != JsonValueKind.Array)
            return Unknown("No video streams could be inspected; kept in video.");
        bool hasVideo = false;
        foreach (var stream in streams.EnumerateArray())
        {
            if (Text(stream, "codec_type") != "video") continue;
            if (stream.TryGetProperty("disposition", out var disposition) &&
                disposition.ValueKind == JsonValueKind.Object &&
                disposition.TryGetProperty("attached_pic", out var attached) &&
                attached.ValueKind == JsonValueKind.Number && attached.TryGetInt32(out int isAttached) && isAttached == 1)
                continue;
            hasVideo = true;
            if (stream.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
                foreach (var data in sideData.EnumerateArray())
                    if (Text(data, "side_data_type").Equals("Spherical Mapping", StringComparison.OrdinalIgnoreCase))
                    {
                        string projection = Text(data, "projection");
                        if (IsProjection(projection))
                            return new(true, $"VR projection metadata: {projection}");
                    }
            if (stream.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
                foreach (var tag in tags.EnumerateObject())
                {
                    string name = tag.Name.Replace("_", "").Replace("-", "").ToLowerInvariant();
                    string value = tag.Value.ValueKind == JsonValueKind.String ? tag.Value.GetString() ?? "" : "";
                    if ((name is "projection" or "projectiontype" or "gspherical:projectiontype") && IsProjection(value))
                        return new(true, $"VR projection tag: {value}");
                    if (name == "gspherical:spherical" && value.Equals("true", StringComparison.OrdinalIgnoreCase))
                        return new(true, "Spherical video metadata");
                }
        }
        return hasVideo ? new(false, "No recognized VR projection metadata; kept in video.")
            : Unknown("No video streams could be inspected; kept in video.");
    }

    private static bool IsProjection(string value) => value.Trim().ToLowerInvariant() is
        "equirectangular" or "tiled equirectangular" or "equirectangular tile" or "cubemap" or "mesh";
}
