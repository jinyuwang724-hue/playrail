param([string]$Report=(Join-Path $PSScriptRoot '..\artifacts\sleep-animation\settings-v2\pipe-acl.json'))
$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;using System.ComponentModel;
public static class SleepPipeAcl {
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateFile(string n,uint a,uint s,IntPtr q,uint c,uint f,IntPtr t);
 [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
 [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
 [DllImport("advapi32.dll")] static extern uint GetSecurityInfo(IntPtr h,int type,uint flags,out IntPtr o,out IntPtr g,out IntPtr d,out IntPtr s,out IntPtr sd);
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(IntPtr sd,uint rev,uint info,out IntPtr s,out uint size);
 public static string Read(string name) {
  var h=CreateFile(name,0xc0000000,0,IntPtr.Zero,3,0,IntPtr.Zero);if(h==new IntPtr(-1))throw new Win32Exception(Marshal.GetLastWin32Error());
  try {IntPtr o,g,d,s,sd;uint r=GetSecurityInfo(h,6,0x17,out o,out g,out d,out s,out sd);if(r!=0)throw new Win32Exception((int)r);
   try {IntPtr str;uint size;if(!ConvertSecurityDescriptorToStringSecurityDescriptor(sd,1,0x17,out str,out size))throw new Win32Exception(Marshal.GetLastWin32Error());try{return Marshal.PtrToStringUni(str);}finally{LocalFree(str);}}
   finally{LocalFree(sd);}
  }finally{CloseHandle(h);}
 }
}
'@
$taskSession=(Get-Process -Id $PID).SessionId
@{session=$taskSession;sddl=[SleepPipeAcl]::Read('\\.\pipe\ClawSleepAnimation-'+$taskSession)}|ConvertTo-Json|Set-Content -LiteralPath $Report -Encoding UTF8
