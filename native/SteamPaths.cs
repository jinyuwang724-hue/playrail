using System;
using System.IO;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using Microsoft.Win32;
internal static class SteamPaths {
 public static string Executable() {
  var candidates=new List<string>();
  try {
   var config=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"GamingMode","config.json")));
   var gaming=config["gaming"] as Dictionary<string,object>; object value;
   if(gaming!=null&&gaming.TryGetValue("steamPath",out value)&&value is string)candidates.Add((string)value);
  } catch { }
  using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")) {
   if(key!=null){candidates.Add(key.GetValue("SteamExe") as string); var dir=key.GetValue("SteamPath") as string;if(!String.IsNullOrEmpty(dir))candidates.Add(Path.Combine(dir,"steam.exe"));}
  }
  foreach(var view in new[]{RegistryView.Registry32,RegistryView.Registry64})using(var hive=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,view))using(var key=hive.OpenSubKey(@"SOFTWARE\Valve\Steam")) {
   var dir=key==null?null:key.GetValue("InstallPath") as string;if(!String.IsNullOrEmpty(dir))candidates.Add(Path.Combine(dir,"steam.exe"));
  }
  candidates.Add(@"C:\Software\steam.exe");
  foreach(var candidate in candidates)try {
   if(String.IsNullOrWhiteSpace(candidate))continue;var file=Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"')));
   if(Path.GetFileName(file).Equals("steam.exe",StringComparison.OrdinalIgnoreCase)&&File.Exists(file))return file;
  }catch { }
  throw new FileNotFoundException("未找到 Steam，请在 Playhub 中设置 Steam 路径");
 }
}
