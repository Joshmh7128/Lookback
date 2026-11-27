using System.Runtime.InteropServices;

// lookback-testwindow <title> [seconds]: shows one 640x480 window without
// taking focus, prints "READY <hwnd>" on stdout, and closes when stdin closes
// or after [seconds] (default 120), so a crashed test never leaves it behind.
return TestWindow.Run(args);

internal static unsafe partial class TestWindow
{
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const uint WsOverlappedWindow = 0x00CF0000;
    private const int SwShowNoActivate = 4;

    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 1;
        }

        string title = args.Length > 0 ? args[0] : "Lookback test window";
        int seconds = args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 120;
        nint instance = GetModuleHandleW(null);
        fixed (char* cls = "LookbackTestWindow")
        fixed (char* text = title)
        {
            var wc = new WndClassEx
            {
                CbSize = (uint)sizeof(WndClassEx),
                LpfnWndProc = &WndProc,
                HInstance = instance,
                LpszClassName = cls,
            };
            if (RegisterClassExW(&wc) == 0)
            {
                return 2;
            }

            nint hwnd = CreateWindowExW(0, cls, text, WsOverlappedWindow, 100, 100, 640, 480, 0, 0, instance, 0);
            if (hwnd == 0)
            {
                return 3;
            }

            ShowWindow(hwnd, SwShowNoActivate);
            Console.Out.WriteLine($"READY {hwnd}");
            Console.Out.Flush();

            var close = new Thread(() =>
            {
                var stdin = new Thread(() =>
                {
                    try
                    {
                        Console.In.ReadToEnd();
                    }
                    catch (IOException)
                    {
                    }
                })
                { IsBackground = true };
                stdin.Start();
                stdin.Join(TimeSpan.FromSeconds(seconds));
                PostMessageW(hwnd, WmClose, 0, 0);
            })
            { IsBackground = true };
            close.Start();

            Msg msg;
            while (GetMessageW(&msg, 0, 0, 0) > 0)
            {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        }

        return 0;
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WmDestroy)
        {
            PostQuitMessage(0);
            return 0;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassEx
    {
        public uint CbSize;
        public uint Style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public nint HInstance;
        public nint HIcon;
        public nint HCursor;
        public nint HbrBackground;
        public char* LpszMenuName;
        public char* LpszClassName;
        public nint HIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);

    [LibraryImport("user32.dll")]
    private static partial ushort RegisterClassExW(WndClassEx* wc);

    [LibraryImport("user32.dll")]
    private static partial nint CreateWindowExW(uint exStyle, char* cls, char* title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hwnd, int cmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(Msg* msg, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(Msg* msg);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(Msg* msg);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int code);
}
