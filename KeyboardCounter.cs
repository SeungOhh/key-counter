// KeyboardCounter - a live typing-speed widget
// Build: build.ps1 (uses the .NET Framework 4.8 csc.exe, no external dependencies)
//
// Design points (target: under 1% CPU)
//  - Keystrokes are tallied only in the WH_KEYBOARD_LL hook. With no events, no code runs.
//  - Repaint runs off a 200ms (5/sec) timer. The drawn area is tiny, so it costs almost nothing.
//  - If the content to draw matches the previous frame, Invalidate is skipped entirely.
//  - When fully idle (rate 0, graph all zeros) the timer slows to 1000ms. The hook (which runs
//    on the UI thread) restores 200ms the instant a key arrives, so nothing feels delayed.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KeyboardCounter
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            // Turn on DPI awareness before any window exists (Per-Monitor V2, falling back to System DPI).
            try
            {
                if (!SetProcessDpiAwarenessContext(new IntPtr(-4)))
                    SetProcessDPIAware();
            }
            catch
            {
                try { SetProcessDPIAware(); }
                catch { }
            }

            bool createdNew;
            using (Mutex mutex = new Mutex(true, "KeyboardCounterWidget_SingleInstance_v1", out createdNew))
            {
                if (!createdNew) return; // only one instance at a time
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new WidgetForm());
            }
        }
    }

    // -------------------------------------------------------------- settings file

    sealed class Config
    {
        // A separate flag marks "a position has been saved". Inferring it from the sign of the
        // coordinates would mistake the negative coordinates of a monitor placed left of / above
        // the primary one for "never saved".
        public bool HasPos;
        public int X;
        public int Y;
        public double Scale = 1.0;
        public bool TopMost = true;
        public int ResponseMs = 1200; // response time constant (tau). Smaller = snappier, larger = steadier.
        public int Opacity = 75;      // 0..100
        public long TodayStrokes;     // keystrokes accumulated today
        public int TodayDate;         // the day that total belongs to (yyyymmdd)

        long savedToday = -1;

        static int TodayKey()
        {
            DateTime d = DateTime.Now;
            return d.Year * 10000 + d.Month * 100 + d.Day;
        }

        // If the date rolled over, start today's count from zero again.
        public bool RollOverDayIfNeeded()
        {
            int today = TodayKey();
            if (TodayDate == today) return false;
            TodayDate = today;
            TodayStrokes = 0;
            savedToday = -1;
            Save();
            return true;
        }

        // Write only when the value actually changed. The caller decides how often to try.
        public void SaveStatsIfDirty()
        {
            if (TodayStrokes == savedToday) return;
            savedToday = TodayStrokes;
            Save();
        }

        static string Path
        {
            get
            {
                string dir = System.IO.Path.GetDirectoryName(Application.ExecutablePath);
                return System.IO.Path.Combine(dir, "KeyboardCounter.ini");
            }
        }

        public static Config Load()
        {
            Config c = new Config();
            try
            {
                if (!File.Exists(Path)) return c;
                foreach (string raw in File.ReadAllLines(Path))
                {
                    string line = raw.Trim();
                    int eq = line.IndexOf('=');
                    if (line.Length == 0 || line[0] == '#' || eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "has_pos": c.HasPos = val == "1"; break;
                        case "x": c.X = ParseInt(val, c.X); break;
                        case "y": c.Y = ParseInt(val, c.Y); break;
                        case "scale": c.Scale = ParseDouble(val, c.Scale); break;
                        case "topmost": c.TopMost = val == "1" || val.ToLowerInvariant() == "true"; break;
                        case "response_ms": c.ResponseMs = ParseInt(val, c.ResponseMs); break;
                        case "today_date": c.TodayDate = ParseInt(val, c.TodayDate); break;
                        case "today_strokes": c.TodayStrokes = ParseLong(val, c.TodayStrokes); break;
                        case "opacity": c.Opacity = ParseInt(val, c.Opacity); break;
                    }
                }
            }
            catch { }

            if (c.Scale < 0.5) c.Scale = 0.5;
            if (c.Scale > 3.0) c.Scale = 3.0;
            if (c.ResponseMs < 300) c.ResponseMs = 300;
            if (c.ResponseMs > 6000) c.ResponseMs = 6000;
            if (c.Opacity < 30) c.Opacity = 30;
            if (c.Opacity > 100) c.Opacity = 100;
            if (c.TodayStrokes < 0) c.TodayStrokes = 0;

            // If the stored total is from a previous day, drop it and start fresh.
            if (c.TodayDate != TodayKey())
            {
                c.TodayDate = TodayKey();
                c.TodayStrokes = 0;
            }
            c.savedToday = c.TodayStrokes;
            return c;
        }

        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# KeyboardCounter settings");
                sb.AppendLine("# response_ms: response time constant in ms. Shorter = snappier, longer = steadier.");
                sb.AppendLine("has_pos=" + (HasPos ? "1" : "0"));
                sb.AppendLine("x=" + X.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("y=" + Y.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("scale=" + Scale.ToString("0.##", CultureInfo.InvariantCulture));
                sb.AppendLine("topmost=" + (TopMost ? "1" : "0"));
                sb.AppendLine("response_ms=" + ResponseMs.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("opacity=" + Opacity.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("today_date=" + TodayDate.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("today_strokes=" + TodayStrokes.ToString(CultureInfo.InvariantCulture));
                File.WriteAllText(Path, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        static int ParseInt(string s, int fallback)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        static long ParseLong(string s, long fallback)
        {
            long v;
            return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        static double ParseDouble(string s, double fallback)
        {
            double v;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }
    }

    // ------------------------------------------------------------- rate estimator

    // Instantaneous typing-rate estimator based on exponential decay.
    //
    // Counting inside a fixed time window yields "keys in window x (60s / window length)", so the
    // displayed value can only ever land on multiples of that factor (a 3s window gives multiples
    // of 20). Weighting each keystroke by exp(-elapsed/tau) and accumulating instead makes the
    // value move continuously, with recent keystrokes counting for more.
    //
    // In steady state rate ~= (keys per second) * tau, so rate/tau*60000 is keys per minute.
    // The whole state is a single double: O(1) per keystroke, no buffer.
    sealed class RateMeter
    {
        double tau;          // time constant (ms)
        double rate;         // decayed accumulator
        long lastDecayMs;
        long lastStrokeMs = long.MinValue / 4;

        public RateMeter(int tauMs)
        {
            tau = tauMs;
        }

        public int TauMs
        {
            get { return (int)tau; }
            set
            {
                double next = value;
                if (next < 1) next = 1;
                // Rescale the accumulator so the displayed rate does not jump when tau changes.
                rate *= next / tau;
                tau = next;
            }
        }

        void Decay(long nowMs)
        {
            long dt = nowMs - lastDecayMs;
            if (dt <= 0) return;
            rate *= Math.Exp(-dt / tau);
            lastDecayMs = nowMs;
        }

        public void AddStroke(long nowMs)
        {
            Decay(nowMs);
            rate += 1.0;
            lastStrokeMs = nowMs;
        }

        // Keys per minute. Once input stops, close down to 0 over 3*tau so the exponential
        // tail does not linger on screen.
        public double Kpm(long nowMs)
        {
            Decay(nowMs);
            double gap = nowMs - lastStrokeMs;
            if (gap >= 3 * tau) return 0.0;

            double gate = 1.0;
            if (gap > tau) gate = 1.0 - (gap - tau) / (2 * tau);
            return rate * 60000.0 / tau * gate;
        }
    }

    // ------------------------------------------------------- keystrokes in the last hour

    // Ring buffer of one-minute buckets. Counts how many keys were actually pressed in the last 60 minutes.
    sealed class HourCounter
    {
        const long BucketMs = 60000;
        const int Slots = 64;
        readonly int[] counts = new int[Slots];
        readonly long[] stamps = new long[Slots];

        public void Add(long nowMs)
        {
            long s = nowMs / BucketMs;
            int i = (int)(s % Slots);
            if (stamps[i] != s) { stamps[i] = s; counts[i] = 0; }
            counts[i]++;
        }

        public int LastHour(long nowMs)
        {
            long s = nowMs / BucketMs;
            int sum = 0;
            for (int k = 0; k < 60; k++)
            {
                long want = s - k;
                int i = (int)(((want % Slots) + Slots) % Slots);
                if (stamps[i] == want) sum += counts[i];
            }
            return sum;
        }
    }

    // ------------------------------------------------------------ global keyboard hook

    sealed class KeyboardHook : IDisposable
    {
        delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x0100;
        const int WM_SYSKEYDOWN = 0x0104;
        const int WM_KEYUP = 0x0101;
        const int WM_SYSKEYUP = 0x0105;

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr GetModuleHandle(string lpModuleName);

        readonly HookProc proc;      // kept in a field so the GC cannot collect the delegate
        readonly bool[] isDown = new bool[256];
        IntPtr handle = IntPtr.Zero;

        public event Action KeyStroke;

        public KeyboardHook()
        {
            proc = Callback;
            handle = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException("Could not install the keyboard hook. (error " + Marshal.GetLastWin32Error() + ")");
        }

        // Keys excluded from the count: modifiers, lock keys and IME switching keys.
        static bool IsCountable(uint vk)
        {
            switch (vk)
            {
                case 0x10: case 0x11: case 0x12:                 // Shift, Ctrl, Alt
                case 0xA0: case 0xA1: case 0xA2:                 // L/R Shift, L Ctrl
                case 0xA3: case 0xA4: case 0xA5:                 // R Ctrl, L/R Alt
                case 0x5B: case 0x5C:                            // left/right Win
                case 0x14: case 0x90: case 0x91:                 // CapsLock, NumLock, ScrollLock
                case 0x15: case 0x17: case 0x18: case 0x19:      // Hangul/English, IME toggle, Hanja
                case 0x1C: case 0x1D: case 0x1E: case 0x1F:      // IME convert family
                    return false;
                default:
                    return true;
            }
        }

        IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN ||
                    msg == WM_KEYUP || msg == WM_SYSKEYUP)
                {
                    // vkCode is the first field of KBDLLHOOKSTRUCT. Reading it directly instead of
                    // marshalling the struct keeps every key event free of heap allocation.
                    uint vk = unchecked((uint)Marshal.ReadInt32(lParam));
                    if (vk < 256)
                    {
                        bool down = (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN);
                        if (down)
                        {
                            // Already held down means this is auto-repeat, so do not count it.
                            if (!isDown[vk])
                            {
                                isDown[vk] = true;
                                if (IsCountable(vk))
                                {
                                    Action h = KeyStroke;
                                    if (h != null) h();
                                }
                            }
                        }
                        else
                        {
                            isDown[vk] = false;
                        }
                    }
                }
            }
            return CallNextHookEx(handle, nCode, wParam, lParam);
        }

        // Windows silently drops a low-level hook from the chain if its callback ever exceeds
        // LowLevelHooksTimeout (300ms by default). The widget would then quietly stop counting and
        // never recover until restarted. Re-installing periodically lets it heal itself. A freshly
        // installed hook also takes the front of the chain, which helps when another program is
        // grabbing keys first.
        public void Rearm()
        {
            IntPtr fresh = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
            if (fresh == IntPtr.Zero) return;   // on failure, keep the existing hook

            IntPtr old = handle;
            handle = fresh;
            if (old != IntPtr.Zero) UnhookWindowsHookEx(old);

            // A key-up may have been missed across the swap, so clear the held-key state.
            Array.Clear(isDown, 0, isDown.Length);
        }

        public void Dispose()
        {
            if (handle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(handle);
                handle = IntPtr.Zero;
            }
        }
    }

    // ---------------------------------------------------------------- widget window

    sealed class WidgetForm : Form
    {
        // Pixel sizes at scale 1.0
        const float BASE_H = 71f;
        const float BASE_TOP_H = 54f;   // height of the top row (rate + graph + cat)
        const float BASE_PAD = 7f;
        const float BASE_ANIM_W = 60f;  // cat cell (30px of cropped sprite x 2)
        const float BASE_ANIM_GAP = 5f;
        const float BASE_NUM_W = 70f;   // four digits = 67.6px, 2.4px to spare
        const float BASE_GAP = 7f;
        const float BASE_GRAPH_W = 52f;
        const float BASE_GRAPH_H = 20f;
        const float BASE_FONT = 26f;
        const float BASE_SUB_FONT = 11f;
        const float BASE_RADIUS = 8f;
        const float BASE_BEZEL = 3f;    // inset of the bezel line inside the panel

        const int POINTS = 60;        // graph samples (200ms x 60 = 12 seconds)
        const int GRAPH_EVERY = 1;    // how many frames between graph samples
        const int ACTIVE_MS = 200;    // refresh period while active (5/sec)
        const int SPRITE_ZOOM = 2;      // sprite magnification (30x25 -> 60x50)
        const int SIT_FRAME = 9;        // rate is 0 (src/sitting.png)
        const int SLEEP_FRAME = 10;     // resting for a while (src/sleep.png)
        const long SLEEP_AFTER_MS = 10000; // no input for longer than this and the cat falls asleep
        const double FRAME_SEC = 0.5;   // how long one frame is held

        // Cycles through src/1.png .. 9.png in filename order.
        static readonly int[] FRAME_SEQ = { 0, 1, 2, 3, 4, 5, 6, 7, 8 };
        const int IDLE_MS = 1000;     // refresh period while idle
        const double MIN_SCALE_TOP = 300.0; // lowest the graph's Y axis ceiling can go (keys/min)

        readonly Config cfg;
        readonly RateMeter meter;
        readonly HourCounter hour = new HourCounter();
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly double[] history = new double[POINTS];
        readonly PointF[] pts = new PointF[POINTS];
        readonly GraphicsPath areaPath = new GraphicsPath();

        KeyboardHook hook;
        ContextMenuStrip menu;

        // The widget has no taskbar button (WS_EX_TOOLWINDOW), so once hidden there would be no
        // way back to it. The tray icon is that way back, and the way to quit while hidden.
        NotifyIcon tray;
        ContextMenuStrip trayMenu;
        Icon trayIcon;
        ToolStripMenuItem trayShowItem;

        // Rendering resources (rebuilt only when the scale or DPI changes)
        float scale = 1f;
        int dpi = 96;
        Font numFont, subFont;
        SolidBrush bgBrush, textBrush, subBrush, glowTextBrush;
        Pen borderPen, linePen, basePen, glowLinePen;
        LinearGradientBrush areaBrush;
        GraphicsPath borderPath;
        RectangleF graphRect;
        int accentKey = int.MinValue;
        readonly StringFormat rightFormat = new StringFormat(StringFormatFlags.NoWrap | StringFormatFlags.NoClip);
        readonly StringFormat leftFormat = new StringFormat(StringFormatFlags.NoWrap | StringFormatFlags.NoClip);

        // The cat sprite. Eleven 32x32 frames (9 running + sitting + sleeping) joined horizontally
        // into one PNG, embedded as base64 so the build stays a single exe. To change the artwork,
        // re-run the conversion and swap this string.
        // Source: src/1.png .. 9.png, sitting.png, sleep.png.
        const int SPRITE_W = 32;
        const int SPRITE_H = 32;

        // Every 32x32 tile has 7 empty pixel rows top and bottom and 2 empty columns either side.
        // Drawing only the smallest rectangle that contains all eleven frames keeps the cat the same
        // size while shrinking the widget by that much.
        // Do not compute this per frame: a shared rectangle is what preserves the up-and-down bounce.
        const int CROP_X = 1;
        const int CROP_Y = 3;
        const int CROP_W = 30;
        const int CROP_H = 25;

        static readonly string SPRITE_PNG =
            "iVBORw0KGgoAAAANSUhEUgAAAWAAAAAgCAYAAAAsTqKUAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqG" +
            "QAADPqSURBVHhe7Z0HVBRZ2vd7d2fMpKYbumloMph1TGMYMYw556w4KuYEiAoISo6Sc84KiBExZ1EMgIhiBDMoIjl2qP93brXOao0Czruzu+/7" +
            "8TunDlK3qi9ddZ/nPuleWaw22mijjTbaaKONNtpoo4022mijjTb+P6O9t8V8iwNhO73MJvUYymxso402fucfH482/o/ww/R+Kr1YLFY7ZsO/g7" +
            "ks1j9OR1ukQJwH4DleFxyGp9nMsczr/mK6sFgsBebJNtr4r0JZWe7qFsHtvF1qD3aOk5/AbLaboDDz6jbBWa/52sOZbW00z9+mduvcg3nyr0aP" +
            "xZI/m2B9rfjJEWQd93m4Yum0Acxr/mp6KLE0Xl/dC+AZUHcXqD+DM65TzzKv+xoayiy1VkwcPzJPfI7NqjHj8y4EvSi4lfAu2mODLbP9r2bJcA" +
            "PtEKeV61ZPGTiE2fbvINFl2ZaLh9xTndZNnMFsaw2TewmVnOd3Gke8GGYboUuXLhyLKZ36Mc//l/Efkb/vJXROO0uETwYSN+K5vUBsNVp+rdMU" +
            "5RnLjXgDLX/pZFK8WwAkLcThNfquzHv/a9Fjsdofi9gZcetC+MOMWLsAlqZxB+Y1reCrg681qKuzOl5McT756vEJZJ0KubllywpD5jV/FbuWDJ" +
            "qBshMAngDIxXnboU9YLFZn5nXfwnuu6vKj69kp8wYp9mG2EVymc2enr1U+bj6a153Z9okRLNYP6YGr96EwGHVRv6DB9kfcMGlf3UsoVGJe+zk7" +
            "x3H6P7DWrsmz5d+zmcAZwWzfOJI7LMtKcDVtlSCR2fY55+N35hDLG3gLlGXA/7euq5nX/FXMUWd1zEzdUwDcQUnhcaRE7bFkXtMcB1bxzK5bKK" +
            "VaT1QYzWz7pVcvpfS1HOd9y1W9mW2fWDNOv0/FozQAxaityEKSj4kp85qWOL5euK8puB/u2KrmGfdX7vp5W8gintETB81X2Tt7Ng3vyuJ/3vYv" +
            "QVW187oJHQcyTzP4gXnic/6T8ve9XN2kcAdxWyAK9IfUcwRqPFVQa8PG290qeGIqj/s7NVHmshKZW3scZN77X4vLquHT8eEsgDcAspG2Z1ww85" +
            "rmSDNRD8rdo1W0dw57GbPNfz5v7uE1/LTfftHjMts+seJXzSHiVxkA3gF4iAvuU7PJjMy8rjl+6sbSZLFYHZnnP8HnszjMcwST8V37lWaHAC+T" +
            "UOP7Ez6YdYDVWPZM5nXfoPONbT0/IGkNnrtqinxmsn9TYnUQDjJQ1WaxFBQjFig5Nvp1A+JXIXC+xhbmzV/Su/PllXLPG2z+BkmoGt56qWNxP0" +
            "6zVlP6Wrn9SN4GJK9GkRO3wWuioo3fZCVzh+mCpVsHy6/NNpOTInkx7trMeM1isb45qZ4JW3eJTD5NOb6ggvvh9BL5RyzW3FbH2Nb9qq9zdZsg" +
            "+9g6dlwXFo/xnue2O7iC7XZsjUqWpoKC4pdtLNYs/fY6724FA9L7QPkpVMXPgXXff4xhXvc1lvVrr/vcoQ+wzxQl7nqIXChvbf6L6gLvxToLfx" +
            "smmJi8rPMtRE3AS4fZmKzP0mHeT9i9oPtoyasjQGMecN8b+Tv1m4Z07qzCvO5bjOjTXuupYz8J9kcDiTNw11a5OHW+YuqJFcqpEfOUvY6v6Fgl" +
            "DpqExmArOE5vP555//+Ug2vU95f69MF5C95BNbmuyp+3bZ+h3+2ChTD5xAZhgbCZyfwb8vd35nXN8Wfl7/vQVHy0U6VO4jkX1U6D0OSrDcToQu" +
            "ovhMSJA+zlQ+SmggovIeIWqmQw7/6vxXH5kPGSF4eAl8loCBmAR5uVG1eM0iIPtEU2//Jj72KnwUCqNSp9usNlgoK9x2Susfl4wcjtw7tYlrl3" +
            "B2JXwWee3mzmvZ8Y30/If3rRuwZPIlHprIPX5vKwGM9vdQxn3Rh2t7tW+nV392g+tp+i8IUbuWW8odYNa/WjF0113ih8RQEQjjlMO11vq4qKne" +
            "2BRB2kr+GlMK/5GpZT/vFrpfdkIC4VTd6LUOrJQ46pcmO+ubL4ygb2u6trOuDu9oGoDXDBqY1dE5j3M4mc0dkJkRqo38tBtW1neE5SWsi85jM6" +
            "5FiqvUKKPypcXdHg3QuIVoPYXRlVzsp4Y6OEgh2dcW/nTDywnikyGcrSZX7AJxzMF4wuTlqGarMOaHKQQ5mXEMsHqLdkVf3O/hWKQTi0BUibg0" +
            "tmnJvbBnZY5DZJfuGGnxWnhc/pkoGIoZBEboL5qB+MmPd2785qdzlx21088Ebpdi6o3Z1w2UztFPO6rxE0m7VCGjoLkug01HrMARXOR62XABU2" +
            "XBTv4eHB1k7IsxyFQptFcJzYbiLzfoI6i9XxRpJpEfZPRdkaFsReXMSs0tjBvO5b+M7/+1JJ5ELUB0fjvcNMIEEb2K8LuHEBXzW8s1LAXeueeO" +
            "M0H/HG/E3M+z9ht2bi2Gsn/K/fOh95y89x/WZm+9fYOOzHPi8dBlFICAViJ+G6hfK98CnssP3GPH+7UUrbjhh3qUHoOEgCN8BqzLeV/9fkz3ys" +
            "+h/e1bf4n8pfa9k7ibOy0U0V0kAhEK4BhGsBoVpAlA4kAUJIg7Qg8RfSMvzCWb12Vn/+F97Iv5LZP8kPyDTnZqeu4Ecx2/4MP572XnRD7KKLih" +
            "2dgXgDJBqreTEv+hrxS9vZImY5RP5haPQYi/pgIaqsVVBiK8BjM2Xct+6Ody4mOLqhZ7OxxQiLcW7llhxU7ZYDEnWx7zdeOPOab7FvRUdPJG4B" +
            "Urbhkb2aNHFO54PHflNKS1isHLVvYadCadhw1PiuwxajH74aYzRisbrdWS8nRpIe6vYo4u5mxTrtn3uqfnnV3HZ8vgGHxVL/fZaPWfL3HQhegv" +
            "eOpih16gPE6gCphmhyUQF8+WhwUMY7bw08su2JyHn89C8/74+sH84xKtzUEbXOCkCsOtLXqcR8apvcl6O/td+P/Sb3UqEtud6q7bWf2WlQNR7L" +
            "UOHRG+JoIZCkAyTootaVDSpEAJE3BzVRQtyyEGBO1w5/CFF8TujIfwTDjw9RsCZgp4QUY9Uk5jXf4B9ZFlpPcCoVNUGukAZpoyFIA7V2XJTuVs" +
            "VjMznkWPXHe59tiFuivoF5M2HHb6P6XDNWqGxyUADidfHKSkU6x5BOijbLsVUdAhE4De9dFqLWXxuI0wb26aHRW4BGDz7EXmpoDFTDY0cNbB2h" +
            "sIp5/yfMhipOe76mPSSRWqCiNHHNhP2UxUKrPLDk5V08ETAEH1yMIIlRBxL1gP2GEIVood5TAJGfBhAtxIcQDThMUtrJvP8T5+N23gY+ACiHOD" +
            "8I9qPlFzCvYRI6/+8bETYN4ohEvLVZDkQIgUghJPYc1Dur4qmpHLIt+uONw3JELeI3G9r5Qv4S/r3y1yr4/TvdWKv4AmHqtOKlQrXpAxFaqPJR" +
            "AyJ1QIXpAFG6aAhQB2KFOLRO3YX5Md8BCanyvjzVWVVbrZ3BjF5KE/O3yVcjejzu7xhU21KOpVVM6sTq+2CjEpDQFXX2PNw3VakdNEhPnXkdk9" +
            "NrFU4jcByq3YZBFKkPJPeENNwAdY5qkPpqo8lXiPIgPfhM5zsw72XQ6dJapbeI1UOdrRJyzbilLNaEVsWVz2/hZyLJA2Vu1hAFdQeSdYG9qrQS" +
            "LN+jiAd7uqPQbgbC56ssYt77if3LeSn1O+RR46QCRGriyCLFA+T8z5q8bmmbNCIyVys+yV0tV5VnI3hxbJHcRb8pirsPGCtcE/vooz5IB0jUBB" +
            "WrByTpA9HaaAwQoilIE+T74LA2zpvzSGy5E7Pfz+GyuF2urlf6gBghmlzlkbOxc4XzWEWXTAvVvPtbO4uKdyvi8S7lptNLO971n9LpUoULF+Jw" +
            "NSBRHRRRPsT62qcOcr80RgfSKC1gvzZwUB1Ry7hhzP4+Z9IQLc28zRxxvaUiqFAtVDnyEDGLs4XF6sR3HK84PWG2osv+Se199htzPPbOVCEhGj" +
            "qeqampyXtso9fUGLQWtT69IY3TA1K6QRKijSZ3PiReAoiCBCjeqw27iXwrZr+f2DNKfmGdjzqqrdmgPHi4vE3tswlLyHefpjImcaby4rCl6uMn" +
            "duUYkLOn1ygfo0I1IIlUB+K0QEWTZ6AJxKhBGqENaSRRyjrAYXVkWvLPfNbdHzi5lnsN3jxUWihAFCRE/HzOJ4Oho/9stnHaUvnQ0/M7nc/bqX" +
            "LnrIlSWvi4DjuXDexidG4NOx+xGkC8Bqh4XVDxZAxoAXECIMEAVLw+kGQAHBbikbOgiLxjRtc0+1yWuqH0KGpTlgN7lHF8uXKzfy/hwPIOfggc" +
            "jveO01EfqAkqThfYZ4C6veqoc+WhyUMNVJQGij0FsB7D/uaz/8h/VP5awm4Cf2aTMxcIlyleWvnGaSNqJQ9rVy/H3d1EMXNR48uG2RIjJNhOwQ" +
            "1ThTzm57SWNGNO4qNdnOpk4y5Rc/twlycuZJ+4uU6hMn+DPFVoyQF8eGhyWoMqjwVY98uPq417yQ3+pbc2PS7/NCnGvMPS3SqocSEziAEureBe" +
            "YLEGd1xgKD/g6Hr13acWK568uY6Tdc1CLT1umuLuLUOUFt6z4FXSSiZRH0jsAST1AJINQUUbQhrTFVRMN+B4d9x30n7CYvGbVUD+c1Ssq7fLQx" +
            "QgBBWogYNL2OEkFrmkt9zPhzeqeZ80Vrx2YaVS1iVLjWMRs5W29ZVrp0/uy96uVtzkOx51AV0BIgCJ3SCN1EWNGx+Nvhq0Qq0M1YDvbO43Xctf" +
            "hSo6F5YrNCFCE1XWShD7qCFituLNOzaCKuLuNOzoAtJGuXIAe0WUe6ghfzuHFnwk6EAarUtbb+XB2vgQrC17HkQZEYvOTxvSaD34LNAwZvbLJG" +
            "o+O0CyqyPe2Skhz0IJklANIICLRmIVR2qgzrYjEKCKB9uVICZuWLwWqFgtUPFaKA9UwynHwaiN6QHECWll9NxHB9kBQ/DMTVA7xlCZVEx8k+Cx" +
            "8uEIVIfIVRWNjiq4toWDzM2q1fVe6mjcJgf4CYBQDZSZyiPfnFuVukT5oPOoLvsavAWQRmjQEwAVZwjs0wf2aYGK0YM0mjwHA+CYDrJ3C++wWJ" +
            "rfjEWnTpe/gr08SCN1IHLgIG4BJyRqnpLvfTtB1VsLJYjdeah2U0PBRkXJwRXKednbuOXE3USsNqgo0p8QmQ49kBsyFaJIAe1FVPjzcNp9DMpC" +
            "e8NrHncYs89PmA3j/vJklewdN7iooMJeBSGzlaIzTTn3EK4JqbUiyLNBmBBSG0X6umub2Xhup0IrPaL8yCGO1UTSjv64ELQEdRFqQJIGSvxUEW" +
            "Q5Fe/C+yNuGfs3Zt8yenc+a6JV1GjZAYgS4oG1Wv3n8fQlQ9SGBv6quClhOd89Yq6K286hiouPGivlkLEtjSVutx6oOPLcden3II3ShTRGVzYG" +
            "j+kgZ4/wIYvV/79W/lri0CJ5HwSrAREyS5cKI/FfPiymcMFqx8URlynAbSvUp5pgwJCBGNaDjdK9PKnbfP1WhzI/Qy7fTKscYWsAn774YM8B9i" +
            "gAPqqQ7lECPUYD1FHvooNSm964vqYLynYqIX+jovTMZtXbe8YqEk+v2cTnV5kqVNC+8hvnPSL1UGujgloPIdKWqTwudNWWwksDcOABCYaAgyrg" +
            "ro7rm1RR60esrh6g4ruDSuyOylAD5HgNhiS+FxBvCGmcPgr8B6A66if4LlSbyuyTQZfza7lFCNNArY0iPjjzkbyYfe+VlzbgrwbxbuKi6oBYKg" +
            "1W8ni4i18bMFMp7aWTOiVThPqg4roCSWQCIArIEFSsAf2zMUEHj5013ygrK8sxO/2E5yLtKZlL5WrFjhzkbmajzFkVSNZHqbk8aj3V0OCjjpJN" +
            "nehQw50dXLxx4dOKVjbQdVEZLMDKWcPgYr0Wb906AUlqKHCQx+Ili3HWfRwyt3Noq7o5+nbpws1Yoph911QBDUT5xmji3fYOEEdootJeAXVubL" +
            "z2UEfBDiUgURsUUT4xWkCKEK7zlDBs2iaEbOwLxCkDB/XguEQPk+atQb6dCpJMBJOZ/X1O/zH9FQ4vU057tEYeOWYclLqo0SGVais2qu25kEbp" +
            "oHRTF9S78oAobdRsk8PtDYoyyz9eH1QsmYT1kOeshTPORhCR3+M0URsqwHG7wSgnSnCO6jf/hrUD1H66s021CrZKeGungkJrLkAmFzcV1NhxIQ" +
            "rSRLmFIi3QhbYqKN8roCc4abQOEKONhmBVjBrcDb9OnoVHzjzgUD+csNCC/qDJSDU1xF0blWZLk2IXsEPrdrEh2c3GC3seXttygEOGqHNWRbkV" +
            "m37PpVvk6HEgDhPixlo51AQQ5acvs/yT9PDOjw/DrgYYNGw4XgToA8d/xXnb3uB1HYawNdq4u4e/j9nvJ4LHtndBmCaq7FTQ6MZDykrBzkW9+P" +
            "2vbFO7XbhZCU3kO4VrQmLDxgMLLp7t4dHWNRVvIHsHCTq452GIN2H96X9TcTqoD9fEdff+EMd0xd4F6i0ll5uVP8lfLH/NcXCRnCsCPyrgUC1Q" +
            "ASqgTi3B25uhOBTrgLePM/DsWiLyk5xx5UQQcs8GAdlmSDDmfjXs1Rzq6izBUyvDRoS6oGrXKCCID5EHHzXWChC581C3R1k2GQfyIfbn476pEh" +
            "rcVAEXDhDMR9MuBZwxUbph9LOAnqC+C6sZ2oMyl7FLSy1VkG2mSlvCiDXEu61sSKIMaev4w04uGiINccecB8R0pS1fKqEHkNYNXkt4GDh2IU5a" +
            "dQWO9MFLXz2MHj8F0WY/4/YObiizPyZbZuh3O7lQPr9oBwe5psTCJDO6PkotFCAhs6q7Gl6v7whxiBYa3XnIXi8HSRQZfIYfrS8DpJgZIsjeBK" +
            "V+RIFoItdBBdvWL8Vjnz6IWaYyjdnn54zpwesWMFkxLXeLEh1OaAoQ4p2pHG1RkL+hhjzoeD08sFBGA4mXxul9VMBaKPfnQVfIgSpfFWUn1wAX" +
            "LfE0ejWUNXSxYxoXrz0Fbwbp6ckz+2Sy41fOijpfHpCshzovFVQ6KkIaq433lh0hidTEB3ceyt3JZKgjs35jhMB+IQ7YDkH7Tlz42xkDpcmQZO" +
            "xF/F5TaGhpojRxCM5vF7SqusV2NGd3KZlkUwwhCtbG+63yoOL0UL2Hiw87FGmhr7ZRRpGZEp45EivvowKgFbEQc4000G/kbGTaCID0gbjjbIBe" +
            "QyfjoLkBblg1n1uYqaPUM2Uu+1DmekWpOEoL0lAtlJrKQxKpjXIbDqqdVOkxUbD94/MnoZZoHVARAkiSe2HnSiPMmDoC9W+OoPZCJJ6fCMTM2e" +
            "Nww2cYXrgJH7W0gsprpLxV5gr515dN5CgSxiFWZZm5Ah1OqtsrwIftirJn4cnHO3suRBHaoBKIAtYFFa0O8aFh2LpiApYvnoymimuoykzFi5NR" +
            "WL5iLm5HzcJLL723LJbqV8sct4xU/fnucjk0eaqBCtHC9RUKolwbQQOJ61ZZsoFEA3ywVEaDhwB3dqqiykeDPkcRDyNBD2+8VDFhwq/YtmExKr" +
            "y7AAe744Q5H+PmrcPFPd1x20rFj9knk++Vv5xWyp95K+XvW3hPYVvTlQ5hRPmqQpo8DtKSC0DFdUCajcJrMTg6pz8Orp8AquwiIM1DRUEqzGf0" +
            "68/8rBbpzmp3a6vwOeU8HOK9KgCJKQdqoMmVB4mPOiS+GqD81UH5qQNBGqj3FiDXlA2przokPgI07mEDkeq4vkapZISuuh7z41ukp7a2auQs9t" +
            "n6YB3gQG9U2QtQaa8GKr4bSs2U0RioA1GEAUodBDLLN6E7qFhDYL8+0p1/hY6+Pq6nuwBFaSg/7oUZc6ciducwfAjsVmXQpUsrylHmtguexY5v" +
            "itAB9ndFg58Q1W58UDH6KN7UGfU+GhCHaqNwYxc8tCYC+XEQktk3UYiFRjx04RsiN2o2cHEjMj3nQVGgh8tW6si15Xswe2OyZyL3N0moJj2YGn" +
            "wEKLdVhoQo4G0yRUDF6qLMURWNnxRwNHGBNYFrG3A2xREZaT4oeXMduSkBuH8sHFfOxqD0QRIqkydhquDH3sz+mJxZI3+ZuPAkGVnrwaGVsChE" +
            "HeUfEySNQeooJ/GweJkClkaqQZq1Hai8jtdPTqPi9VVkp4fi6K5VeHTjAKre3QTEl3HRafBJZl9f4+RqXgrpB/u60i4ksciIoJeZyaPRX4MOt5" +
            "TvVEKlmxpqfYkFaABpjB6oaC36mTmtN0KPnoZ4nR+PxtwUvD0ZgnkLpuBmwBg8ddN92lKJ2+KhGsM/eKlLkWKAGhceKnYT65s8f0WIgoW0Uihz" +
            "4qGWCECsDh3+kCb0hqQwAai9ATTcQlZGKBLXzcKZGDtAdIeu8c4JW1jQkgIm9Bao/vzMni/CAUOIwrRRZi4PKsEAlbYc1BCvKMkATaFaqPZSR7" +
            "W3LPFGRWtAemQcqHdnAfF9AI9wNcULCYvG4HigBYAiepVjXtzK4uZyAYkLuPvhxkexORuPbMkkbIAySw4d1xWFauPdNiV6LJbY89BE4qAkzkxC" +
            "D7EaKI/sgb49dTB0YDdIn8eh6bw/8pOd0eOnvrjkOhDPXUkYQq8Vcd1/kfxFz/lT8sfEwMCAc2cTu0SyW5FOLkr9uKCOzoG0/DakJedBvTuHul" +
            "enkR9uiedng4GqTEhrr+Fswu4WPc6v0bVrV+Vrq+RL4a9CW7q0svWXKWEqkIRnhECABhCiAQSpAf5cPLZUQqWngFbIUj91NLkSi1gRR9YKWozl" +
            "f40fcnZqPMTBnrR1+8FKBQ3+OhCFEndcGdJoQ1rxljmoQxRhCCR2BxVjAOl1S1oJSKpuoPhBBtItl+O463o0VtyiB+TjtE0Vg1isFi1AUgN821" +
            "pwHwe60i9WHK4LSaQeGvyFeGehACQaotJBBZWuaqj0IAkYMgAMQUVqgDoxFXcy3BEWYIfiF5nIOxCKvCNhuHwuAVRRCLIdu99kdsbk5FrOGewn" +
            "s7oBnUyr8VCDJFIH5bYcWvkSC7jCQ4D3znyZ2xWpDunFdZBW3gKacgDcR2a0HVKGdsdBp9V0bSXwCneOuxX3b2G57yBVBe2HVspN2EcqGnRo5U" +
            "sy+U1BAlQ5kySpDqQxWihz5KAxWB2IVoU0fQqkldlATR7QdBd1ZZm4sm4ujs4ywptHpL7zCV3nGmT7WyvcMWW5bBIG2C9zbcXh5L2T2K4+ynco" +
            "QhKtC3GYNqrsVWjLp9JNILN+Y7TpMSF5GALU3AYacvHwWhIOrJuBdG9ToCkfwGNcD12c01KN6QkTTqLs++uj1o2PWpLMi9BG2U42pFGyxFp9oB" +
            "DvHVXpygWEq0B6fiXEVdmQvr8ANNxEXqoXTk4ZifunggBRLkDlIsHH3JHZ19cImMFeQU/A+7tCEqOHWg9i5RvS1neDrzqtgElojTwLInSicG0g" +
            "mg8qxx7S+kegyDgQ5+Px0UBcmj8RhZdjAekjAE9xMGyXL7O/Lxg8p+O+KYoHL61UaKoiuYQ4fZR+VLo1bgLaCyD9l7sR5U/cfH060SY9YAQUH0" +
            "HxwwyUPLuAu5eTccRyFbIO+aHuw23aIrwbMbdZ5f8ZH+WPWLX/fvljEj5R3h1OyrRXIPYTQuwjgDhQD1SWPSTP0iB5dhB4lQ5x8UlQbzKA14fx" +
            "MHFVU68fWS1W0nyNDcP548t3KQPBRPEKaMWLYKJw1QE/FVA+bNR7cnDDrDMOmSigLmkkHrn0xZOtcqh15YHyE0C8l08r7+LdqphqoNii0fUFO4" +
            "3kponC9YH9MgVc7SKEONyAVsDlu1RpixdJPVEfoIcKd+IC6wKHf4W0PAtUVTZQn43Sh0dxbu4Y3InaA1APABQiJdjy97Kq5unEe+5OBEAWQ6Jn" +
            "13hDiEJ1UONJrG5D1HqqQxqlh/fOahCF68oSManDZW6J5BmA18g9Eohko544H2pNK0CgBKdDNrdYj3vVVOU+UklMUxZeoI9oXTTQ1p/MLZXG6u" +
            "ODqxqqSQIqnAfq9m5Ia+5AWnaJnoHL7h5Alt0mFOelApJ8QHwTkc5rrJl9MbEa2Wl81V4eXctI4ruSKE1IorXon0QJ0zHfeB06Jly9VxWN/hxI" +
            "D46AtOwqqOo8UJXZoGpzUXwzBa9vkL6J4D/DjSMuL1uzwm+mQefRJY586vfYIon5J8oEsdFfSMd5pbFkLJDKD2IJaqPOXxOScD6o49Mgqb4Dqo" +
            "pMRLl4mRmPy8ZzkH/EVzYJiG4jzHndWmafX8LtctdStYTU0lIxOrTiFZOYX4wOrfwo8vxjyaGHxhBN2gpu8udCdGQKRCWnIXl/AdKyi2h6ex4v" +
            "c1OBqusAcnD3mEPpeGGnVq1Ei5irtA3hpKyPfH99UMTNT5ApXHLQIQdS3ZAoO1frL0RjgArEp5dCWn4DVFUOqOocSCpvo+TRyY/voBAPzvp8mN" +
            "S9E6O06escMuFdQaostPNhF4cef8QKLrfh0n+XKFwH7534EJNKjygBJHmekNY9AuqItf8QueF7cGL8UNw7GwZQj+kxcDBidwizn6/zn5W/zxmh" +
            "J1B/aM6pgSMbpAaYJN+ocF2IvZXxPsMcKLsCaVEqJE/2QfokEZKiVEiLT0OS74cHVrwnVqMVml3M9DVCpnE3wEOFtmapAAEafdTwbLcCMjd2xB" +
            "PPXsCRpWhMN8WB4M24ccwT4vJM5AdMR7WDHKS+pPpLACpAAyJSs2yvhMCZStuYfTSH/HETXjpSu/8e25XGdgMVR47utBKmww7xsvaGIH3UegvR" +
            "FNMHkifxtCKQfsgEqm+h7OlpiMtvgiwzzT+598X84boazM6+AjdmgXJ0Iy3gHwcAPQhkMabff6eD+2Qg6qLCUwOiYD6QNhxUBRn8+bQ1WPviEv" +
            "JjXFH3KpNe5ffh8XGYrpwxiNkhk9MbVK/QCuCjAiYCTwtfFFEKupDGytqINdwQqIl6Hw7qE40gepoEydszMius4iqayq/S3136Nh0ng9eda40C" +
            "tBopP77KU1WWyY7Vpq1dKQkzkAFO/06y3lp02RlJvjWFCmhLuDHTEuJXRyApOU2/A9TlyCy/D+eQdXBP3gojtZ+YfTHx3zpxw11Lnff1ZKDHkf" +
            "csc29JzJMWQvI+SLIt3vBjzE+ffgfk2dT7C9AU2R3SwjhIyq+BqsiCtCILb/KPApJ7QM0VnI00JaVlzdZMmv/CntNIQgvxJLZLvrNM4dITH/2T" +
            "PANZ4o08f/I7Cc/UhRiisSAcTUUpaCpKhvjlIaD8DOqeJOKA95orZNEus69vsW0kf9hbR5Lg+qh842VJNipBD1KS5U/Qo8vNZM+CHHoQh6mh6e" +
            "gsND3dB/GzVIhfHoak+ARQk4mygiRcSbY8tWWSZl9mX98ia4fgJg7I+haFyapsyPgj4QCS8CReGFHCNT4aaApUgfjSJkgrc2nlj6ocNL67htc5" +
            "h4CGe7TlXXglGEtG9O7J7Ocr/EfkL8p+8arTxzzdfazmfJEoDJvQxZMk/RChC5GXANIQooBJIk4TL9218eiINaTvr4B6ex7S4rOQlpwFKq7h5X" +
            "kPIEgZ+ZacJ717j21R7j5n7WDFRRU2bIB4Qf6qaPBRQ5L1KER5b0Hpk8N4czYE2UkOkBSfAiR38OaCO6qdlIAANVBEAZNqoY8hCxK/z1ireojZ" +
            "x7eQz9yul91A3IqPyvf3GO9nSvfTv2W/d6eVc4OvGmrPmENSnEEPQlFhMvAuA6Jn+5HsvIQIQIurYJZN6Kd7N93xGWJ6o8aLZLhllhf5Sc+w0a" +
            "TW8ePL/9SWYIj6ACHe7zWE5MB4iHL2QkLiQu+vAaIC2cxL3UfRrbjGyN2LWrXCyHOG6gppEElwyKxgEucVh2ujyoNk5WUxYKIASJKGFNlLUwaj" +
            "KdsDoscJaHoch8aCCOBVKqiiRFyM23bJdc3IlS253Z8YpMdWv2uh3EiqB2glRCtgooy1aOVLJ90+KeYYUvsqhCREFZJHUZCUXoD4eSpEhfuBsu" +
            "OovBcNzw0jzVqxWQ8r3GnldtGbQ0B4d5TZcWTxxUTynImwk4mHfG8y6ZBDppRlWW/STipBtFHoqoXXV72Bt+mQvDgA8eujQPVlZKdZ1nqsMVrP" +
            "7JPJ3gmdVuZbcEokJMNMlG0UKacih9Y/j2hyyJ49/ZPU/sboQBSshvqbbpCUXYLo5RFaAYrfncF1t9G4tu5vD4+vV/RwWj9hTmsmQfKuzqzl3E" +
            "AUqe2VWbq0oo3ToyebxhCS8ZeNC6KQiQKWhPMgOjYHqM+H9N15iF8dg/TdKaA0Hc7LB5J38F1c3KiSRSpwyCRIx3nJc4/TRyPxBohh8KnyJE4P" +
            "TcFqaDwyC02FyRA9Owjxm5OgSi8Cohzg7Unkpjs+tln4U0sVSP8x+fPcOGKG9PkBOkxZkpuAzfMGfJwo+v94fTX7OSn7bHBWgZSMyXAd+qBIOC" +
            "DGEMWH1+PxlXA6Dix9cwpUyRnUFx3Fowx7IM8D2PczAmd3mcXsszn8pij7PVzXCbX2ikCoIXCfhNWuA7XX0fg8HQ8yI/Hybiqod+dRmROOm2Za" +
            "gC+HtpgRpE7HhkkY4rUtF2ThyK2tnPvMPv6A3frpv969EpaDo+NRYc9BYygpIO9BK1iS3aZnP1rpdvt4fFTARDkTRR2ri1fBw1BWdAaoyJTNDi" +
            "QWnLgEWRvb3x7DYgmZfTI5GmKaQNaji9NmQOxKvrysnpZkX0Upo4AjY+lEjySUj0a61rYrqPiukITx8cR3IO6eC0Xds3Tg/VlaCb28tLf82kHn" +
            "zHjX32xGKLO+Y1li93bHV3LTEapOr7QRBavjhWVnvPAZSP89TYECmRVGLOEYkqzrR4ceqOrbQMkJ4EUMRAVBSHZeEMT85NaQskwpEiEqQLRMCd" +
            "NHrBBUJBf1PsoQh5H6X5mFXOXOwbu9hqBeHgBVfQeoJwmgJ6h4fRaJfjtaKr6nmdxHXfD0ZjwF6i7E4f1R76SEShc+vZqLJLw+7NVCU3QPIIIH" +
            "itTXRmtCHK4pS4jRFiIZG4ZoClPFw2Aj5F0MR3lROlB9FQ8TjfEwZHTjgUBzJ2a/n1BjsZTP77dLq08YB7GrHMpd+RBHfFxEQY5wEuYRQBqlKb" +
            "OA6bIzWYhCpox1IQ1VQc1RY0grrkP6/iJQfwMFx+3xyFIB2McHcrZCWn8blw84X9FsZl+MT2wZo9ntjrlyMV17GqlFx4JJ6IEkRl86cfBijxJe" +
            "k/pQUvNOLOIIDp556qL69WWg8T7QeA8QZ+NKwvas1iT+mMTOV3YgJWf0JEhPdLJa+3I/Tbzcw/vomXycAKP5wLmltAUsKT4N0TNi/Weg+GawJG" +
            "z7RJPm9gH5nL9U/vjflr+zgUvi0JQFkNBVYSQCV/Wg95QxG8YeW+2qRlc+NLiqQhIopEMgtZ58+p2gIIy28F/fisX7O4nA2zPA+/MouhqKD/dT" +
            "AaoQkvxA2BmxvroM/VukL+fuk3ir4u2OLihz1kPprTB8yImE6GECqu9Gozo3DE2XbPEwaBoumurh4NwOKDJXQKmdCkodVPBkJxtZGxTwZLMcEC" +
            "pA1kYlUn3zbVw3jBtbU3hIth1i+hJIndkQ79WgzX4qRBeSpFFA6gA6008fdNkVcY300RhEll6SWHFPSMJ4eBQ8Fo0fbtJxz1dXAvDGVQtI00Nh" +
            "6MjKo/FubpYrp37TFUzzNvFERQZeHd+J4yvZl+/uUK3Os+DUnVjaEdcj1gAlx4BnIcDppXhly0NjINl6ThdIGwLq2T6UPDqB/KsJeHY7Dk9O2C" +
            "HBeTnJQH5/QTRN93ax8zkB5XbKqPcW4pb3VNw8ZAc89MBLe200eLFlCbkIAV646QOvDgKVJ0A9jkDeYatS59W/frfV84kR/Q04Z9awzze5K6PJ" +
            "RxUl9gp4EjASyN0GUfIoPLdSovsllQqiICXctxei9IojJEVxqChIwPXErY02v41uqebzd6b9pKF255iDCGUHkeU3+/V1k3Yfim3YeG7JxfXVnX" +
            "E/biVQdhjIsQWVPgU4PBJN4YZ4aM5FmTOHtpDpOtCDv9CWf/nLc7hzIQz3IxeiPGkq7QKj7jp8t9IW6BeMVWV1vrTfNock6HDPDyI3NigvVdqd" +
            "IwtPar0FqIoxAg4MARWhASpMBeJgLt4Tly9aln+gPZFQDgqjpoAiZUnVZ/D+qise22gAXoqyfQNKyfjORe4h2wadFhKhnxiszxYcXcEJuredUy" +
            "3ay0NTEB/YZ4i3CWNx23UYcqz0UeykLPvup+eiMssF9875oarwIN5keiLNfupLhVZ4fl9jxsCuylc3cF/Ty4vJRE/yDjHaeOeohKzVnT9WP5BQ" +
            "mCaaApVReWipbPKtywXEt/HkWlij06bZ85if2xyf5O/1v1n+rkasSMNDTzRFDgflzEbMHHl62XTUFDk32CtB7KdOhx1IDTAJudwy6YCCXTqoe3" +
            "oAeHsary7uRcnNSKD0PFB6DsW3Y1BwyBJvs0PxNGI2Qid3iG6tB0pwmSZYRq/kC1GF+OIOiF6fRk3BPuTGr0NJph8qciNQ4PIzHqaaofzpcZRf" +
            "sMVrKzncX9sZRTuUUbhVHlUOXHpzIOxWQNRsJdL/t7FbZTT8zWVP4NU+ZIavvZ++XCk1f4fam6zNvGy/yfI2pyPNEivv+FHIswfOLwEOjgRSh4" +
            "BK6o3nVlxUe2vQWWIk90NlujEKMpxRcNwO97YJgVA1+jyqztAK/rDfxuZqEf8ebjHO3Gvb7OnkF+WOHdUSXIxP1sWPQ+HGDkjfNgA5RxyqqOfJ" +
            "kN7xRGHwWLxz49GKAaJHAFWEt3n7ccv1F6DqAiC9h73bZn1zE6DWkGyiFY6SJNqVen09GGG75uZdj90koQ5OBRWohcd2OrjgPgVZQYtw3Nf4pM" +
            "fGCSTc0OrdtJojcaGSQ8VuNvLX/QMXTXVx5/AuvLzqjty4lXi8SwUI4wBn50P8LAlX9+/C5cC5KHYWIGgCq5XJln+ydVrPAeE2U1f/8nO//lfi" +
            "tr584jsCBes74d7yTohfbvD82gHrR9UFMcD7o/REgxeRwAM/PItfjKI9QjS6KAA5NgD1hJ58a55nwHnkD6eLUjdUQpSJ4qwAOJoY/cESmWaorP" +
            "b0lANQewy1dwNw0vrns2lL5SMyTXmnz69VSfOYrmZ+NmJL5odb3pDmuQHXNgOXTfA8cAReOJFYsRaoCD69/LogeBRKb/jgRtKW+87zum4Nm9nR" +
            "M2UlP+DICs6Fa1Eri24kbi3wWDfymwtBvsUv+io619d3KcShUbQSourv4e3j47gSaoz0TbqoTTemKzxI+Vlm7DoUZWxHZeYeRO+Y8M29H1rDnL" +
            "7C7hkrVU++3KWCWncVVHtqoCh4IpJMDFC8R1FmHR8ehrveP6Pksjuq8oLx7pIjjnktzZvWrVOLMf+v8B+Rv9jfdK0ltvKot2oPBAtwb7vKcxbL" +
            "UO6ciVI0fHlocueBCtGkE3DiQC3UuqmhaHMHPHAbgJzE9XiY4QhJyVlQxadlYYi3Z/EmMwD5R/eg/shSwEcBOwexmtvYisHcf+yb0yWJeIF4ew" +
            "BovAV8OInKK05A/WXgzRG8zAyik+0kCYgcD7oUTWTPhoSUh/qqo26XAl2idm25fMN4Q+WWt/fcPqP7oFCz0XP/Wabyz2WLS3u065a5rav4YeJa" +
            "VGcHAc/igJL9wOsoINcFD7xGQxyoA7xIoGsdi/NTpGvGdV0WOan9yhsb2Lff+P8CvD+JF5e9qUibL4PsLXE7ZedjvItBlv2Aa5u0WGTTa57X1k" +
            "nzMsI2+acEbghb/auOR7jlFNfCs861qL+Ei/FWWfttp/i/uOYjvnXA5oXpnJ++vxCawclYm4gn13xEJwLXHicz+vHQLcnvcwNwZu/ceyd8jd8/" +
            "zbASWY6QX8e8719B8jK2CyIMgCdBqHp1BjkHLJGfsglev/U58cBryAvZPsaleHkjAuJbe5DvY3Rrci+Fb2492BI75g00Aiklex2JrF09HwVMVN" +
            "z4MX7fYfv8AROTnBfYnQ1fk34lwTzz+n6LzDNxFvFbZw8y3T2k0+qjDlP3PT3rXFf3IBJX4kxpt2vxtKGGh7wW2G2drP/NLSbDtk+ddyLIOMp1" +
            "/eg/WMgEq2F/X1Hg2BW3/Kfi3mFLPLvogYpcP5zzXYAHlvLAxfmgio+i9vkh+JtPIXtefK3WlZz77lDAJ8yH/Njz3gGzZ6i5CCAfjc+P4HacCW" +
            "pvu+Fk8Jrqexl73h7zWZpxOsA4/9FJ61eprotb2vuk1bhPUHBoDOkOvCX7Fr9E2eNjyPcaDZSk0uGmF1kRyEnaiKLjFohc2ev5v8oAIHySv+t/" +
            "ofytG6I29I058Wh0UO/IBTxVcM2Mf+/gvM73ydJ3kMUuwRqgQsguaDp0XPWVszoKduuj7rYPHfL8pHw/HbQ1TLyBc+tR4tsVOekuZenxdgEtJY" +
            "E/Z90gucnpHvMTco/Y3M/wXnzGan4vz+vRJnfSXOdfvRS5vhoNF4DqG8C5tYCvEsR2SoATG2TSEO/ogux1ih/WGgn+YHR8N2leK91xaz2e7eDB" +
            "aTwnJnDX/B2Xorck30q1Op99yPZMnOMil+RtRuF4cxCQ3ATeHUeQ1bzfZz7zcbr9w3dOMN4wXve7S0J2rxrb18dy1koWC826MiYTe/UKt56+pq" +
            "sci94TdZxue/Li/5T79zUMWCztz37tZDxWn2S0f5g5YpD6uO5/YrXLd3DIbY5vzdN9AO7hzgmvUqdVw4iVzfKwWjL15a1QFF4JQrTtnPhYmxmu" +
            "rIFLv9gT9k/wQ8TOaWZJTgt2sTSNv/v5jVBlae2e141sZchmtv1ZPKer/PrYVg1U+nxUXLXDq4xtcFw13G3t0mk/2YzuOPX1ZTcRQEodc3Eq0q" +
            "LZzef/Z8iz42ynmGaEmoT5bhi+wX/r6FWBpmMtf1Kk98L9tMkOcXW/uS/un2W/7RSnFzfDGhuLUpGTtqvymN/Kp9UFiSi/G4tjgeueZ8WurbgV" +
            "vzHXceu875ax5vh3yd9pE+5BuLDRYKcMabAWvaz3zgZ51Dpw6fpf2gImR7AanvoPxovseBTfjsWDo7sheZUuq4L4XQGfBvX2HMQvjqEqdizqH+" +
            "0H3mdAmhuItQuM/oxn8Ae2zPrJ6ErUmlvPzjlLXgcOx3PzzihYI4c76xXrL6xk3wucqOiiqMhr1Za+LeK+eULvExGbUohgMts+J9pm5tYz4etS" +
            "I7ZPMxus/q8fhP8/YzV/wMR4x/nW83/5csclsmn7vL5f/984/i+xZVI3o8tR608VnbQquRC8/Pz0EX1+F263LdOm5R3d9fRmkult7/VjW72P7f" +
            "82puq21/NdN3TUGD6d0G5nPa+XkcV0erMZ4q0SxdeqLTT/GxncvTv77AKF2yAlmO5cWgE/Nmfjlbkc4KEKKUkIh2tD7MdF6WUX4MUxIMsJzzLs" +
            "8f6SC/DyCKRvTtJlaDILOAOv01ajNHEWUHgAuGyJu9ErXk6f/s9x869gTLd2+otGqPWd3ofbd+5Abl9d3VaV2rbRxv9avlVG1mrXso3/Vvp3il" +
            "/Msb5irPD0pQUb77Yp4pKxHJrs2YAnh94ESOzBBq7uhOROAKgEE7w5sBkfTlsA+V7AAxIejaYThaUnzPByhzKdL0CuO15FGNd57p77C7PHNtpo" +
            "o402vmBC+3Wj+P1dJ8qPXd6v8+jQ+ao775spF721VITYphOK3X+ic084a4k7QfPxYv9ilB7fiBeHN0gLkla+OuszL23zWMGSqPF/c8lyMbqaFb" +
            "/2tLXJqKHMXtpoo4022mgVgztuGCo/IGhi50Vb+7AWx1iOdj8VvjJs/axeRibjdfttmCLsN6t/F1Jr3Jq9Ltpoo4022mijjTbaaKONNtpoo402" +
            "2mijjTbaaOMv5f8BU+5PuvmVBboAAAAASUVORK5CYII=";

        static Bitmap sprite;
        static ImageAttributes spriteAttrs;

        static void LoadSprite()
        {
            if (sprite != null) return;
            try
            {
                byte[] png = Convert.FromBase64String(SPRITE_PNG);
                using (MemoryStream ms = new MemoryStream(png))
                using (Image img = Image.FromStream(ms))
                    sprite = new Bitmap(img);   // detach from the stream so later draws stay safe

                spriteAttrs = new ImageAttributes();
                spriteAttrs.SetWrapMode(WrapMode.TileFlipXY);
            }
            catch
            {
                sprite = null;
            }
        }

        double graphTop = MIN_SCALE_TOP;
        double graphValue;
        int graphTick;
        int displayKpm;          // painted before the first tick, so it must start at 0
        int hourStrokes;
        int frameIndex;      // position within the running cycle
        int spriteFrame;     // the sheet frame actually drawn (running / sitting / asleep)
        int paintedFrame = -1;
        long lastKeyMs = -100000;
        long lastTickMs;
        long lastStatsMs;
        long lastRearmMs;
        double animAccum;

        // What was actually painted last time (identical means skip the repaint)
        int paintedKpm = int.MinValue;
        double paintedSum = double.NaN;
        int paintedHour = int.MinValue;
        long paintedToday = long.MinValue;

        public WidgetForm()
        {
            cfg = Config.Load();
            meter = new RateMeter(cfg.ResponseMs);
            LoadSprite();

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = cfg.TopMost;
            Text = "Typing Speed";
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);

            rightFormat.Alignment = StringAlignment.Far;
            rightFormat.LineAlignment = StringAlignment.Center;
            leftFormat.Alignment = StringAlignment.Near;
            leftFormat.LineAlignment = StringAlignment.Center;

            BuildMenu();
            BuildTray();

            dpi = GetDpi();
            ApplyMetrics();
            RestorePosition();
            Opacity = cfg.Opacity / 100.0;

            timer.Interval = IDLE_MS;
            timer.Tick += OnTick;
            timer.Start();

            MouseDown += OnMouseDown;
            MouseUp += OnMouseUp;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (hook != null) return;
            try
            {
                hook = new KeyboardHook();
                hook.KeyStroke += OnKeyStroke;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "KeyboardCounter", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        // A tool window that shows without activating: clicking it never steals focus from
        // whatever the user is actually working in.
        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TOOLWINDOW = 0x00000080;
                const int WS_EX_NOACTIVATE = 0x08000000;
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        // ------------------------------------------------------------- input / update

        void OnKeyStroke()
        {
            // The hook callback runs on the thread that installed it (the UI thread), so touching
            // this state directly is safe.
            long now = clock.ElapsedMilliseconds;
            meter.AddStroke(now);
            hour.Add(now);
            cfg.TodayStrokes++;
            lastKeyMs = now;
            if (timer.Interval != ACTIVE_MS)
                timer.Interval = ACTIVE_MS; // if it was idling, jump straight back to fast updates
        }

        void OnTick(object sender, EventArgs e)
        {
            long now = clock.ElapsedMilliseconds;
            double kpm = meter.Kpm(now);

            // The legs advance one frame every 0.5s regardless of typing speed. Tying the rate to
            // the typing speed makes it outrun the 5/sec repaint, and skipped frames look jerky.
            double dt = (now - lastTickMs) / 1000.0;
            lastTickMs = now;
            if (dt > 1.0) dt = 1.0;
            if (kpm > 0)
            {
                animAccum += dt;
                while (animAccum >= FRAME_SEC)
                {
                    animAccum -= FRAME_SEC;
                    frameIndex = (frameIndex + 1) % FRAME_SEQ.Length;
                }
            }
            else
            {
                animAccum = 0;
                frameIndex = 0;
            }

            // running / sitting / asleep after a long rest
            if (kpm > 0) spriteFrame = FRAME_SEQ[frameIndex];
            else if (now - lastKeyMs >= SLEEP_AFTER_MS) spriteFrame = SLEEP_FRAME;
            else spriteFrame = SIT_FRAME;

            // The graph samples 5 times a second rather than on every screen frame. Sixty slots
            // then span 12 seconds, which is enough to show a trend, and the EMA flattens the
            // sawtooth that individual keystrokes would otherwise produce.
            graphValue += (kpm - graphValue) * 0.35;
            if (++graphTick >= GRAPH_EVERY)
            {
                graphTick = 0;
                Array.Copy(history, 1, history, 0, POINTS - 1);
                history[POINTS - 1] = graphValue < 0.5 ? 0.0 : graphValue;
            }

            double sum = 0, peak = 0;
            for (int i = 0; i < POINTS; i++)
            {
                sum += history[i];
                if (history[i] > peak) peak = history[i];
            }

            // Ease the Y axis ceiling toward its target so the graph does not jump.
            double target = Math.Max(MIN_SCALE_TOP, peak * 1.25);
            graphTop += (target - graphTop) * 0.18;

            displayKpm = (int)Math.Round(kpm, MidpointRounding.AwayFromZero);
            hourStrokes = hour.LastHour(now);

            // The hook may have been dropped silently, so re-arm it periodically.
            if (now - lastRearmMs > 20000)
            {
                lastRearmMs = now;
                if (hook != null) hook.Rearm();
            }

            // Checking for midnight and saving once every 30s is plenty; no disk work per frame.
            if (now - lastStatsMs > 30000)
            {
                lastStatsMs = now;
                cfg.RollOverDayIfNeeded();
                cfg.SaveStatsIfDirty();
                UpdateTrayText();   // on the same slow cadence; the tooltip is the only view when hidden
            }

            UpdateAccent(kpm);

            bool idle = sum <= 0.0001 && now - lastKeyMs > 2000;
            bool changed = displayKpm != paintedKpm
                        || spriteFrame != paintedFrame
                        || Math.Abs(sum - paintedSum) > 0.0001
                        || hourStrokes != paintedHour
                        || cfg.TodayStrokes != paintedToday;

            if (changed)
            {
                paintedKpm = displayKpm;
                paintedFrame = spriteFrame;
                paintedSum = sum;
                paintedHour = hourStrokes;
                paintedToday = cfg.TodayStrokes;
                Invalidate();
            }

            // While nothing is being typed, wake up less often.
            int want = idle ? IDLE_MS : ACTIVE_MS;
            if (timer.Interval != want) timer.Interval = want;
        }

        // ------------------------------------------------------------------- drawing

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            g.FillRectangle(bgBrush, ClientRectangle);

            float pad = BASE_PAD * scale;
            float emW = BASE_ANIM_W * scale;
            float emGap = BASE_ANIM_GAP * scale;
            float numW = BASE_NUM_W * scale;
            float gap = BASE_GAP * scale;
            float gw = BASE_GRAPH_W * scale;
            float gh = BASE_GRAPH_H * scale;
            float topH = BASE_TOP_H * scale;

            // Horizontal order: number -> graph -> cat
            float numX = pad;
            float gx = pad + numW + gap;
            float catX = gx + gw + emGap;

            // the running cat
            DrawRunner(g, catX, topH);

            // Current rate, integers only. Drawn several times at slight offsets so it bleeds like a glow.
            string num = displayKpm.ToString(CultureInfo.InvariantCulture);
            RectangleF numRect = new RectangleF(numX, 0, numW, topH);
            float bleed = Math.Max(1f, scale);
            for (int i = 0; i < 4; i++)
            {
                float ox = (i == 0 ? -bleed : i == 1 ? bleed : 0);
                float oy = (i == 2 ? -bleed : i == 3 ? bleed : 0);
                g.DrawString(num, numFont, glowTextBrush,
                    new RectangleF(numRect.X + ox, numRect.Y + oy, numRect.Width, numRect.Height), rightFormat);
            }
            g.DrawString(num, numFont, textBrush, numRect, rightFormat);

            // the graph
            float gy = (topH - gh) / 2f;
            float step = (gw - 1f) / (POINTS - 1);
            float bottom = gy + gh;

            for (int i = 0; i < POINTS; i++)
            {
                double v = history[i] / graphTop;
                if (v > 1) v = 1;
                if (v < 0) v = 0;
                pts[i] = new PointF(gx + i * step, bottom - (float)(v * gh));
            }

            areaPath.Reset();
            areaPath.AddLines(pts);
            areaPath.AddLine(pts[POINTS - 1].X, pts[POINTS - 1].Y, gx + gw - 1f, bottom);
            areaPath.AddLine(gx + gw - 1f, bottom, gx, bottom);
            areaPath.CloseFigure();

            g.FillPath(areaBrush, areaPath);
            g.DrawLine(basePen, gx, bottom, gx + gw - 1f, bottom);
            g.DrawLines(glowLinePen, pts);
            g.DrawLines(linePen, pts);

            // bottom row: keystrokes in the last hour / today
            float subH = ClientSize.Height - topH;
            float half = ClientSize.Width / 2f;
            g.DrawLine(basePen, pad, topH, ClientSize.Width - pad, topH);
            g.DrawString("1hr " + hourStrokes.ToString("N0", CultureInfo.InvariantCulture),
                subFont, subBrush, new RectangleF(pad, topH, half - pad, subH), leftFormat);
            g.DrawString("TODAY " + cfg.TodayStrokes.ToString("N0", CultureInfo.InvariantCulture),
                subFont, subBrush, new RectangleF(half, topH, half - pad, subH), rightFormat);

        }

        // Draw the cat sprite, cutting out only the current frame.
        void DrawRunner(Graphics g, float x0, float topH)
        {
            if (sprite == null) return;

            int frame = spriteFrame;
            float w = CROP_W * SPRITE_ZOOM * scale;
            float h = CROP_H * SPRITE_ZOOM * scale;
            int dx = (int)Math.Round(x0);
            int dy = (int)Math.Round((topH - h) / 2f);

            // Pixel art must be magnified without interpolation. Without TileFlipXY the sampler
            // picks up pixels from the neighbouring frame at the edges and leaves a thin seam.
            InterpolationMode oldMode = g.InterpolationMode;
            PixelOffsetMode oldOffset = g.PixelOffsetMode;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(sprite,
                new Rectangle(dx, dy, (int)Math.Round(w), (int)Math.Round(h)),
                frame * SPRITE_W + CROP_X, CROP_Y, CROP_W, CROP_H,
                GraphicsUnit.Pixel, spriteAttrs);
            g.InterpolationMode = oldMode;
            g.PixelOffsetMode = oldOffset;
        }

        static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2f;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // --------------------------------------------------------- sizing / DPI handling

        void ApplyMetrics()
        {
            scale = (float)(cfg.Scale * dpi / 96.0);

            int w = (int)Math.Round((BASE_PAD * 2 + BASE_ANIM_W + BASE_ANIM_GAP +
                                     BASE_NUM_W + BASE_GAP + BASE_GRAPH_W) * scale);
            int h = (int)Math.Round(BASE_H * scale);

            DisposeResources();

            // A monospaced face sells the meter look, and the width stays put as digits change.
            numFont = new Font("Consolas", BASE_FONT * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            subFont = new Font("Consolas", BASE_SUB_FONT * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            bgBrush = new SolidBrush(Color.FromArgb(255, 8, 12, 13));
            textBrush = new SolidBrush(Color.FromArgb(255, 240, 244, 248));
            subBrush = new SolidBrush(Color.FromArgb(255, 150, 158, 170));
            glowTextBrush = new SolidBrush(Color.FromArgb(26, 255, 255, 255));
            borderPen = new Pen(Color.FromArgb(48, 255, 255, 255), 1f);
            linePen = new Pen(Color.FromArgb(255, 78, 214, 168), Math.Max(1.2f, 1.4f * scale));
            linePen.LineJoin = LineJoin.Round;
            glowLinePen = new Pen(Color.FromArgb(30, 78, 214, 168), Math.Max(3.5f, 4f * scale));
            glowLinePen.LineJoin = LineJoin.Round;
            basePen = new Pen(Color.FromArgb(30, 255, 255, 255), 1f);

            // A gradient brush's rectangle must match the area actually filled. Build it 1px wide
            // and GDI+ tiles the brush hundreds of times across, producing circular artifacts.
            float gh = BASE_GRAPH_H * scale;
            float gy = (BASE_TOP_H * scale - gh) / 2f;
            float gxm = (BASE_PAD + BASE_NUM_W + BASE_GAP) * scale;
            graphRect = new RectangleF(gxm, gy, BASE_GRAPH_W * scale, gh + 1);
            areaBrush = new LinearGradientBrush(graphRect,
                Color.FromArgb(120, 78, 214, 168),
                Color.FromArgb(10, 78, 214, 168),
                LinearGradientMode.Vertical);

            ClientSize = new Size(w, h);

            borderPath = RoundedRect(new RectangleF(0.5f, 0.5f, w - 1f, h - 1f), BASE_RADIUS * scale);

            if (Region != null) { Region.Dispose(); Region = null; }
            using (GraphicsPath path = RoundedRect(new RectangleF(0, 0, w, h), BASE_RADIUS * scale))
                Region = new Region(path);

            accentKey = int.MinValue;  // re-apply the accent colour to the new resources
            paintedKpm = int.MinValue; // force a repaint
            UpdateAccent(displayKpm > 0 ? displayKpm : 0);
            Invalidate();
        }

        void DisposeResources()
        {
            if (numFont != null) numFont.Dispose();
            if (subFont != null) subFont.Dispose();
            if (bgBrush != null) bgBrush.Dispose();
            if (textBrush != null) textBrush.Dispose();
            if (subBrush != null) subBrush.Dispose();
            if (glowTextBrush != null) glowTextBrush.Dispose();
            if (borderPen != null) borderPen.Dispose();
            if (linePen != null) linePen.Dispose();
            if (glowLinePen != null) glowLinePen.Dispose();
            if (basePen != null) basePen.Dispose();
            if (areaBrush != null) areaBrush.Dispose();
            if (borderPath != null) borderPath.Dispose();
        }

        // ------------------------------------------------------- accent colour by rate

        // slow (grey) -> sky -> green -> yellow -> orange -> red, linearly interpolated between.
        static readonly int[] STOP_KPM = { 0, 150, 350, 550, 750, 950 };
        static readonly Color[] STOP_COLOR = {
            Color.FromArgb(255, 130, 138, 150),
            Color.FromArgb(255,  79, 195, 247),
            Color.FromArgb(255,  78, 214, 155),
            Color.FromArgb(255, 242, 193,  78),
            Color.FromArgb(255, 255, 138,  76),
            Color.FromArgb(255, 255,  92, 109),
        };

        static Color AccentFor(double kpm)
        {
            if (kpm <= STOP_KPM[0]) return STOP_COLOR[0];
            for (int i = 1; i < STOP_KPM.Length; i++)
            {
                if (kpm >= STOP_KPM[i]) continue;
                double t = (kpm - STOP_KPM[i - 1]) / (double)(STOP_KPM[i] - STOP_KPM[i - 1]);
                Color a = STOP_COLOR[i - 1], b = STOP_COLOR[i];
                return Color.FromArgb(255,
                    (int)(a.R + (b.R - a.R) * t),
                    (int)(a.G + (b.G - a.G) * t),
                    (int)(a.B + (b.B - a.B) * t));
            }
            return STOP_COLOR[STOP_COLOR.Length - 1];
        }

        // Quantise the colour to 8 keys/min steps. The eye cannot tell the difference, and it
        // avoids rebuilding the gradient brush on every single frame.
        void UpdateAccent(double kpm)
        {
            int key = (int)(Math.Min(kpm, 1200.0) / 8.0);
            if (key == accentKey || textBrush == null) return;
            accentKey = key;

            Color c = AccentFor(kpm);
            textBrush.Color = c;
            linePen.Color = c;
            glowLinePen.Color = Color.FromArgb(30, c);
            glowTextBrush.Color = Color.FromArgb(28, c);
            basePen.Color = Color.FromArgb(30, c);
            borderPen.Color = Color.FromArgb(52, c);

            // The bottom row uses a darkened version of the same colour so it reads as one display.
            subBrush.Color = Color.FromArgb(255, (int)(c.R * 0.66), (int)(c.G * 0.66), (int)(c.B * 0.66));

            if (areaBrush != null) areaBrush.Dispose();
            areaBrush = new LinearGradientBrush(graphRect,
                Color.FromArgb(120, c), Color.FromArgb(10, c), LinearGradientMode.Vertical);
        }

        [DllImport("user32.dll")]
        static extern int GetDpiForWindow(IntPtr hWnd);

        int GetDpi()
        {
            try
            {
                if (IsHandleCreated)
                {
                    int d = GetDpiForWindow(Handle);
                    if (d >= 72) return d;
                }
            }
            catch { }
            using (Graphics g = CreateGraphics())
                return (int)Math.Round(g.DpiX);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        protected override void WndProc(ref Message m)
        {
            const int WM_DPICHANGED = 0x02E0;
            if (m.Msg == WM_DPICHANGED)
            {
                int newDpi = (int)((uint)m.WParam.ToInt64() & 0xFFFF);
                if (newDpi >= 72 && newDpi != dpi)
                {
                    dpi = newDpi;
                    ApplyMetrics();
                }

                // The OS does not move a Per-Monitor V2 window for you. Honouring the suggested
                // rect from lParam is what keeps it correctly placed on the rescaled monitor.
                try
                {
                    RECT sug = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                    const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
                    SetWindowPos(Handle, IntPtr.Zero, sug.Left, sug.Top, Width, Height,
                                 SWP_NOZORDER | SWP_NOACTIVATE);
                }
                catch { }
                ClampToScreen();
                SavePosition();

                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        // --------------------------------------------------------------- drag / menu

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

        IntPtr prevForeground;

        void OnMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            const int WM_NCLBUTTONDOWN = 0x00A1;
            const int HTCAPTION = 2;
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
        }

        void OnMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;

            // For a popup menu to dismiss on an outside click, its owner must be the foreground
            // window. But this window is WS_EX_NOACTIVATE, so leaving it holding focus makes the
            // user's keystrokes vanish. Remember the previous foreground window and hand focus
            // back when the menu closes.
            prevForeground = GetForegroundWindow();
            SetForegroundWindow(Handle);
            menu.Show(this, e.Location);
        }

        // ------------------------------------------------------------------ tray icon

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr hIcon);

        // Builds the tray icon out of the embedded sprite so the exe still needs no .ico resource.
        static Icon MakeTrayIcon()
        {
            if (sprite == null) return SystemIcons.Application;
            try
            {
                using (Bitmap bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        // the sitting pose, scaled up to fill the icon square
                        g.DrawImage(sprite, new Rectangle(1, 3, 30, 25),
                            SIT_FRAME * SPRITE_W + CROP_X, CROP_Y, CROP_W, CROP_H,
                            GraphicsUnit.Pixel, spriteAttrs);
                    }
                    // GetHicon hands back a handle we own; clone into a managed icon and free it,
                    // otherwise the handle leaks for the life of the process.
                    IntPtr h = bmp.GetHicon();
                    try { return (Icon)Icon.FromHandle(h).Clone(); }
                    finally { DestroyIcon(h); }
                }
            }
            catch { return SystemIcons.Application; }
        }

        void BuildTray()
        {
            trayMenu = new ContextMenuStrip();

            trayShowItem = new ToolStripMenuItem("Show widget");
            trayShowItem.Checked = true;
            trayShowItem.Click += delegate { SetWidgetVisible(!Visible); };
            trayMenu.Items.Add(trayShowItem);

            trayMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem quit = new ToolStripMenuItem("Exit");
            quit.Click += delegate { Close(); };
            trayMenu.Items.Add(quit);

            trayIcon = MakeTrayIcon();
            tray = new NotifyIcon();
            tray.Icon = trayIcon;
            tray.Text = "KeyboardCounter";
            tray.ContextMenuStrip = trayMenu;
            tray.DoubleClick += delegate { SetWidgetVisible(!Visible); };
            tray.Visible = true;
        }

        // Hiding keeps the hook and the counters running - only the window goes away.
        void SetWidgetVisible(bool show)
        {
            Visible = show;
            if (show)
            {
                ClampToScreen();   // a display change while hidden could have stranded it
                TopMost = cfg.TopMost;
            }
            if (trayShowItem != null) trayShowItem.Checked = show;
        }

        void UpdateTrayText()
        {
            if (tray == null) return;
            // NotifyIcon.Text is capped at 63 characters.
            string s = "KeyboardCounter\r\n"
                     + displayKpm.ToString(CultureInfo.InvariantCulture) + " now, "
                     + cfg.TodayStrokes.ToString("N0", CultureInfo.InvariantCulture) + " today";
            if (s.Length > 63) s = s.Substring(0, 63);
            try { tray.Text = s; }
            catch { }
        }

        void BuildMenu()
        {
            menu = new ContextMenuStrip();

            // Whether an item was picked or the menu was dismissed, return focus to the window in use.
            menu.Closed += delegate
            {
                if (prevForeground != IntPtr.Zero && prevForeground != Handle)
                    SetForegroundWindow(prevForeground);
                prevForeground = IntPtr.Zero;
            };

            ToolStripMenuItem size = new ToolStripMenuItem("Size");
            size.DropDownItems.Add(SizeItem("Small", 0.85));
            size.DropDownItems.Add(SizeItem("Normal", 1.0));
            size.DropDownItems.Add(SizeItem("Large", 1.3));
            size.DropDownItems.Add(SizeItem("Extra large", 1.7));
            menu.Items.Add(size);

            ToolStripMenuItem speed = new ToolStripMenuItem("Response");
            speed.DropDownItems.Add(ResponseItem("Very fast (0.7s)", 700));
            speed.DropDownItems.Add(ResponseItem("Default (1.2s)", 1200));
            speed.DropDownItems.Add(ResponseItem("Steady (2.5s)", 2500));
            menu.Items.Add(speed);

            ToolStripMenuItem top = new ToolStripMenuItem("Always on top");
            top.Checked = cfg.TopMost;
            top.Click += delegate
            {
                cfg.TopMost = !cfg.TopMost;
                top.Checked = cfg.TopMost;
                TopMost = cfg.TopMost;
                cfg.Save();
            };
            menu.Items.Add(top);

            ToolStripMenuItem auto = new ToolStripMenuItem("Run at Windows startup");
            auto.Checked = IsAutoStart();
            auto.Click += delegate
            {
                bool on = !auto.Checked;
                SetAutoStart(on);
                auto.Checked = IsAutoStart();
            };
            menu.Items.Add(auto);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem resetToday = new ToolStripMenuItem("Reset today's count");
            resetToday.Click += delegate
            {
                cfg.TodayStrokes = 0;
                cfg.SaveStatsIfDirty();
                Invalidate();
            };
            menu.Items.Add(resetToday);

            ToolStripMenuItem reset = new ToolStripMenuItem("Reset position");
            reset.Click += delegate
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(wa.Right - Width - 24, wa.Top + 24);
                SavePosition();
            };
            menu.Items.Add(reset);

            menu.Items.Add(new ToolStripSeparator());

            // Safe to offer because the tray icon is how it comes back.
            ToolStripMenuItem hide = new ToolStripMenuItem("Hide to tray");
            hide.Click += delegate { SetWidgetVisible(false); };
            menu.Items.Add(hide);

            ToolStripMenuItem quit = new ToolStripMenuItem("Exit");
            quit.Click += delegate { Close(); };
            menu.Items.Add(quit);
        }

        ToolStripMenuItem SizeItem(string text, double value)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Checked = Math.Abs(cfg.Scale - value) < 0.01;
            item.Click += delegate
            {
                cfg.Scale = value;
                cfg.Save();
                ApplyMetrics();
                ClampToScreen();
                foreach (ToolStripItem sib in item.Owner.Items)
                {
                    ToolStripMenuItem mi = sib as ToolStripMenuItem;
                    if (mi != null) mi.Checked = (mi == item);
                }
            };
            return item;
        }

        ToolStripMenuItem ResponseItem(string text, int ms)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Checked = cfg.ResponseMs == ms;
            item.Click += delegate
            {
                cfg.ResponseMs = ms;
                meter.TauMs = ms;
                cfg.Save();
                foreach (ToolStripItem sib in item.Owner.Items)
                {
                    ToolStripMenuItem mi = sib as ToolStripMenuItem;
                    if (mi != null) mi.Checked = (mi == item);
                }
            };
            return item;
        }

        // ------------------------------------------------------- position / autostart

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "KeyboardCounter";

        static bool IsAutoStart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    return k != null && k.GetValue(RunValue) != null;
            }
            catch { return false; }
        }

        static void SetAutoStart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                    else if (k.GetValue(RunValue) != null) k.DeleteValue(RunValue);
                }
            }
            catch { }
        }

        void RestorePosition()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            if (!cfg.HasPos)
                Location = new Point(wa.Right - Width - 24, wa.Top + 24);
            else
                Location = new Point(cfg.X, cfg.Y);
            ClampToScreen();
        }

        void ClampToScreen()
        {
            Rectangle wa = Screen.GetWorkingArea(Bounds);
            int x = Math.Max(wa.Left, Math.Min(Left, wa.Right - Width));
            int y = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - Height));
            Location = new Point(x, y);
        }

        void SavePosition()
        {
            cfg.HasPos = true;
            cfg.X = Left;
            cfg.Y = Top;
            cfg.Save();
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            SavePosition();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SavePosition();
            timer.Stop();
            // Clear the tray icon before the process goes away, or a dead icon lingers in the
            // notification area until something makes the shell repaint it.
            if (tray != null) tray.Visible = false;
            if (hook != null) hook.Dispose();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeResources();
                rightFormat.Dispose();
                leftFormat.Dispose();
                areaPath.Dispose();
                timer.Dispose();
                if (menu != null) menu.Dispose();
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
                if (trayMenu != null) trayMenu.Dispose();
                if (trayIcon != null) trayIcon.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
