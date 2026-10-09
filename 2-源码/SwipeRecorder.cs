// SwipeRecorder.cs
//
// Captures a real finger swipe straight from the touch digitizer by reading
// raw HID reports, which is the only way to observe touch that the shell
// consumes for its own gesture (verified: a low-level mouse hook sees nothing).
//
// It does three things:
//   1. lists every raw-input HID device with its usage page / usage / name
//   2. dumps each digitizer's HID capabilities and value-capability list, so the
//      exact usage -> coordinate mapping can be confirmed rather than guessed
//   3. records X / Y / tip-switch for a number of seconds, together with the raw
//      report bytes as a fallback, and writes everything to a text report
//
// Usage:  SwipeRecorder.exe <seconds> <output-file>
//
// ASCII only on purpose.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class Rec
{
    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }

    delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devs, uint count, uint size);
    [DllImport("user32.dll")]
    static extern uint GetRawInputData(IntPtr hRaw, uint cmd, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetRawInputDeviceInfoW")]
    static extern uint GetRawInputDeviceInfoPtr(IntPtr hDevice, uint cmd, IntPtr data, ref uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetRawInputDeviceInfoW")]
    static extern uint GetRawInputDeviceInfoStr(IntPtr hDevice, uint cmd, StringBuilder data, ref uint size);
    [DllImport("user32.dll")]
    static extern uint GetRawInputDeviceList(IntPtr list, ref uint count, uint size);

    [DllImport("hid.dll")]
    static extern int HidP_GetCaps(IntPtr preparsed, IntPtr caps);
    [DllImport("hid.dll")]
    static extern int HidP_GetValueCaps(int reportType, IntPtr valueCaps, ref ushort length, IntPtr preparsed);
    [DllImport("hid.dll")]
    static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection,
        ushort usage, out uint value, IntPtr preparsed, IntPtr report, uint reportLength);

    const int WM_INPUT = 0x00FF;
    const int WM_CLOSE_APP = 0x0400 + 1;
    const uint RIDEV_INPUTSINK = 0x00000100;
    const uint RID_INPUT = 0x10000003;
    const uint RIDI_DEVICENAME = 0x20000007;
    const uint RIDI_PREPARSEDDATA = 0x20000005;
    const uint RIDI_DEVICEINFO = 0x2000000b;
    const int RIM_TYPEHID = 2;
    const int HidP_Input = 0;
    const int HIDP_STATUS_SUCCESS = 0x00110000;

    static IntPtr hwnd;
    static WndProcDelegate wndProc;
    static readonly StringBuilder report = new StringBuilder();
    static readonly List<string> trace = new List<string>();
    static readonly Dictionary<IntPtr, IntPtr> preparsedCache = new Dictionary<IntPtr, IntPtr>();
    static readonly List<long[]> points = new List<long[]>();
    static long t0;
    static int screenW, screenH;
    static long stopAt;

    static void L(string s) { report.AppendLine(s); Console.WriteLine(s); }

    // ------------------------------------------------------------------ window

    static IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        if (msg == WM_INPUT) { HandleInput(l); return IntPtr.Zero; }
        if (msg == WM_CLOSE_APP) { PostMessage(hwnd, 0x0012 /*WM_QUIT*/, IntPtr.Zero, IntPtr.Zero); return IntPtr.Zero; }
        if (msg == 0x0012) { PostMessage(hwnd, 0x0012, IntPtr.Zero, IntPtr.Zero); return IntPtr.Zero; }
        return DefWindowProc(h, msg, w, l);
    }

    static void HandleInput(IntPtr lParam)
    {
        uint size = 0;
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER)));
        if (size == 0) return;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            uint got = GetRawInputData(lParam, RID_INPUT, buf, ref size, (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER)));
            if (got == 0 || got == uint.MaxValue) return;

            uint type = (uint)Marshal.ReadInt32(buf, 0);
            if (type != RIM_TYPEHID) return;
            IntPtr hDevice = Marshal.ReadIntPtr(buf, 8);

            // RAWHID follows RAWINPUTHEADER (24 bytes on x64)
            int off = IntPtr.Size == 8 ? 24 : 16;
            uint sizeHid = (uint)Marshal.ReadInt32(buf, off);
            uint count = (uint)Marshal.ReadInt32(buf, off + 4);
            if (sizeHid == 0 || count == 0) return;
            IntPtr rawData = (IntPtr)((long)buf + off + 8);

            IntPtr preparsed = GetPreparsed(hDevice);
            if (preparsed == IntPtr.Zero) return;

            uint x, y, tip, cid;
            int rx = HidP_GetUsageValue(HidP_Input, 0x01, 0, 0x30, out x, preparsed, rawData, sizeHid);
            int ry = HidP_GetUsageValue(HidP_Input, 0x01, 0, 0x31, out y, preparsed, rawData, sizeHid);
            int rt = HidP_GetUsageValue(HidP_Input, 0x0D, 0, 0x42, out tip, preparsed, rawData, sizeHid);
            int rc = HidP_GetUsageValue(HidP_Input, 0x0D, 0, 0x51, out cid, preparsed, rawData, sizeHid);

            long t = DateTime.Now.Ticks / 10000 - t0;

            byte rid = (byte)Marshal.ReadByte(rawData, 0);

            var sb = new StringBuilder();
            sb.Append("t=").Append(t).Append("ms rid=").Append(rid).Append(" dev=0x").Append(hDevice.ToInt64().ToString("X"))
              .Append(" size=").Append(sizeHid).Append(" count=").Append(count);
            if (rx == HIDP_STATUS_SUCCESS) sb.Append(" X=").Append(x);
            if (ry == HIDP_STATUS_SUCCESS) sb.Append(" Y=").Append(y);
            if (rt == HIDP_STATUS_SUCCESS) sb.Append(" Tip=").Append(tip);
            if (rc == HIDP_STATUS_SUCCESS) sb.Append(" Cid=").Append(cid);
            sb.Append("   raw=");
            for (int i = 0; i < sizeHid && i < 32; i++) sb.Append(((byte)Marshal.ReadByte(rawData, i)).ToString("X2"));
            trace.Add(sb.ToString());

            if (rx == HIDP_STATUS_SUCCESS && ry == HIDP_STATUS_SUCCESS)
            {
                // the digitizer reports in its own units (0..9600 x 0..7200 on the
                // Intel Precise Touch); scale to the physical screen for display
                double px = (double)x * screenW / 9600.0;
                double py = (double)y * screenH / 7200.0;
                points.Add(new long[] { t, x, y, (rt == HIDP_STATUS_SUCCESS) ? tip : 1,
                                        (long)Math.Round(px), (long)Math.Round(py), rid });
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static IntPtr GetPreparsed(IntPtr hDevice)
    {
        IntPtr p;
        if (preparsedCache.TryGetValue(hDevice, out p)) return p;
        uint sz = 0;
        GetRawInputDeviceInfoPtr(hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref sz);
        if (sz == 0) { preparsedCache[hDevice] = IntPtr.Zero; return IntPtr.Zero; }
        p = Marshal.AllocHGlobal((int)sz);
        uint got = GetRawInputDeviceInfoPtr(hDevice, RIDI_PREPARSEDDATA, p, ref sz);
        if (got == 0 || got == uint.MaxValue) { Marshal.FreeHGlobal(p); p = IntPtr.Zero; }
        preparsedCache[hDevice] = p;
        return p;
    }

    // ------------------------------------------------------------------ devices

    static void ListDevices()
    {
        L("=== RAW INPUT DEVICES ===");
        uint count = 0;
        uint sz = (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST));
        GetRawInputDeviceList(IntPtr.Zero, ref count, sz);
        if (count == 0) { L("  (none)"); return; }
        IntPtr list = Marshal.AllocHGlobal((int)(count * sz));
        try
        {
            if (GetRawInputDeviceList(list, ref count, sz) == uint.MaxValue) { L("  list failed"); return; }
            for (int i = 0; i < count; i++)
            {
                IntPtr h = Marshal.ReadIntPtr(list, (int)(i * sz));
                uint type = (uint)Marshal.ReadInt32(list, (int)(i * sz + IntPtr.Size));
                string name = DeviceName(h);
                string extra = "";
                if (type == RIM_TYPEHID)
                {
                    IntPtr p = GetPreparsed(h);
                    if (p != IntPtr.Zero)
                    {
                        IntPtr caps = Marshal.AllocHGlobal(64);
                        if (HidP_GetCaps(p, caps) == HIDP_STATUS_SUCCESS)
                        {
                            ushort usage = (ushort)Marshal.ReadInt16(caps, 0);
                            ushort page = (ushort)Marshal.ReadInt16(caps, 2);
                            ushort inLen = (ushort)Marshal.ReadInt16(caps, 4);
                            ushort nvc = (ushort)Marshal.ReadInt16(caps, 48);
                            ushort nlc = (ushort)Marshal.ReadInt16(caps, 44);
                            extra = string.Format("  usagePage=0x{0:X2} usage=0x{1:X2} inLen={2} valueCaps={3} linkColls={4}",
                                page, usage, inLen, nvc, nlc);
                        }
                        Marshal.FreeHGlobal(caps);
                    }
                }
                L(string.Format("  [{0}] type={1} handle=0x{2}{3}", i, type, h.ToInt64().ToString("X"), extra));
                L(string.Format("        name={0}", name));
            }
        }
        finally { Marshal.FreeHGlobal(list); }
    }

    static string DeviceName(IntPtr h)
    {
        uint sz = 0;
        GetRawInputDeviceInfoStr(h, RIDI_DEVICENAME, null, ref sz);
        if (sz == 0) return "(no name)";
        var sb = new StringBuilder((int)sz + 2);
        if (GetRawInputDeviceInfoStr(h, RIDI_DEVICENAME, sb, ref sz) == 0 || sb.Length == 0) return "(no name)";
        return sb.ToString();
    }

    static void DumpValueCaps()
    {
        L("");
        L("=== VALUE CAPABILITIES OF EACH HID DEVICE ===");
        uint count = 0;
        uint sz = (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST));
        GetRawInputDeviceList(IntPtr.Zero, ref count, sz);
        if (count == 0) return;
        IntPtr list = Marshal.AllocHGlobal((int)(count * sz));
        try
        {
            if (GetRawInputDeviceList(list, ref count, sz) == uint.MaxValue) return;
            var seen = new HashSet<IntPtr>();
            for (int i = 0; i < count; i++)
            {
                IntPtr h = Marshal.ReadIntPtr(list, (int)(i * sz));
                uint type = (uint)Marshal.ReadInt32(list, (int)(i * sz + IntPtr.Size));
                if (type != RIM_TYPEHID || !seen.Add(h)) continue;
                IntPtr p = GetPreparsed(h);
                if (p == IntPtr.Zero) continue;
                IntPtr caps = Marshal.AllocHGlobal(64);
                if (HidP_GetCaps(p, caps) != HIDP_STATUS_SUCCESS) { Marshal.FreeHGlobal(caps); continue; }
                ushort nvc = (ushort)Marshal.ReadInt16(caps, 48);
                ushort page = (ushort)Marshal.ReadInt16(caps, 2);
                ushort usage = (ushort)Marshal.ReadInt16(caps, 0);
                L("");
                L(string.Format("  device 0x{0}  usagePage=0x{1:X2} usage=0x{2:X2}  valueCaps={3}",
                    h.ToInt64().ToString("X"), page, usage, nvc));
                L(string.Format("    {0}", DeviceName(h)));
                if (nvc == 0 || nvc > 256) { Marshal.FreeHGlobal(caps); continue; }

                int capSize = 72;
                IntPtr vc = Marshal.AllocHGlobal(capSize * nvc);
                ushort len = nvc;
                int st = HidP_GetValueCaps(HidP_Input, vc, ref len, p);
                if (st != HIDP_STATUS_SUCCESS) { L("    HidP_GetValueCaps failed 0x" + st.ToString("X")); Marshal.FreeHGlobal(vc); Marshal.FreeHGlobal(caps); continue; }
                for (int k = 0; k < len; k++)
                {
                    int o = k * capSize;
                    ushort vp = (ushort)Marshal.ReadInt16(vc, o + 0);
                    byte rid = (byte)Marshal.ReadByte(vc, o + 2);
                    ushort linkColl = (ushort)Marshal.ReadInt16(vc, o + 6);
                    ushort linkUsage = (ushort)Marshal.ReadInt16(vc, o + 8);
                    ushort linkPage = (ushort)Marshal.ReadInt16(vc, o + 10);
                    bool isRange = Marshal.ReadByte(vc, o + 12) != 0;
                    ushort bitSize = (ushort)Marshal.ReadInt16(vc, o + 18);
                    ushort repCount = (ushort)Marshal.ReadInt16(vc, o + 20);
                    int lmin = Marshal.ReadInt32(vc, o + 40);
                    int lmax = Marshal.ReadInt32(vc, o + 44);
                    ushort u = (ushort)Marshal.ReadInt16(vc, o + 56);        // NotRange.Usage
                    ushort umin = (ushort)Marshal.ReadInt16(vc, o + 56);     // Range.UsageMin
                    ushort umax = (ushort)Marshal.ReadInt16(vc, o + 58);     // Range.UsageMax
                    string usageStr = isRange ? string.Format("0x{0:X2}..0x{1:X2}", umin, umax)
                                              : string.Format("0x{0:X2}", u);
                    L(string.Format("      page=0x{0:X2} usage={1,-12} linkColl={2,-3} linkPage=0x{3:X2} linkUsage=0x{4:X2} rid={5,-3} bits={6,-3} cnt={7,-3} logical={8}..{9}",
                        vp, usageStr, linkColl, linkPage, linkUsage, rid, bitSize, repCount, lmin, lmax));
                }
                Marshal.FreeHGlobal(vc);
                Marshal.FreeHGlobal(caps);
            }
        }
        finally { Marshal.FreeHGlobal(list); }
    }

    // ------------------------------------------------------------------ main

    static int Main(string[] args)
    {
        int seconds = 25;
        string outFile = "swipe-report.txt";
        if (args.Length > 0) int.TryParse(args[0], out seconds);
        if (args.Length > 1) outFile = args[1];

        SetProcessDpiAwarenessContext(new IntPtr(-4));
        screenW = GetSystemMetrics(0);
        screenH = GetSystemMetrics(1);

        L("=== SWIPE RECORDER ===");
        L(string.Format("  screen {0} x {1} (physical)   duration {2}s", screenW, screenH, seconds));
        L("");

        // hidden message window
        wndProc = new WndProcDelegate(WndProc);
        var wc = new WNDCLASSEX();
        wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX));
        wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc);
        wc.hInstance = GetModuleHandle(null);
        wc.lpszClassName = "DSHSwipeRecorder";
        ushort atom = RegisterClassEx(ref wc);
        L(string.Format("RegisterClassEx -> atom {0}  (err {1})", atom, Marshal.GetLastWin32Error()));
        if (atom == 0) { L("RegisterClassEx FAILED"); return 2; }
        IntPtr HWND_MESSAGE = new IntPtr(-3);
        hwnd = CreateWindowEx(0, "DSHSwipeRecorder", "rec", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            // fall back to a normal hidden window if message-only is refused
            hwnd = CreateWindowEx(0, "DSHSwipeRecorder", "rec", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        }
        if (hwnd == IntPtr.Zero) { L("CreateWindowEx failed " + Marshal.GetLastWin32Error()); return 2; }
        L(string.Format("window handle = 0x{0}", hwnd.ToInt64().ToString("X")));

        ListDevices();
        DumpValueCaps();

        var devs = new RAWINPUTDEVICE[3];
        devs[0].usUsagePage = 0x0D; devs[0].usUsage = 0x04;   // Touch Screen
        devs[1].usUsagePage = 0x0D; devs[1].usUsage = 0x05;   // Touch Pad
        devs[2].usUsagePage = 0x0D; devs[2].usUsage = 0x22;   // Finger
        for (int i = 0; i < devs.Length; i++) { devs[i].dwFlags = RIDEV_INPUTSINK; devs[i].hwndTarget = hwnd; }
        bool okReg = RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
        L("");
        L(string.Format("RegisterRawInputDevices -> {0}  (err {1})", okReg, Marshal.GetLastWin32Error()));

        L("");
        L("### NOW SWIPE ###  do your natural finger swipe from the bottom edge, twice.");
        L("");

        t0 = DateTime.Now.Ticks / 10000;
        stopAt = Environment.TickCount + seconds * 1000;

        // pump with a precise wait instead of Sleep(), so no WM_INPUT is delayed
        MSG m;
        while (Environment.TickCount < stopAt)
        {
            MsgWaitForMultipleObjects(0, IntPtr.Zero, false, 1, 0x04FF /* QS_ALLINPUT */);
            while (PeekMessage(out m, IntPtr.Zero, 0, 0, 1))
            {
                TranslateMessage(ref m);
                DispatchMessage(ref m);
            }
        }

        L("");
        L(string.Format("=== CAPTURED {0} HID REPORTS, {1} WITH X/Y ===", trace.Count, points.Count));
        L("");
        L("--- ALL X/Y points are dumped to the CSV (no decimation) ---");
        if (points.Count > 0)
        {
            string csv = outFile + ".csv";
            var cb = new StringBuilder();
            cb.AppendLine("seq,t_ms,Xraw,Yraw,px,py,tip,rid");
            for (int i = 0; i < points.Count; i++)
            {
                long[] p = points[i];
                cb.Append(i).Append(',').Append(p[0]).Append(',').Append(p[1]).Append(',')
                  .Append(p[2]).Append(',').Append(p[4]).Append(',').Append(p[5]).Append(',')
                  .Append(p[3]).Append(',').Append(p[6]).AppendLine();
            }
            try
            {
                File.WriteAllText(csv, cb.ToString(), Encoding.UTF8);
                L("   CSV written: " + csv + "   (" + points.Count + " rows, full resolution)");
            }
            catch (Exception ex) { L("   CSV write failed: " + ex.Message); }
            long minY = long.MaxValue, maxY = long.MinValue, minX = long.MaxValue, maxX = long.MinValue;
            foreach (long[] p in points)
            {
                if (p[2] < minY) minY = p[2];
                if (p[2] > maxY) maxY = p[2];
                if (p[1] < minX) minX = p[1];
                if (p[1] > maxX) maxX = p[1];
            }
            L("");
            L(string.Format("  X raw range {0} .. {1}", minX, maxX));
            L(string.Format("  Y raw range {0} .. {1}", minY, maxY));
        }
        else
        {
            L("   (none) -- raw report dump follows");
            int shown = 0;
            foreach (string s in trace) { L("   " + s); if (++shown >= 40) break; }
        }

        L("");
        L("--- first 30 raw reports (any content) ---");
        for (int i = 0; i < trace.Count && i < 30; i++) L("   " + trace[i]);

        try { File.WriteAllText(outFile, report.ToString(), Encoding.UTF8); Console.WriteLine("written: " + outFile); }
        catch (Exception ex) { Console.WriteLine("write failed: " + ex.Message); }
        return 0;
    }

    [DllImport("user32.dll")]
    static extern bool PeekMessage(out MSG m, IntPtr h, uint min, uint max, uint remove);
    [DllImport("user32.dll")]
    static extern uint MsgWaitForMultipleObjects(uint nCount, IntPtr pHandles, bool bWaitAll, uint ms, uint wakeMask);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string name);
}


