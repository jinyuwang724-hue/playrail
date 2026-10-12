using System;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Web.Script.Serialization;
namespace ClawCompatibility {
 [ComImport,Guid("00021401-0000-0000-C000-000000000046")] class ShellLink { }
 [ComImport,Guid("000214F9-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IShellLink {
  void GetPath([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder path,int count,IntPtr data,uint flags);
 }
 public static class Program {
  static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
  [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
  [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
  [DllImport("advapi32.dll",SetLastError=true)] static extern bool GetTokenInformation(IntPtr token,int kind,out int value,int size,out int returned);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
  static string Elevation(int pid) {
   IntPtr process=OpenProcess(0x1000,false,pid),token=IntPtr.Zero;
   if(process==IntPtr.Zero)return "Unknown";
   try{int value,size;if(!OpenProcessToken(process,8,out token)||!GetTokenInformation(token,20,out value,4,out size))return "Unknown";return value==0?"Normal":"Elevated";}
   finally{if(token!=IntPtr.Zero)CloseHandle(token);CloseHandle(process);}
  }
  static string LinkPath(string slot) {
   if(slot!="user"&&slot!="common")throw new ArgumentException("不支持的快捷方式位置");
   return Path.Combine(Environment.GetFolderPath(slot=="user"?Environment.SpecialFolder.StartMenu:Environment.SpecialFolder.CommonStartMenu),"Programs","Steam","Steam.lnk");
  }
  static bool TargetMatches(string file,string steam) {
   object link=new ShellLink();
   try{((IPersistFile)link).Load(file,0);var text=new StringBuilder(32768);((IShellLink)link).GetPath(text,text.Capacity,IntPtr.Zero,4);return Path.GetFullPath(text.ToString()).Equals(steam,StringComparison.OrdinalIgnoreCase);}
   finally{Marshal.ReleaseComObject(link);}
  }
  static Dictionary<string,object> Links(string steam) {
   var result=new Dictionary<string,object>();
   foreach(var slot in new[]{"user","common"}) {
    string file=LinkPath(slot);bool exists=File.Exists(file);bool matches=exists&&TargetMatches(file,steam);
    result[slot]=new Dictionary<string,object>{{"present",exists},{"matches",matches},{"admin",matches&&(File.ReadAllBytes(file)[0x15]&0x20)!=0}};
   }
   return result;
  }
  static object Inspect() {
   var steam=SteamPaths.Executable();var processes=new List<object>();
   foreach(var process in Process.GetProcessesByName("steam"))try{processes.Add(new{pid=process.Id,elevation=Elevation(process.Id)});}finally{process.Dispose();}
   bool? home=null;string homeError=null;try{home=GameBarHomePreference.Read();}catch(Exception ex){homeError=ex.Message;}
   return new{steamPath=steam,steamProcesses=processes,home=home,homeError=homeError,shortcuts=Links(steam)};
  }
  static object Execute(Dictionary<string,object> request) {
   string operation=request["op"] as string;
   if(operation=="inspect")return Inspect();
   if(operation=="home") {
    object value;if(!request.TryGetValue("value",out value)||!(value is bool))throw new ArgumentException("主页设置格式错误");
    GameBarHomePreference.Set((bool)value);return new{home=GameBarHomePreference.Read()};
   }
   if(operation=="shortcuts") {
    var values=request["values"] as Dictionary<string,object>;if(values==null)throw new ArgumentException("快捷方式设置格式错误");
    string steam=SteamPaths.Executable();
    foreach(var item in values) {
     if(!(item.Value is bool))throw new ArgumentException("快捷方式开关格式错误");
     string file=LinkPath(item.Key);if(!File.Exists(file))continue;
     if(!TargetMatches(file,steam))throw new InvalidOperationException("Steam 快捷方式目标已改变，未覆盖");
     byte[] data=File.ReadAllBytes(file);if(data.Length<76||data[0]!=0x4c)throw new InvalidDataException("快捷方式格式错误");
     bool current=(data[0x15]&0x20)!=0;if(current==(bool)item.Value)continue;
     data[0x15]=(byte)((bool)item.Value?data[0x15]|0x20:data[0x15]&~0x20);File.WriteAllBytes(file,data);
    }
    return Links(steam);
   }
   throw new ArgumentException("不支持的兼容性操作");
  }
  [STAThread] public static int Main(string[] args) {
   if(args.Length!=1)return 2;
   string input=Path.GetFullPath(args[0]),output=input+".result";
   try {
    if(new FileInfo(input).Length>16384)throw new InvalidDataException("请求过大");
    var data=Execute(Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(input)));
    File.WriteAllText(output+".tmp",Json.Serialize(new{ok=true,data=data}),new UTF8Encoding(false));File.Move(output+".tmp",output);return 0;
   }catch(Exception ex){File.WriteAllText(output+".tmp",Json.Serialize(new{ok=false,error=ex.Message,requiresAdmin=ex is UnauthorizedAccessException}),new UTF8Encoding(false));File.Move(output+".tmp",output);return 1;}
  }
 }
}
