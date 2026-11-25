param([ValidateRange(10, 3600)][int]$Seconds = 330)
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Threading;
public static class NativeFpsProbe {
 [DllImport("d3d9.dll")] static extern IntPtr Direct3DCreate9(uint sdk);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int w,int h,IntPtr p,IntPtr menu,IntPtr inst,IntPtr param);
 [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
 [StructLayout(LayoutKind.Sequential)] struct PP { public uint Width,Height,Format,Count,Multi,Quality,Swap; public IntPtr Window; public int Windowed,Depth; public uint DepthFormat,Flags,Refresh,Interval; }
 [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Create(IntPtr self,uint adapter,uint type,IntPtr window,uint flags,ref PP p,out IntPtr device);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Clear(IntPtr self,uint count,IntPtr rect,uint flags,uint color,float z,uint stencil);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Present(IntPtr self,IntPtr src,IntPtr dst,IntPtr window,IntPtr dirty);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint Release(IntPtr self);
 static T Method<T>(IntPtr p,int slot) where T:Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(p),slot*IntPtr.Size));
 public static string Run(int seconds) {
  var h=CreateWindowEx(0,"STATIC","Direct3D FPS 实机验证",0x10CF0000,80,80,360,200,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
  var api=Direct3DCreate9(32); IntPtr dev=IntPtr.Zero;
  if(h==IntPtr.Zero||api==IntPtr.Zero) throw new Exception("D3D initialization unavailable");
  try {
   var p=new PP { Count=1,Swap=1,Window=h,Windowed=1,Interval=1 };
   int hr=Method<Create>(api,16)(api,0,1,h,0x20,ref p,out dev); if(hr<0) Marshal.ThrowExceptionForHR(hr);
   var clear=Method<Clear>(dev,43); var present=Method<Present>(dev,17); var clock=Stopwatch.StartNew(); int count=0;
   while(clock.Elapsed.TotalSeconds<seconds) { hr=clear(dev,0,IntPtr.Zero,1,(uint)(0xFF000000 | ((count*9)&255)<<16 | 0x000080FF),1,0); if(hr<0) Marshal.ThrowExceptionForHR(hr); hr=present(dev,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero); if(hr<0) Marshal.ThrowExceptionForHR(hr); count++; Thread.Sleep(16); }
   return $"SuccessfulPresents={count}; Seconds={clock.Elapsed.TotalSeconds:F3}; MeasuredCallsPerSecond={count/clock.Elapsed.TotalSeconds:F3}";
  } finally { if(dev!=IntPtr.Zero) Method<Release>(dev,2)(dev); Method<Release>(api,2)(api); DestroyWindow(h); }
 }
}
'@
[NativeFpsProbe]::Run($Seconds)
