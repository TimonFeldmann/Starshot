using System.Runtime.InteropServices;

namespace Starshot.Tests;

/// <summary>
/// user32/kernel32 bits for the "foreground the signal host right before each recording"
/// step (the window-fg capture path records the foreground window). Same AttachThreadInput
/// dance the orchestrator script proved: it bypasses the foreground lock for a background
/// process.
/// </summary>
internal static class Win32
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(
        uint idAttach,
        uint idAttachTo,
        [MarshalAs(UnmanagedType.Bool)] bool fAttach
    );

    /// <summary>
    /// SetForegroundWindow with the AttachThreadInput dance; reports whether the target is
    /// actually the foreground window afterwards.
    /// </summary>
    public static bool ForegroundForce(IntPtr handle)
    {
        IntPtr fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint myThread = GetCurrentThreadId();
        bool attached = false;
        if (fgThread != 0 && fgThread != myThread)
            attached = AttachThreadInput(myThread, fgThread, true);
        bool ok = SetForegroundWindow(handle);
        if (attached)
            AttachThreadInput(myThread, fgThread, false);
        return ok && GetForegroundWindow() == handle;
    }
}
