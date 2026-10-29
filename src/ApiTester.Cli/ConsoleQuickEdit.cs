using System.Runtime.InteropServices;

namespace ApiTester.Cli;

/// <summary>Turns the console's QuickEdit mode off for a while and back on after.
/// <para>With QuickEdit on (the classic console's default), a click in the window starts a
/// selection, and until it ends (Enter, Esc, a right-click) every write to the console blocks. For
/// a long-running gateway that logs each request, a stray click froze the gateway and every page
/// relayed through it. Windows Terminal does not pause output on a selection, and there the mode
/// change is harmless.</para>
/// <para>The mode belongs to the console's input buffer, which is opened as <c>CONIN$</c>, so it is
/// found even when stdin is redirected (<c>&lt; nul</c>, a launcher's pipe).</para></summary>
internal static class ConsoleQuickEdit
{
    private const uint EnableQuickEditMode = 0x0040;
    private const uint EnableExtendedFlags = 0x0080;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>Suspends QuickEdit when <paramref name="console"/> is true and this is Windows with
    /// a console attached; otherwise does nothing. Dispose to put the original mode back.</summary>
    public static IDisposable SuspendWhile(bool console)
    {
        if (!console || !OperatingSystem.IsWindows()) return Nothing.Instance;
        IntPtr handle = InvalidHandle;
        try
        {
            handle = CreateFileW("CONIN$", GenericRead | GenericWrite, FileShareRead | FileShareWrite,
                                 IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle == InvalidHandle || handle == IntPtr.Zero) return Nothing.Instance;
            if (!GetConsoleMode(handle, out uint mode) || (mode & EnableQuickEditMode) == 0 ||
                !SetConsoleMode(handle, (mode & ~EnableQuickEditMode) | EnableExtendedFlags))
            {
                CloseHandle(handle);
                return Nothing.Instance;
            }
            return new Restore(handle, mode);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            if (handle != InvalidHandle && handle != IntPtr.Zero) CloseHandle(handle);
            return Nothing.Instance;
        }
    }

    private sealed class Restore(IntPtr handle, uint mode) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            try { SetConsoleMode(handle, mode | EnableExtendedFlags); } catch { /* console gone */ }
            try { CloseHandle(handle); } catch { }
        }
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();
        public void Dispose() { }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
