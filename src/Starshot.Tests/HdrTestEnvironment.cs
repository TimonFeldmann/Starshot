using System.Diagnostics;

namespace Starshot.Tests;

/// <summary>
/// One-time provisioning of everything the HDR flat-field test needs on this machine:
/// the DEBUG app build (which also stages the encoder DLLs), the C++ signal host,
/// ffmpeg/ffprobe, and a live per-display HDR-state probe (`--hdr-probe`, the same API
/// source the recording's HDR decision uses). Anything that cannot be produced here sets
/// SkipReason instead of failing the test.
/// </summary>
internal sealed class HdrTestEnvironment
{
    public string RepoRoot { get; }

    public string StarshotExe => Path.Combine(RepoRoot, "build", "app", "Starshot.exe");

    public string SignalHostExe =>
        Path.Combine(RepoRoot, "tools", "HdrSignalHost", "hdr-signal.exe");

    public string FfmpegExe { get; }

    public string FfprobeExe { get; }

    /// <summary>Raw `--hdr-probe` output lines (one probe, shared by all test rows).</summary>
    public IReadOnlyList<string> HdrProbeLines { get; }

    /// <summary>Set when a prerequisite cannot be satisfied on this machine; the test skips.</summary>
    public string? SkipReason { get; private set; }

    private HdrTestEnvironment(
        string repoRoot,
        string ffmpeg,
        string ffprobe,
        IReadOnlyList<string> hdrProbeLines
    )
    {
        RepoRoot = repoRoot;
        FfmpegExe = ffmpeg;
        FfprobeExe = ffprobe;
        HdrProbeLines = hdrProbeLines;
    }

    public static async Task<HdrTestEnvironment> ProvisionAsync()
    {
        string repo = FindRepoRoot();

        // Coexistence with the user's app: the record-test child is headless (no UI/tray/
        // hotkeys) and the log sink is shared, so a running Starshot instance is left
        // ALIVE - the test then reuses build\app instead of rebuilding (a running app
        // locks build\app\Starshot.exe, MSB3027). A stale hdr-signal host is always
        // cleared (it would keep painting the panel); a stale Starshot instance is only
        // cleared when no user instance is running (it would block the build).
        bool userAppRunning = Process.GetProcessesByName("Starshot").Length > 0;
        KillByName("hdr-signal");
        if (!userAppRunning)
            KillByName("Starshot");
        await Task.Yield();

        string[] codecDlls =
        [
            "Starshot.Codecs.dll",
            "libx264-164.dll",
            "libx265.dll",
            "faac-2.dll",
        ];

        if (userAppRunning)
        {
            // No rebuild while the app is running: the record-test child loads the encoder
            // DLLs from build\app\codecs (staged only by an app build), so the existing
            // build must be complete.
            string starshotExe = Path.Combine(repo, "build", "app", "Starshot.exe");
            if (!File.Exists(starshotExe))
                return Unusable(
                    repo,
                    "Starshot is running (its build is locked) but build\\app\\Starshot.exe is missing; close the app and re-run"
                );
            string appCodecsDir = Path.Combine(repo, "build", "app", "codecs");
            if (codecDlls.Any(d => !File.Exists(Path.Combine(appCodecsDir, d))))
                return Unusable(
                    repo,
                    "Starshot is running (its build is locked) and build\\app\\codecs is incomplete; close the app and re-run to re-stage the encoder DLLs"
                );
        }
        else
        {
            // 1. Encoder DLLs (the app build copies them into build\app\codecs\).
            string codecsDir = Path.Combine(repo, "build", "codecs");
            if (codecDlls.Any(d => !File.Exists(Path.Combine(codecsDir, d))))
            {
                (int code, string outp, string err) = await RunPowershellScriptAsync(
                    repo,
                    Path.Combine(repo, "tools", "codecs", "build-codecs.ps1"),
                    TimeSpan.FromMinutes(5)
                );
                if (codecDlls.Any(d => !File.Exists(Path.Combine(codecsDir, d))))
                {
                    return Unusable(
                        repo,
                        $"encoder DLLs unavailable (build-codecs.ps1 exit {code}; it needs the VS C++ workload + local encoder sources):\n{Tail(outp + "\n" + err)}"
                    );
                }
            }

            // 2. App (DEBUG, x64) - always (re)built so the test exercises the current source.
            (int buildCode, string buildOut, string buildErr) = await RunProcessAsync(
                "dotnet",
                $"build \"{Path.Combine(repo, "src", "Starshot", "Starshot.csproj")}\" -c Debug -p:Platform=x64",
                repo,
                TimeSpan.FromMinutes(10)
            );
            if (buildCode != 0)
            {
                return Unusable(
                    repo,
                    $"app build failed (dotnet build exit {buildCode}):\n{Tail(buildOut + "\n" + buildErr)}"
                );
            }
        }

        // 3. C++ signal host (MSVC).
        if (!File.Exists(SignalHostPath(repo)))
        {
            (int hostCode, string hostOut, string hostErr) = await RunPowershellScriptAsync(
                repo,
                Path.Combine(repo, "tools", "HdrSignalHost", "build.ps1"),
                TimeSpan.FromMinutes(5)
            );
            if (!File.Exists(SignalHostPath(repo)))
            {
                return Unusable(
                    repo,
                    $"signal host build failed (exit {hostCode}; it needs the MSVC C++ workload):\n{Tail(hostOut + "\n" + hostErr)}"
                );
            }
        }

        // 4. ffmpeg / ffprobe (STARSHOT_TEST_FFMPEG_DIR, the repo-local tools\ffmpeg\bin
        //    copy, or PATH).
        (string? ffmpeg, string? ffprobe, string? missing) = LocateFfmpeg();
        if (ffmpeg is null || ffprobe is null)
        {
            return Unusable(repo, $"ffmpeg/ffprobe unavailable: {missing}");
        }

        // 5. Live per-display HDR state (the recording's own decision source).
        (int probeCode, string probeOut, string probeErr) = await RunProcessAsync(
            Path.Combine(repo, "build", "app", "Starshot.exe"),
            "--hdr-probe",
            repo,
            TimeSpan.FromSeconds(90)
        );
        var probeLines = probeOut
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToList();
        if (probeCode != 0 || !probeLines.Any(l => l.Contains("kind=HighDynamicRange")))
        {
            return Unusable(
                repo,
                $"no display is currently in HDR mode (Windows HDR off? probe exit {probeCode}):\n{string.Join("\n", probeLines)}\n{Tail(probeErr)}"
            );
        }

        return new HdrTestEnvironment(repo, ffmpeg, ffprobe, probeLines);
    }

    private static string SignalHostPath(string repo) =>
        Path.Combine(repo, "tools", "HdrSignalHost", "hdr-signal.exe");

    private static HdrTestEnvironment Unusable(string repo, string reason) =>
        new(repo, "", "", Array.Empty<string>()) { SkipReason = reason };

    /// <summary>Repo root = the directory containing Starshot.slnx (walk up from here and from the CWD).</summary>
    public static string FindRepoRoot()
    {
        foreach (
            string? start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() }
        )
        {
            for (DirectoryInfo? d = new DirectoryInfo(start); d is not null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "Starshot.slnx")))
                    return d.FullName;
            }
        }
        throw new InvalidOperationException(
            "Starshot.slnx not found; run the tests from inside the repo"
        );
    }

    public static void KillByName(string name)
    {
        foreach (Process p in Process.GetProcessesByName(name))
        {
            try
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
            catch
            {
                // best effort
            }
        }
    }

    /// <summary>
    /// Run a process with captured output; -1 exit code on timeout. (GUI-subsystem apps
    /// only emit console output through redirected pipes, which is exactly what this does.)
    /// </summary>
    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(
        string fileName,
        string arguments,
        string workingDir,
        TimeSpan timeout
    )
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDir,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        Task<string> outTask = p.StandardOutput.ReadToEndAsync();
        Task<string> errTask = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        bool done;
        try
        {
            await p.WaitForExitAsync(cts.Token);
            done = true;
        }
        catch (OperationCanceledException)
        {
            done = false;
        }
        if (!done)
        {
            try
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
            catch
            {
                // best effort
            }
        }
        return (done ? p.ExitCode : -1, await outTask, await errTask);
    }

    private static Task<(int, string, string)> RunPowershellScriptAsync(
        string repo,
        string scriptPath,
        TimeSpan timeout
    )
    {
        string pwsh =
            FindOnPath("pwsh")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"
            );
        return RunProcessAsync(
            pwsh,
            $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            repo,
            timeout
        );
    }

    /// <summary>
    /// Locate ffmpeg/ffprobe: STARSHOT_TEST_FFMPEG_DIR first (explicit override), then the
    /// repo-local tools\ffmpeg\bin copy (a gitignored local artifact - drop the binaries
    /// there once and no environment variable is ever needed), then PATH.
    /// </summary>
    public static (string? Ffmpeg, string? Ffprobe, string? Missing) LocateFfmpeg()
    {
        string? envDir = Environment.GetEnvironmentVariable("STARSHOT_TEST_FFMPEG_DIR");
        if (envDir is not null && Directory.Exists(envDir))
        {
            string envFf = Path.Combine(envDir, "ffmpeg.exe");
            string envFp = Path.Combine(envDir, "ffprobe.exe");
            if (File.Exists(envFf) && File.Exists(envFp))
                return (envFf, envFp, null);
            return (null, null, $"STARSHOT_TEST_FFMPEG_DIR={envDir} lacks ffmpeg.exe/ffprobe.exe");
        }
        try
        {
            string repoBin = Path.Combine(FindRepoRoot(), "tools", "ffmpeg", "bin");
            string repoFf = Path.Combine(repoBin, "ffmpeg.exe");
            string repoFp = Path.Combine(repoBin, "ffprobe.exe");
            if (File.Exists(repoFf) && File.Exists(repoFp))
                return (repoFf, repoFp, null);
        }
        catch (InvalidOperationException)
        {
            // no repo root - fall through to PATH
        }
        string? pathFf = FindOnPath("ffmpeg");
        string? pathFp = FindOnPath("ffprobe");
        if (pathFf is null || pathFp is null)
            return (
                null,
                null,
                "ffmpeg/ffprobe not found (set STARSHOT_TEST_FFMPEG_DIR, put them in "
                    + "tools\\ffmpeg\\bin, or add them to PATH)"
            );
        return (pathFf, pathFp, null);
    }

    public static string? FindOnPath(string exe)
    {
        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (pathVar is null)
            return null;
        string[] exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE").Split(';');
        foreach (string dir in pathVar.Split(Path.PathSeparator))
        {
            foreach (string ext in exts)
            {
                string cand = Path.Combine(dir.Trim(), exe + ext);
                if (File.Exists(cand))
                    return cand;
            }
        }
        return null;
    }

    public static string Tail(string s, int lines = 25)
    {
        string[] all = s.Split('\n');
        return string.Join("\n", all.TakeLast(lines));
    }
}
