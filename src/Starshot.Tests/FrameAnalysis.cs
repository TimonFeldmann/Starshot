using System.Diagnostics;

namespace Starshot.Tests;

/// <summary>
/// Post-capture analysis: ffprobe metadata (with retries) and the source-rate 10-bit YUV
/// extraction that feeds the QuadClassifier (ffmpeg pipe, HDR10 colorimetry declared on
/// BOTH input and output so ffmpeg cannot EOTF-convert the HDR10 content).
/// </summary>
internal static class FrameAnalysis
{
    public sealed record VideoInfo(int Width, int Height, string PixFmt, string Trc);

    /// <summary>
    /// ffprobe stream metadata with retries (the file can be briefly locked right after
    /// capture). Throws when ffprobe yields no stream info after all attempts.
    /// </summary>
    public static async Task<VideoInfo> ProbeVideoAsync(string ffprobe, string file, string workDir)
    {
        for (int i = 0; i < 10; i++)
        {
            (int code, string outp, _) = await HdrTestEnvironment.RunProcessAsync(
                ffprobe,
                $"-v error -select_streams v:0 -show_entries stream=width,height,pix_fmt,color_transfer \"{file}\"",
                workDir,
                TimeSpan.FromSeconds(60)
            );
            var kv = new Dictionary<string, string>();
            foreach (string line in outp.Split('\n'))
            {
                string t = line.Trim();
                int eq = t.IndexOf('=');
                if (eq > 0)
                    kv[t[..eq]] = t[(eq + 1)..];
            }
            if (kv.ContainsKey("width") && kv.ContainsKey("height"))
            {
                return new VideoInfo(
                    int.Parse(kv["width"]),
                    int.Parse(kv["height"]),
                    kv.GetValueOrDefault("pix_fmt", ""),
                    kv.GetValueOrDefault("color_transfer", "")
                );
            }
            Thread.Sleep(500);
        }
        throw new InvalidOperationException($"ffprobe produced no stream info for {file}");
    }

    /// <summary>
    /// Stream the recording's planar 10-bit frames at its native (source) frame rate into a
    /// fresh classifier. No fps resampling: WGC delivers a sparse, jittery stream for static
    /// content (~2-6 fps), and resampling to 1 fps can alias an entire color phase away, so
    /// we classify every decoded frame (a few dozen per 28 s recording - trivial).
    /// </summary>
    public static async Task<QuadClassifier> ExtractAndClassifyAsync(
        string ffmpeg,
        string file,
        int width,
        int height,
        string workDir
    )
    {
        string args = string.Join(
            ' ',
            "-v error -nostdin",
            // Keep the HDR10 content on a pass-through path (identical input AND output
            // colorimetry; otherwise ffmpeg EOTF-converts PQ to SDR and the codes are gone).
            "-color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc -color_range pc",
            $"-i \"{file}\"",
            "-colorspace bt2020nc -color_primaries bt2020 -color_trc smpte2084 -color_range pc",
            "-f rawvideo -pix_fmt yuv420p10le -"
        );
        var psi = new ProcessStartInfo(ffmpeg, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workDir,
        };
        using var p = Process.Start(psi)!;
        Task<string> errTask = p.StandardError.ReadToEndAsync();
        var clf = new QuadClassifier(width, height);
        await clf.ReadFramesAsync(p.StandardOutput.BaseStream);
        bool done = p.WaitForExit(300_000);
        if (!done)
        {
            p.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"ffmpeg extraction timed out for {file}");
        }
        string err = await errTask;
        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                $"ffmpeg extraction failed for {file} (exit {p.ExitCode}):\n{HdrTestEnvironment.Tail(err)}"
            );
        if (clf.Frames == 0)
            throw new InvalidOperationException(
                $"ffmpeg produced no frames from {file}:\n{HdrTestEnvironment.Tail(err)}"
            );
        return clf;
    }
}
