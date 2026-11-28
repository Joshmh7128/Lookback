using System.Runtime.InteropServices;

// lookback-testwindow <title> [seconds] [--action]: shows one 640x480 window
// without taking focus, prints "READY <hwnd>" on stdout, and closes when stdin
// closes or after [seconds] (default 120), so a crashed test never leaves it
// behind. With --action it plays a tiny "game" for the demo check: moving
// blocks, and every 20 s a white flash with a short tone (played by this
// process, so process loopback hears it), the bright and loud moment the
// clipper proposes as a candidate.
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
            if (args.Contains("--action"))
            {
                new Thread(() => Act(hwnd)) { IsBackground = true }.Start();
            }

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

    /// <summary>Paints the "game" at about 30 fps from its own thread: blocks moving over a dark field, a white flash with a tone every 20 s (far enough apart to be separate clips).</summary>
    private static void Act(nint hwnd)
    {
        byte[] tone = Tone(880, 0.35, 0.6);
        GCHandle pinned = GCHandle.Alloc(tone, GCHandleType.Pinned);
        var started = System.Diagnostics.Stopwatch.StartNew();
        bool flashing = false;
        while (true)
        {
            double t = started.Elapsed.TotalSeconds;
            bool flash = t % 20 is >= 5 and < 5.5;
            if (flash && !flashing)
            {
                PlaySoundW((byte*)pinned.AddrOfPinnedObject(), 0, SndMemory | SndAsync | SndNoDefault);
            }

            flashing = flash;
            nint dc = GetDC(hwnd);
            if (dc == 0)
            {
                return; // the window is gone
            }

            Fill(dc, 0, 0, 640, 480, flash ? 0xFFFFFFu : 0x302820u);
            if (!flash)
            {
                for (int i = 0; i < 4; i++)
                {
                    int x = (int)((t * (90 + (i * 37)) + (i * 150)) % 600);
                    int y = 40 + (i * 100);
                    Fill(dc, x, y, x + 60, y + 60, 0x40C0F0u >> (i * 2));
                }
            }

            _ = ReleaseDC(hwnd, dc);
            Thread.Sleep(33);
        }
    }

    private static void Fill(nint dc, int left, int top, int right, int bottom, uint bgr)
    {
        nint brush = CreateSolidBrush(bgr);
        var r = new Rect { Left = left, Top = top, Right = right, Bottom = bottom };
        _ = FillRect(dc, &r, brush);
        _ = DeleteObject(brush);
    }

    /// <summary>A 16-bit mono 48 kHz WAV of a sine tone, for PlaySound from memory.</summary>
    private static byte[] Tone(double hz, double seconds, double amplitude)
    {
        int n = (int)(48_000 * seconds);
        var wav = new byte[44 + (n * 2)];
        var w = new BinaryWriter(new MemoryStream(wav));
        w.Write("RIFF"u8);
        w.Write(36 + (n * 2));
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(48_000);
        w.Write(96_000);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(n * 2);
        for (int i = 0; i < n; i++)
        {
            double fade = Math.Min(1, Math.Min(i, n - i) / 480.0);
            w.Write((short)(short.MaxValue * amplitude * fade * Math.Sin(2 * Math.PI * hz * i / 48_000)));
        }

        return wav;
    }

    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndMemory = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hwnd, nint dc);

    [LibraryImport("user32.dll")]
    private static partial int FillRect(nint dc, Rect* rect, nint brush);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateSolidBrush(uint color);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);

    [LibraryImport("winmm.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySoundW(byte* sound, nint module, uint flags);

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
