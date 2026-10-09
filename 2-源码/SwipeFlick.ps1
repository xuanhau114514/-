#requires -Version 3.0
<#
    SwipeFlick.ps1

    Variant of the touch swipe that models a real finger FLICK rather than a
    long constant-speed drag: a short, fast movement with an ease-out velocity
    profile, released early so that the shell's own fling animation carries the
    Start menu the rest of the way.

    -DurationMs : time from touch-down to release
    -Distance   : how far the injected contact travels before release
    -Ease       : >1 gives an ease-out (fast start, decelerating) profile.
                  1 = linear. Typical: 2.5 - 4
    -HoldMs     : pause at the bottom edge before moving

    ASCII only on purpose.
#>

param(
  [int]$DurationMs = 110,
  [int]$Distance   = 220,
  [double]$Ease    = 2.5,
  [int]$HoldMs     = 25,
  [int]$Steps      = 0        # 0 = auto
  ,[int]$TopHoldMs = 40       # ???????????
)

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;

public class Flick {
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }

  [StructLayout(LayoutKind.Sequential)]
  public struct POINTER_INFO {
    public uint pointerType, pointerId, frameId, pointerFlags;
    public IntPtr sourceDevice, hwndTarget;
    public POINT ptPixelLocation, ptPixelLocationRaw, ptHimetricLocation, ptHimetricLocationRaw;
    public uint dwTime, historyCount, inputData, dwKeyStates;
    public ulong PerformanceCount;
    public int ButtonChangeType;
  }
  [StructLayout(LayoutKind.Sequential)]
  public struct POINTER_TOUCH_INFO {
    public POINTER_INFO pointerInfo;
    public uint touchFlags, touchMask;
    public RECT rcContact, rcContactRaw;
    public uint orientation, pressure;
  }

  [DllImport("User32.dll", SetLastError=true)] public static extern bool InitializeTouchInjection(uint m, uint f);
  [DllImport("User32.dll", SetLastError=true)] public static extern bool InjectTouchInput(int c, [MarshalAs(UnmanagedType.LPArray), In] POINTER_TOUCH_INFO[] a);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string w);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string w);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);

  const uint PT_TOUCH=2, INRANGE=0x2, INCONTACT=0x4, DOWN=0x10000, UPDATE=0x20000, UP=0x40000;

  static bool inited=false, initRes=false;
  static bool Init(){ if(inited) return initRes; inited=true;
    SetProcessDpiAwarenessContext(new IntPtr(-4));
    initRes = InitializeTouchInjection(256,1); return initRes; }

  static POINTER_TOUCH_INFO Mk(int x,int y,uint f){
    POINTER_TOUCH_INFO c=new POINTER_TOUCH_INFO();
    c.pointerInfo.pointerType=PT_TOUCH; c.pointerInfo.pointerFlags=f; c.pointerInfo.pointerId=0;
    c.pointerInfo.ptPixelLocation.X=x; c.pointerInfo.ptPixelLocation.Y=y;
    c.touchFlags=0; c.touchMask=0;
    c.rcContact.left=x; c.rcContact.right=x; c.rcContact.top=y; c.rcContact.bottom=y;
    return c; }
  static bool Send(int x,int y,uint f){ POINTER_TOUCH_INFO[] a=new POINTER_TOUCH_INFO[1]; a[0]=Mk(x,y,f); return InjectTouchInput(1,a); }

  public static int ScreenH(){ if(!Init()) return 0; return GetSystemMetrics(1); }
  public static int[] StartX(){
    if(!Init()) return null;
    int sx = GetSystemMetrics(0)/2;
    IntPtr t=FindWindow("Shell_TrayWnd",null);
    IntPtr h=(t!=IntPtr.Zero)?FindWindowEx(t,IntPtr.Zero,"Start",null):IntPtr.Zero;
    if(h!=IntPtr.Zero){ RECT r; if(GetWindowRect(h,out r)) sx=(r.left+r.right)/2; }
    return new int[]{sx};
  }

  public static string Flick_(int x,int y0,int dist,int steps,int totalMs,int holdMs,double ease,int topHoldMs){
    if(!Init()) return "InitializeTouchInjection FAILED";
    int y1 = y0 - dist;
    if(!Send(x,y0,DOWN|INRANGE|INCONTACT)) return "DOWN FAILED err="+Marshal.GetLastWin32Error();
    if(holdMs>0) Thread.Sleep(holdMs);
    int per=Math.Max(totalMs/Math.Max(steps,1),1);
    int last=y0;
    for(int i=1;i<=steps;i++){
      double t=(double)i/steps;
      double p;
      // measured from a real finger swipe: the motion ACCELERATES (ease-in),
      // so the contact is still moving fast at release and the shell's fling
      // animation gets a high initial velocity. Negative Ease = ease-in.
      if(ease < -1.0)       p = Math.Pow(t, -ease);
      else if(ease > 1.0)   p = 1.0 - Math.Pow(1.0-t, ease);
      else                  p = t;
      last=y0+(int)Math.Round((y1-y0)*p);
      if(!Send(x,last,UPDATE|INRANGE|INCONTACT)) return "UPDATE "+i+" FAILED err="+Marshal.GetLastWin32Error();
      Thread.Sleep(per);
    }
    if(topHoldMs>0) Thread.Sleep(topHoldMs);
    if(!Send(x,last,UP)) return "UP FAILED err="+Marshal.GetLastWin32Error();
    return "OK";
  }
}
'@

$sh = [Flick]::ScreenH()
if ($sh -le 0) { Write-Host 'touch init FAILED'; exit 1 }
$xs = [Flick]::StartX()
$x  = $xs[0]
$y0 = $sh - 2
$st = if ($Steps -gt 0) { $Steps } else { [Math]::Max(5, [int]($DurationMs / 14.0)) }

Write-Host ''
Write-Host ("  start ({0},{1})   flick {2} px upward   {3} ms   {4} frames   ease={5}   hold={6} ms   topHold={7} ms" -f $x,$y0,$Distance,$DurationMs,$st,$Ease,$HoldMs,$TopHoldMs)
$res = [Flick]::Flick_($x, $y0, $Distance, $st, $DurationMs, $HoldMs, $Ease, $TopHoldMs)
Write-Host ("  result: {0}" -f $res) -ForegroundColor $(if ($res -eq 'OK') { 'Green' } else { 'Red' })
Write-Host ''

