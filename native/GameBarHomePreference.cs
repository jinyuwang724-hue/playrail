using System;
using System.IO;
using System.Runtime.InteropServices;
public static class GameBarHomePreference {
 [DllImport("advapi32.dll", CharSet=CharSet.Unicode)] static extern int RegLoadAppKeyW(string file,out IntPtr key,int access,int options,int reserved);
 [DllImport("advapi32.dll", CharSet=CharSet.Unicode)] static extern int RegOpenKeyExW(IntPtr key,string sub,int options,int access,out IntPtr result);
 [DllImport("advapi32.dll", CharSet=CharSet.Unicode)] static extern int RegQueryValueExW(IntPtr key,string name,IntPtr reserved,out int type,byte[] data,ref int length);
 [DllImport("advapi32.dll", CharSet=CharSet.Unicode)] static extern int RegSetValueExW(IntPtr key,string name,int reserved,int type,byte[] data,int length);
 [DllImport("advapi32.dll")] static extern int RegCloseKey(IntPtr key);
 public static bool Read() { return Access(false, false); }
 public static bool Set(bool home) { return Access(true, home); }
 static bool Access(bool write,bool home) {
  string file=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Packages","Microsoft.XboxGamingOverlay_8wekyb3d8bbwe","Settings","settings.dat");
  IntPtr hive=IntPtr.Zero,key=IntPtr.Zero;
  try {
   int access=write?131103:131097;
   int error=RegLoadAppKeyW(file,out hive,access,0,0);
   if(error!=0)throw new IOException("Game Bar settings load failed: "+error);
   error=RegOpenKeyExW(hive,"LocalState",0,access,out key);
   if(error!=0)throw new IOException("Game Bar settings container failed: "+error);
   byte[] data=new byte[64]; int length=data.Length,type;
   error=RegQueryValueExW(key,"InvokeToHomeWidget",IntPtr.Zero,out type,data,ref length);
   if(error!=0&&error!=2)throw new IOException("Game Bar preference read failed: "+error);
   if(!write)return error==2||length<1||data[0]!=0;
   if(error==2||length<1){ type=100000011;length=9;data=new byte[9];BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data,1); }
   data[0]=home?(byte)1:(byte)0;
   error=RegSetValueExW(key,"InvokeToHomeWidget",0,type,data,length);
   if(error!=0)throw new IOException("Game Bar preference write failed: "+error);
   return home;
  } finally { if(key!=IntPtr.Zero)RegCloseKey(key);if(hive!=IntPtr.Zero)RegCloseKey(hive); }
 }
}
