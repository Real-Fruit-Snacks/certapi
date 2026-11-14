using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ApiTester.Cli;

/// <summary>A plain Win32 window and message loop on its own STA thread, with a synchronization
/// context that runs continuations on that thread — what WebView2 needs to live inside a console
/// program without WinForms or WPF (which would add the Windows Desktop runtime as a dependency).</summary>
internal sealed class Win32Host
{
    private const uint WM_APP_WORK = 0x8000 + 1;
    private const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000, WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
    private const int SW_SHOWNOACTIVATE = 4, SW_SHOW = 5;

    private static readonly WndProc Proc = WindowProc;   // kept alive for the life of the process
    private static ushort _class;

    public IntPtr Window { get; private set; }

    /// <summary>Run <paramref name="body"/> on a new STA thread that owns a window of the given client
    /// size, pumping messages until it finishes; returns its result or rethrows its exception.</summary>
    public static T Run<T>(int width, int height, bool visible, string title, Func<Win32Host, Task<T>> body)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var host = new Win32Host();
            var context = new LoopContext(GetCurrentThreadId());
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                host.Window = CreateHostWindow(width, height, visible, title);
                Task<T> task;
                try { task = body(host); }
                catch (Exception ex) { task = Task.FromException<T>(ex); }
                task.ContinueWith(_ => context.Post(_ => PostQuitMessage(0), null), TaskScheduler.Default);
                while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.hwnd == IntPtr.Zero && msg.message == WM_APP_WORK) { context.RunPending(); continue; }
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                    context.RunPending();
                }
                context.RunPending();
                if (task.IsFaulted) failure = task.Exception!.InnerException ?? task.Exception;
                else if (task.IsCanceled) failure = new OperationCanceledException();
                else result = task.Result;
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (host.Window != IntPtr.Zero) DestroyWindow(host.Window);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    private static IntPtr CreateHostWindow(int width, int height, bool visible, string title)
    {
        var module = GetModuleHandleW(null);
        if (_class == 0)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
                hInstance = module,
                lpszClassName = "certapi.browse"
            };
            _class = RegisterClassExW(ref wc);
            if (_class == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "could not register the browser window class");
        }
        // Hidden runs sit off-screen rather than unshown: a window that was never shown does not
        // paint, and a screenshot of it would come back blank.
        IntPtr hwnd = visible
            ? CreateWindowExW(0, "certapi.browse", title, WS_OVERLAPPEDWINDOW, 100, 100, width + 16, height + 39,
                IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero)
            : CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "certapi.browse", title, WS_POPUP, -32000, -32000,
                width, height, IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "could not create the browser window");
        ShowWindow(hwnd, visible ? SW_SHOW : SW_SHOWNOACTIVATE);
        return hwnd;
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam) =>
        DefWindowProcW(hwnd, msg, wParam, lParam);

    private sealed class LoopContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback, object?)> _work = new();
        private readonly uint _thread;

        public LoopContext(uint thread) => _thread = thread;

        public override void Post(SendOrPostCallback d, object? state)
        {
            _work.Enqueue((d, state));
            PostThreadMessageW(_thread, WM_APP_WORK, IntPtr.Zero, IntPtr.Zero);
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (GetCurrentThreadId() == _thread) { d(state); return; }
            using var done = new ManualResetEventSlim();
            Post(s => { try { d(s); } finally { done.Set(); } }, state);
            done.Wait();
        }

        public override SynchronizationContext CreateCopy() => this;

        public void RunPending()
        {
            while (_work.TryDequeue(out var item)) item.Item1(item.Item2);
        }
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int x, y;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint thread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
}
