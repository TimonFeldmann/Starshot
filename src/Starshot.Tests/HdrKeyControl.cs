using System.Runtime.InteropServices;

namespace Starshot.Tests;

/// <summary>
/// Global keyboard control for the HDR suite while the user watches the panel (works with
/// any window focused - the keys are polled with GetAsyncKeyState, no focus needed):
/// ESC ends the suite (the active row is interrupted + cleaned up, and the remaining rows
/// skip), and Space toggles a pause - the active row FREEZES IN PLACE (the signal host
/// holds the current color with the window up, and the record-test child holds its duration
/// budget; see the pause-flag file) and resumes from where it stopped when Space is
/// pressed again.
///
/// The live suite state (running / paused / stopped) is published to
/// %TEMP%\starshot_hdr_suite_state.txt (atomic tmp + rename, best effort) so the console
/// wrapper (scripts\run-hdr-test.ps1) can print each pause instead of a paused suite
/// looking like a hung one.
/// </summary>
internal sealed class HdrKeyControl
{
    private const int VK_SPACE = 0x20;

    private const int VK_ESCAPE = 0x1B;

    /// <summary>Name of the state file under %TEMP% (polled by scripts\run-hdr-test.ps1).</summary>
    public const string StateFileName = "starshot_hdr_suite_state.txt";

    private readonly Thread _poller;

    private volatile bool _stop;

    private volatile bool _paused;

    private volatile bool _running = true;

    /// <summary>Latched on the first ESC press; once set, the suite ends.</summary>
    public bool StopRequested => _stop;

    /// <summary>True between a Space press (pause) and the next press (resume).</summary>
    public bool Paused => _paused;

    public HdrKeyControl()
    {
        _poller = new Thread(Poll) { IsBackground = true, Name = "hdr-key-control" };
        _poller.Start();
    }

    private void Poll()
    {
        WriteState("running");
        bool escHeld = false;
        bool spaceHeld = false;
        while (_running)
        {
            bool esc = IsKeyDown(VK_ESCAPE);
            bool space = IsKeyDown(VK_SPACE);
            if (esc && !escHeld)
            {
                _stop = true;
                WriteState("stopped");
            }
            // Edge-triggered: each Space press toggles. Once the suite is ending, Space
            // no longer toggles the pause (it would fight the stop).
            else if (space && !spaceHeld && !_stop)
            {
                _paused = !_paused;
                WriteState(_paused ? "paused" : "running");
            }
            escHeld = esc;
            spaceHeld = space;
            Thread.Sleep(100);
        }
    }

    /// <summary>Hold the test while paused, until Space resumes or ESC ends the suite.</summary>
    public async Task WaitWhilePausedAsync(CancellationToken outer = default)
    {
        while (_paused && !_stop && !outer.IsCancellationRequested)
            await Task.Delay(100);
    }

    /// <summary>
    /// Publish the suite state to the temp state file (atomic tmp + rename, best effort -
    /// a state-file failure must never break the suite).
    /// </summary>
    private static void WriteState(string state)
    {
        try
        {
            string final = Path.Combine(Path.GetTempPath(), StateFileName);
            File.WriteAllText(final + ".tmp", state + Environment.NewLine);
            File.Move(final + ".tmp", final, overwrite: true);
        }
        catch
        {
            // best effort
        }
    }

    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & short.MinValue) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
