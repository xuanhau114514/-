// StartMenuSlowDrag.cs  (v2)
//
// Resident tray tool.
//
//   Win key, tapped alone  -> opens the Start menu with a FAST upward swipe
//                             injected from the bottom edge, which replaces the
//                             normal short animation with the smooth
//                             finger-following one.
//   Win key + <letter>     -> the original combo is replayed, so nothing is lost.
//   CapsLock, tapped alone -> toggles CapsLock as usual, does NOT open the menu.
//   CapsLock + <letter>    -> acts as Win + <letter> (a spare substitute, since
//                             the Fn key is handled by the embedded controller
//                             and is invisible to software).
//
//   Ctrl+Alt+F / Ctrl+Alt+D   duration -/+ 25 ms   (tray balloon shows the value)
//   Ctrl+Alt+Shift+S          quit
//
// Config file "config.txt" next to the exe:
//     duration=150      swipe duration in ms
//     distance=400      swipe distance in px
//     winkey=on         off = do not touch the Win/CapsLock keys at all
//
// Why the Win key needs a keyboard hook:
//   RegisterHotKey cannot register the Windows key on its own -- it is reserved
//   by the OS. A low-level keyboard hook (WH_KEYBOARD_LL) is required.
//   Measured behaviour: the Start menu opens on the Win key-UP. Suppressing only
//   the key-up would leave the OS believing Win is still held (GetAsyncKeyState
//   reported 0x8001 / pressed), which would turn later keypresses into Win+key
//   combos. So both the key-down and the key-up are suppressed, which leaves the
//   OS key state clean, and combos are replayed explicitly instead.
//
// Touch injection configuration is taken verbatim from FlaUI's Touch class:
//   InitializeTouchInjection(256, TOUCH_FEEDBACK_DEFAULT)
//   DOWN|INRANGE|INCONTACT  ->  UPDATE|INRANGE|INCONTACT ...  ->  UP
//   touchFlags = 0, touchMask = 0, rcContact = zero-size rect at the contact
//   and the contact must START AT THE BOTTOM EDGE of the screen.
//
// ASCII only on purpose.

using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTER_INFO
    {
        public uint pointerType, pointerId, frameId, pointerFlags;
        public IntPtr sourceDevice, hwndTarget;
        public POINT ptPixelLocation, ptPixelLocationRaw, ptHimetricLocation, ptHimetricLocationRaw;
        public uint dwTime, historyCount, inputData, dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags, touchMask;
        public RECT rcContact, rcContactRaw;
        public uint orientation, pressure;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }

    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("User32.dll", SetLastError = true)] public static extern bool InitializeTouchInjection(uint maxCount, uint feedbackMode);
    [DllImport("User32.dll", SetLastError = true)] public static extern bool InjectTouchInput(int count, [MarshalAs(UnmanagedType.LPArray), In] POINTER_TOUCH_INFO[] contacts);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string win);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string win);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);

    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr hMod, uint tid);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte bs, uint f, UIntPtr extra);

    public const uint PT_TOUCH = 2, INRANGE = 0x2, INCONTACT = 0x4;
    public const uint DOWN = 0x10000, UPDATE = 0x20000, UP = 0x40000;
    public const int WH_KEYBOARD_LL = 13;
    public const byte VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_CAPITAL = 0x14;
    public const long MAGIC_VAL = 0x44534800L;
    public static readonly UIntPtr MAGIC = (UIntPtr)0x44534800UL;
}

class TrayApp : Form
{
    const int WM_HOTKEY = 0x0312;
    const int HK_FIRE = 1, HK_FASTER = 2, HK_SLOWER = 3, HK_QUIT = 4;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4;
    const byte VK_S = 0x53, VK_F = 0x46, VK_D = 0x44;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    readonly NotifyIcon tray = new NotifyIcon();
    readonly string dir, durPath, distPath, logPath;

    // Defaults measured with SwipeRecorder.exe from real finger swipes:
    //   press, dwell ~30 ms, then an ACCELERATING 200 px over 120 ms, and
    //   release while still moving fast.
    // The release velocity is what feeds the shell's fling animation. A drag
    // that ends at a constant speed -- or worse, pauses at the top before
    // letting go -- gives the fling zero initial velocity and the menu just
    // snaps into place, which is exactly what felt wrong.
    int durationMs = 120;
    int distancePx = 200;
    double easeIn = 2.0;
    int holdMs = 30;          // dwell after touch-down, before the movement
    bool takeover = true;

    bool touchReady = false;
    readonly object gate = new object();
    bool dragging = false;

    // keyboard hook state
    IntPtr hook = IntPtr.Zero;
    Native.HookProc hookProc;
    bool winDown = false, winCombo = false;

    static string ExeDir()
    {
        try { return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
        catch { return Environment.CurrentDirectory; }
    }

    public TrayApp()
    {
        dir = ExeDir();
        durPath = Path.Combine(dir, "duration-ms.txt");
        distPath = Path.Combine(dir, "distance-px.txt");
        logPath = Path.Combine(dir, "slowdrag.log");
        try { File.WriteAllText(logPath, ""); } catch { }
        LoadConfig();

        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        FormBorderStyle = FormBorderStyle.None;
        Opacity = 0;

        tray.Icon = SystemIcons.Application;
        tray.Visible = true;
        UpdateTrayText();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Start menu now", null, delegate { Fire(); });
        menu.Items.Add("Faster (-25 ms)", null, delegate { Nudge(-25); });
        menu.Items.Add("Slower (+25 ms)", null, delegate { Nudge(+25); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open folder", null, delegate { try { System.Diagnostics.Process.Start("explorer.exe", dir); } catch { } });
        menu.Items.Add("Quit", null, delegate { Quit(); });
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += delegate { Fire(); };

        IntPtr forced = this.Handle;   // SetVisibleCore is overridden, so force it
        Log("started handle=" + forced + " dur=" + durationMs + " dist=" + distancePx + " takeover=" + takeover);
    }

    void Log(string s)
    {
        try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + Environment.NewLine); } catch { }
    }

    void LoadConfig()
    {
        try
        {
            string cfg = Path.Combine(dir, "config.txt");
            if (File.Exists(cfg))
            {
                foreach (string raw in File.ReadAllLines(cfg))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    if (k == "duration") { int n; if (int.TryParse(v, out n)) durationMs = n; }
                    else if (k == "distance") { int n; if (int.TryParse(v, out n)) distancePx = n; }
                    else if (k == "ease") { double d; if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) easeIn = d; }
                    else if (k == "hold") { int n; if (int.TryParse(v, out n)) holdMs = n; }
                    else if (k == "winkey") takeover = !(v == "off" || v == "0" || v == "false");
                }
            }
        }
        catch { }
        durationMs = Math.Max(60, Math.Min(1500, durationMs));
        distancePx = Math.Max(120, Math.Min(1500, distancePx));
    }

    // config.txt is the single source of truth, so a hotkey nudge must be
    // written back there -- otherwise the loader would reset it on the next
    // fire, which is exactly the bug this replaces.
    void SaveDuration()
    {
        try
        {
            string cfg = Path.Combine(dir, "config.txt");
            var lines = new System.Collections.Generic.List<string>(
                File.Exists(cfg) ? File.ReadAllLines(cfg) : new string[0]);
            bool done = false;
            for (int i = 0; i < lines.Count; i++)
            {
                int eq = lines[i].IndexOf('=');
                if (eq <= 0) continue;
                if (lines[i].Substring(0, eq).Trim().ToLowerInvariant() == "duration")
                {
                    lines[i] = "duration=" + durationMs;
                    done = true;
                    break;
                }
            }
            if (!done) lines.Add("duration=" + durationMs);
            File.WriteAllLines(cfg, lines.ToArray());
        }
        catch { }
    }

    void UpdateTrayText()
    {
        // NotifyIcon.Text has a hard 63-character limit and throws if exceeded.
        string s = "Swipe " + durationMs + " ms / " + distancePx + " px  (Win = open)";
        if (s.Length > 63) s = s.Substring(0, 63);
        try { tray.Text = s; } catch { }
    }

    protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterHotKey(Handle, HK_FIRE, MOD_CONTROL | MOD_ALT, VK_S);
        RegisterHotKey(Handle, HK_FASTER, MOD_CONTROL | MOD_ALT, VK_F);
        RegisterHotKey(Handle, HK_SLOWER, MOD_CONTROL | MOD_ALT, VK_D);
        RegisterHotKey(Handle, HK_QUIT, MOD_CONTROL | MOD_ALT | MOD_SHIFT, VK_S);

        if (takeover)
        {
            hookProc = new Native.HookProc(KeyboardHook);
            hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, hookProc, IntPtr.Zero, 0);
            Log("keyboard hook = " + hook + " (0 means FAILED)");
        }
        else Log("takeover disabled in config");
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        UnregisterHotKey(Handle, HK_FIRE);
        UnregisterHotKey(Handle, HK_FASTER);
        UnregisterHotKey(Handle, HK_SLOWER);
        UnregisterHotKey(Handle, HK_QUIT);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            if (id == HK_FIRE) Fire();
            else if (id == HK_FASTER) Nudge(-25);
            else if (id == HK_SLOWER) Nudge(+25);
            else if (id == HK_QUIT) Quit();
        }
        base.WndProc(ref m);
    }

    // ---------------------------------------------------------------- keyboard

    static void Down(byte vk) { Native.keybd_event(vk, 0, 0, Native.MAGIC); }
    static void Up(byte vk) { Native.keybd_event(vk, 0, 2, Native.MAGIC); }

    static void ReplayWinCombo(byte vk)
    {
        Down(Native.VK_LWIN); Thread.Sleep(18);
        Down(vk);             Thread.Sleep(30);
        Up(vk);               Thread.Sleep(18);
        Up(Native.VK_LWIN);
    }

    IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            Native.KBDLLHOOKSTRUCT k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
            if (k.dwExtraInfo.ToInt64() != Native.MAGIC_VAL)
            {
                int msg = wParam.ToInt32();
                bool isDown = (msg == 0x100 || msg == 0x104);
                bool isUp = (msg == 0x101 || msg == 0x105);
                byte vk = (byte)k.vkCode;

                if (vk == Native.VK_LWIN || vk == Native.VK_RWIN)
                {
                    if (isDown) { if (!winDown) { winDown = true; winCombo = false; } }
                    else if (isUp)
                    {
                        bool lone = winDown && !winCombo;
                        winDown = false; winCombo = false;
                        if (lone) { Log("lone Win -> fire"); Fire(); }
                    }
                    return (IntPtr)1;   // always swallow the Win key itself
                }

                if (vk == Native.VK_CAPITAL)
                {
                    // CapsLock is deliberately NOT intercepted: with Win+<key>
                    // combos preserved there is no need for a substitute
                    // modifier, so the key behaves exactly as stock Windows.
                }
                else if (isDown)
                {
                    if (winDown && !winCombo) { winCombo = true; Log("Win+" + vk + " -> replay"); ReplayWinCombo(vk); return (IntPtr)1; }
                    if (winDown && winCombo) { return (IntPtr)1; }
                }
                else if (isUp)
                {
                    if (winDown && winCombo) return (IntPtr)1;   // swallow the matching key-up
                }
            }
        }
        return Native.CallNextHookEx(hook, nCode, wParam, lParam);
    }

    // ---------------------------------------------------------------- actions

    void Nudge(int delta)
    {
        LoadConfig();
        durationMs = Math.Max(60, Math.Min(1500, durationMs + delta));
        SaveDuration();
        UpdateTrayText();
        tray.BalloonTipTitle = "Start menu swipe";
        tray.BalloonTipText = "Duration = " + durationMs + " ms";
        try { tray.ShowBalloonTip(1200); } catch { }
    }

    void Fire()
    {
        lock (gate) { if (dragging) return; dragging = true; }
        Thread t = new Thread(delegate ()
        {
            try
            {
                // Win acts as a real toggle: if the Start menu is already up,
                // just dismiss it -- no fancy swipe needed for closing.
                if (IsStartMenuOpen())
                {
                    Log("menu is open -> dismissing");
                    CloseStartMenu();
                }
                else
                {
                    DoDrag();
                }
            }
            catch { }
            finally { lock (gate) { dragging = false; } }
        });
        t.IsBackground = true;
        t.Start();
    }

    static readonly string[] ShellHosts = { "StartMenuExperienceHost", "SearchHost", "SearchApp" };

    // The Start menu belongs to StartMenuExperienceHost (and SearchHost once the
    // search box is involved). When it is up, one of those processes owns the
    // foreground window, and it also owns a visible top-level window.
    static bool IsStartMenuOpen()
    {
        var pids = new System.Collections.Generic.HashSet<uint>();
        foreach (string n in ShellHosts)
        {
            try { foreach (var p in System.Diagnostics.Process.GetProcessesByName(n)) pids.Add((uint)p.Id); }
            catch { }
        }
        if (pids.Count == 0) return false;

        try
        {
            IntPtr fg = Native.GetForegroundWindow();
            uint fgPid;
            Native.GetWindowThreadProcessId(fg, out fgPid);
            if (pids.Contains(fgPid)) return true;
        }
        catch { }

        bool found = false;
        try
        {
            Native.EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                if (!Native.IsWindowVisible(h)) return true;
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (pids.Contains(pid))
                {
                    Native.RECT r;
                    if (Native.GetWindowRect(h, out r) && (r.right - r.left) > 80 && (r.bottom - r.top) > 80)
                    {
                        found = true;
                        return false;   // stop enumerating
                    }
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return found;
    }

    static void CloseStartMenu()
    {
        // Escape dismisses the Start menu. It is injected with MAGIC so our own
        // keyboard hook lets it through untouched.
        Down(0x1B);
        Thread.Sleep(40);
        Up(0x1B);
    }

    void Quit() { tray.Visible = false; Application.Exit(); }

    static Native.POINTER_TOUCH_INFO Mk(int x, int y, uint flags)
    {
        Native.POINTER_TOUCH_INFO c = new Native.POINTER_TOUCH_INFO();
        c.pointerInfo.pointerType = Native.PT_TOUCH;
        c.pointerInfo.pointerFlags = flags;
        c.pointerInfo.pointerId = 0;
        c.pointerInfo.ptPixelLocation.X = x;
        c.pointerInfo.ptPixelLocation.Y = y;
        c.touchFlags = 0; c.touchMask = 0;
        c.rcContact.left = x; c.rcContact.right = x;
        c.rcContact.top = y; c.rcContact.bottom = y;
        return c;
    }

    static bool Send(int x, int y, uint flags)
    {
        Native.POINTER_TOUCH_INFO[] a = new Native.POINTER_TOUCH_INFO[1];
        a[0] = Mk(x, y, flags);
        return Native.InjectTouchInput(1, a);
    }

    bool EnsureTouch()
    {
        if (touchReady) return true;
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
        touchReady = Native.InitializeTouchInjection(256, 1);
        return touchReady;
    }

    // fast upward swipe from the bottom edge -- the shell's own fling animation
    // finishes it, which is what makes it look smooth
    void DoDrag()
    {
        if (!EnsureTouch()) { Log("DoDrag: InitializeTouchInjection FAILED"); return; }
        LoadConfig();

        int sh = Native.GetSystemMetrics(1);
        int startX = Native.GetSystemMetrics(0) / 2;

        IntPtr trayWnd = Native.FindWindow("Shell_TrayWnd", null);
        IntPtr startBtn = (trayWnd != IntPtr.Zero) ? Native.FindWindowEx(trayWnd, IntPtr.Zero, "Start", null) : IntPtr.Zero;
        if (startBtn != IntPtr.Zero)
        {
            Native.RECT r;
            if (Native.GetWindowRect(startBtn, out r)) startX = (r.left + r.right) / 2;
        }

        int y0 = sh - 2;                       // bottom edge -- required
        int y1 = y0 - distancePx;
        int steps = Math.Max(8, Math.Min(40, durationMs / 15));

        if (!Send(startX, y0, Native.DOWN | Native.INRANGE | Native.INCONTACT)) { Log("DOWN failed"); return; }
        if (holdMs > 0) Thread.Sleep(holdMs);   // measured press dwell

        int per = Math.Max(durationMs / steps, 1);
        int last = y0;
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            // ease-in: slow to start, fast at release (measured from a real swipe)
            double p = (easeIn > 1.0) ? Math.Pow(t, easeIn) : t;
            last = y0 + (int)Math.Round((y1 - y0) * p);
            if (!Send(startX, last, Native.UPDATE | Native.INRANGE | Native.INCONTACT)) { Log("UPDATE " + i + " failed"); return; }
            Thread.Sleep(per);
        }
        // release IMMEDIATELY, while the contact still has speed: a pause here
        // would zero the release velocity and kill the shell's fling animation
        bool up = Send(startX, last, Native.UP);
        Log("DoDrag dur=" + durationMs + "ms steps=" + steps + " dist=" + distancePx + " ease=" + easeIn + " hold=" + holdMs + " x=" + startX + " y=" + y0 + "->" + last + " up=" + up);
    }

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp());
    }
}
