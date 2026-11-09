$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class MotionOsdDiagnostic {
 const string Dll=@"C:\Software\RivaTuner Statistics Server\RTSSHooks64.dll";
 [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] public static extern uint GetFlags();
 [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Ansi)] public static extern void LoadProfile(string name);
 [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Ansi)] static extern bool GetProfileProperty(string name,out uint data,uint size);
 [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Ansi)] static extern IntPtr OpenFileMapping(uint access,bool inherit,string name);
 [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr MapViewOfFile(IntPtr h,uint access,uint high,uint low,UIntPtr bytes);
 [DllImport("kernel32.dll")] static extern bool UnmapViewOfFile(IntPtr p);
 [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
 static uint U(IntPtr p,int off) {return unchecked((uint)Marshal.ReadInt32(p,off));}
 static string S(IntPtr p,int off,int max) {var b=new byte[max];Marshal.Copy(IntPtr.Add(p,off),b,0,max);int n=Array.IndexOf(b,(byte)0);return System.Text.Encoding.Default.GetString(b,0,n<0?max:n);}
 public static Dictionary<string,object> Read() {
  var r=new Dictionary<string,object>();r["Flags"]=GetFlags();r["OsdVisible"]=(GetFlags()&1)!=0;
  LoadProfile("");foreach(var name in new[]{"AppDetectionLevel","EnableOSD","EnableStat","FramerateLimit"}) {uint value;r[name]=GetProfileProperty(name,out value,4)?(object)value:"Unavailable";}
  IntPtr h=OpenFileMapping(4,false,"RTSSSharedMemoryV2");if(h==IntPtr.Zero){r["SharedMemoryError"]=Marshal.GetLastWin32Error();return r;}
  IntPtr p=MapViewOfFile(h,4,0,0,UIntPtr.Zero);try {
   if(p==IntPtr.Zero){r["MappingError"]=Marshal.GetLastWin32Error();return r;}
   r["Signature"]=U(p,0).ToString("X8");r["Version"]=U(p,4).ToString("X8");r["OsdFrame"]=U(p,32);
   int appSize=(int)U(p,8),appOff=(int)U(p,12),appCount=(int)U(p,16),osdSize=(int)U(p,20),osdOff=(int)U(p,24),osdCount=(int)U(p,28);
   r["MemoryLayout"]=new {AppEntrySize=appSize,AppOffset=appOff,AppCount=appCount,OsdEntrySize=osdSize,OsdOffset=osdOff,OsdCount=osdCount};
   if(U(p,0)!=0x52545353||appSize<300||appCount>512||osdSize<288||osdCount>512)throw new Exception("Invalid RTSS shared-memory header");
   var apps=new List<object>();for(int i=0;i<appCount;i++){int o=appOff+i*appSize;uint pid=U(p,o);if(pid==0)continue;uint t0=U(p,o+268),t1=U(p,o+272),frames=U(p,o+276);apps.Add(new{ProcessId=pid,Name=S(p,o+4,260),Flags=U(p,o+264).ToString("X8"),Frames=frames,Framerate=t1>t0?1000.0*frames/(t1-t0):0,OsdFrame=U(p,o+332)});}r["Applications"]=apps;
   var slots=new List<object>();for(int i=0;i<osdCount;i++){int o=osdOff+i*osdSize;string owner=S(p,o+256,256);if(owner.Length==0)continue;string content=osdSize>=299520?S(p,o+266752,32768):"";if(content.Length==0&&osdSize>=4608)content=S(p,o+512,4096);if(content.Length==0)content=S(p,o,256);slots.Add(new{Index=i,Owner=owner,Text=content});}r["OsdSlots"]=slots;
  }finally {if(p!=IntPtr.Zero)UnmapViewOfFile(p);CloseHandle(h);}return r;
 }
}
'@
$taskResult = [MotionOsdDiagnostic]::Read()
$taskResult['CapturedAt'] = (Get-Date).ToString('o')
try { $taskResult['Playhub'] = Invoke-RestMethod 'http://127.0.0.1:47991/mode/current' -TimeoutSec 3 } catch { $taskResult['Playhub'] = 'Unavailable' }
$taskResult | ConvertTo-Json -Depth 6
