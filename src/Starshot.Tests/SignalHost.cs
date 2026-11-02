using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Starshot.Tests;

/// <summary>
/// One C++ signal-host session. The host renders the flat HDR fields in a borderless D3D11
/// window covering the target panel: GREEN (opening phase, firstPhaseSeconds) then
/// RED/BLUE/YELLOW (periodSeconds each) - a long opening phase keeps GREEN visible through
/// a recording's WGC session-init blackout. It prints `window 0x..` to stdout (captured in
/// memory here) and atomically publishes `t0` (unix-ms + period) to a file, where t0 = the
/// first presented GREEN frame, so recordings can be aligned to the color phases. It
/// foregrounds its own window once at startup; re-foreground it right before each recording
/// (Foreground()). With maxSeconds > 0 the host also exits on its own after that runtime
/// (play-only runs). With pauseFlagFile, while that file exists the host freezes the color
/// phases + label on the current color (the window stays up, presenting the frozen frame).
/// </summary>
internal sealed class SignalHost : IDisposable
{
    private readonly Process _process;
    private readonly object _outLock;
    private readonly StringBuilder _stdout;
    private readonly StringBuilder _stderr;

    public long T0Ms { get; }

    public double Period { get; }

    public IntPtr WindowHandle { get; }

    public int Width { get; }

    public int Height { get; }

    public int Nits { get; }

    public string StdoutSoFar
    {
        get
        {
            lock (_outLock)
                return _stdout.ToString();
        }
    }

    public string StderrSoFar
    {
        get
        {
            lock (_outLock)
                return _stderr.ToString();
        }
    }

    private SignalHost(
        Process process,
        object outLock,
        StringBuilder stdout,
        StringBuilder stderr,
        long t0Ms,
        double period,
        IntPtr windowHandle,
        int width,
        int height,
        int nits
    )
    {
        _process = process;
        _outLock = outLock;
        _stdout = stdout;
        _stderr = stderr;
        T0Ms = t0Ms;
        Period = period;
        WindowHandle = windowHandle;
        Width = width;
        Height = height;
        Nits = nits;
    }

    public static async Task<SignalHost> StartAsync(
        string exe,
        int width,
        int height,
        int nits,
        string workDir,
        int firstPhaseSeconds = 10,
        int maxSeconds = 0,
        int periodSeconds = 6,
        string? pauseFlagFile = null
    )
    {
        // Args: W H period t0File nits firstPhaseSec maxSec [pauseFlagFile] (the host writes
        // t0 atomically as tmp + rename, once, at the first GREEN frame). A long opening
        // phase keeps GREEN visible through a recording's WGC session-init; maxSec = 0 runs
        // forever (the orchestrator kills the process), otherwise the host exits on its own
        // after maxSec of non-paused time. While pauseFlagFile exists, the host freezes the
        // color phases + label on the current color (window stays up, frozen frame).
        string t0File = Path.Combine(workDir, "t0.txt");
        if (File.Exists(t0File))
            File.Delete(t0File);

        var psi = new ProcessStartInfo(
            exe,
            $"{width} {height} {periodSeconds} \"{t0File}\" {nits} {firstPhaseSeconds} {maxSeconds}"
                + (pauseFlagFile is null ? string.Empty : $" \"{pauseFlagFile}\"")
        )
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        Process p = Process.Start(psi)!;
        // The host prints only a few lines (monitor list, window handle, one line per color
        // change), so capture them in memory - no file to contend with the poll loops.
        object outLock = new();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (outLock)
                stdout.AppendLine(e.Data);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (outLock)
                stderr.AppendLine(e.Data);
        };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        string stdoutNow()
        {
            lock (outLock)
                return stdout.ToString();
        }

        string stderrNow()
        {
            lock (outLock)
                return stderr.ToString();
        }

        // t0: well-formed `<unix-ms> <period>` (the host publishes it atomically).
        long t0Ms = 0;
        double period = 6;
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(t0File))
            {
                string raw;
                try
                {
                    raw = File.ReadAllText(t0File);
                }
                catch (IOException)
                {
                    // transient lock (e.g. EDR scanning a fresh file in TEMP): keep polling
                    await Task.Delay(200);
                    continue;
                }
                string[] toks = raw.Trim().Split(' ');
                if (
                    toks.Length >= 2
                    && Regex.IsMatch(toks[0], @"^\d{12,16}$")
                    && double.TryParse(toks[1], out double pd)
                )
                {
                    t0Ms = long.Parse(toks[0]);
                    period = pd;
                    break;
                }
            }
            if (p.HasExited)
                break;
            await Task.Delay(200);
        }
        if (t0Ms == 0)
        {
            p.Kill(entireProcessTree: true);
            throw new InvalidOperationException(
                $"signal host never published a well-formed t0 (exit {ExitText(p)}):\n{stdoutNow()}\n{stderrNow()}"
            );
        }

        // Window handle (for foregrounding before each recording).
        IntPtr hwnd = IntPtr.Zero;
        deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var m = Regex.Match(stdoutNow(), @"window 0x([0-9A-Fa-f]+)");
            if (m.Success)
            {
                hwnd = new IntPtr(
                    long.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber)
                );
                break;
            }
            if (p.HasExited)
                break;
            await Task.Delay(200);
        }
        if (hwnd == IntPtr.Zero)
        {
            p.Kill(entireProcessTree: true);
            throw new InvalidOperationException(
                $"signal host never printed its window handle (exit {ExitText(p)}):\n{stdoutNow()}\n{stderrNow()}"
            );
        }

        return new SignalHost(p, outLock, stdout, stderr, t0Ms, period, hwnd, width, height, nits);
    }

    /// <summary>Re-assert the host window as the foreground window (the recording's target).</summary>
    public void Foreground()
    {
        for (int i = 0; i < 5 && !Win32.ForegroundForce(WindowHandle); i++)
            Thread.Sleep(250);
    }

    /// <summary>Sleep until t0 + offset (t0 = the host's first GREEN frame).</summary>
    public void WaitUntil(double offsetSeconds)
    {
        DateTimeOffset target = DateTimeOffset
            .FromUnixTimeMilliseconds(T0Ms)
            .AddSeconds(offsetSeconds);
        while (DateTimeOffset.UtcNow < target)
            Thread.Sleep(50);
    }

    private static string ExitText(Process p) =>
        p.HasExited ? p.ExitCode.ToString() : "still running";

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }
        catch
        {
            // best effort
        }
        _process.Dispose();
    }
}
