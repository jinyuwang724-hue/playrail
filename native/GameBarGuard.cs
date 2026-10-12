using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ClawRouter {
    // Windows can activate Game Bar independently of the keyboard hook. Window
    // creation/show events cover that path without an admin-only WMI trace.
    public sealed class GameBarGuard : IDisposable {
        public const string PackageFamily = "Microsoft.XboxGamingOverlay_8wekyb3d8bbwe";
        readonly object gate = new object();
        readonly Action<string,string> log;
        readonly uint session;
        readonly WinEvent callback;
        IntPtr hook;
        bool enabled, disposed;
        int generation;
        long closed, failures;
        public long Closed { get { return Interlocked.Read(ref closed); } }
        public long Failures { get { return Interlocked.Read(ref failures); } }
        public bool Active { get { lock(gate)return enabled && hook!=IntPtr.Zero; } }
        public GameBarGuard(Action<string,string> logger) {
            log=logger;
            if(!ProcessIdToSessionId((uint)Process.GetCurrentProcess().Id,out session))
                throw new InvalidOperationException("无法确认当前 Windows 会话");
            callback=OnWindow;
            // Constructed on the router's message-pump thread. Out-of-context
            // callbacks return on that thread even when acquisition is async.
            hook=SetWinEventHook(0x8000,0x8002,IntPtr.Zero,callback,0,0,2);
            if(hook==IntPtr.Zero)throw new InvalidOperationException("Game Bar 窗口通知不可用");
        }
        public static bool IsTarget(string image, string package, uint targetSession, uint currentSession) {
            string name=Path.GetFileName(image ?? "");
            return targetSession==currentSession && package==PackageFamily &&
                (String.Equals(name,"GameBar.exe",StringComparison.OrdinalIgnoreCase) ||
                 String.Equals(name,"GameBarFTServer.exe",StringComparison.OrdinalIgnoreCase));
        }
        public void SetEnabled(bool value) {
            int lease;
            lock(gate) {
                if(disposed || enabled==value)return;
                generation++;lease=generation;
                enabled=value;
            }
            if(!value)return;
            // Subscribe before the one-time sweep so a racing launch is covered.
            foreach(string name in new[]{"GameBar","GameBarFTServer"})
                foreach(var process in Process.GetProcessesByName(name))
                    using(process)Close((uint)process.Id,lease);
            log("gamebar-guard","Steam 接管期间关闭 Game Bar；已启用窗口创建和显示保护");
        }
        void OnWindow(IntPtr eventHook,uint kind,IntPtr window,int objectId,int childId,uint thread,uint time) {
            if(window==IntPtr.Zero || objectId!=0 || childId!=0 || (kind!=0x8000 && kind!=0x8002))return;
            int lease;
            lock(gate){if(!enabled || disposed)return;lease=generation;}
            uint pid;GetWindowThreadProcessId(window,out pid);
            if(pid==0 || !IsCandidate(pid))return;
            try {Close(pid,lease);}catch(Exception ex){try{Failure(pid,ex.Message);}catch{}}
        }
        static bool IsCandidate(uint pid) {
            IntPtr handle=OpenProcess(0x1000,false,pid);
            if(handle==IntPtr.Zero)return false;
            try {
                uint length=32768;var image=new StringBuilder((int)length);
                if(!QueryFullProcessImageName(handle,0,image,ref length))return false;
                string name=Path.GetFileName(image.ToString());
                return String.Equals(name,"GameBar.exe",StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(name,"GameBarFTServer.exe",StringComparison.OrdinalIgnoreCase);
            } finally {CloseHandle(handle);}
        }
        void Close(uint pid,int lease) {
            lock(gate) {
                if(!enabled || disposed || lease!=generation)return;
                // Keep this same handle from identity verification through close:
                // PID reuse cannot change the process that receives termination.
                IntPtr handle=OpenProcess(0x1001,false,pid);
                if(handle==IntPtr.Zero) {
                    int error=Marshal.GetLastWin32Error();
                    if(error!=87)Failure(pid,"无法打开，错误="+error);
                    return;
                }
                try {
                    uint code,targetSession,length=32768;
                    if(!GetExitCodeProcess(handle,out code) || code!=259)return;
                    var image=new StringBuilder((int)length);
                    if(!ProcessIdToSessionId(pid,out targetSession) || targetSession!=session ||
                        !QueryFullProcessImageName(handle,0,image,ref length))return;
                    string name=Path.GetFileName(image.ToString());
                    if(!String.Equals(name,"GameBar.exe",StringComparison.OrdinalIgnoreCase) &&
                        !String.Equals(name,"GameBarFTServer.exe",StringComparison.OrdinalIgnoreCase))return;
                    length=0;GetPackageFamilyName(handle,ref length,null);
                    if(length==0 || length>1024)return;
                    var family=new StringBuilder((int)length);
                    if(GetPackageFamilyName(handle,ref length,family)!=0 ||
                        !IsTarget(image.ToString(),family.ToString(),targetSession,session))return;
                    if(!TerminateProcess(handle,0)) {Failure(pid,"关闭失败，错误="+Marshal.GetLastWin32Error());return;}
                    Interlocked.Increment(ref closed);
                    log("gamebar-closed",Path.GetFileName(image.ToString())+"，PID="+pid+"，已核对包身份和会话");
                } finally {CloseHandle(handle);}
            }
        }
        void Failure(uint pid,string reason) {Interlocked.Increment(ref failures);log("gamebar-close-failed","PID="+pid+"，"+reason);}
        public void Dispose() {
            SetEnabled(false);
            lock(gate) {disposed=true;if(hook!=IntPtr.Zero){UnhookWinEvent(hook);hook=IntPtr.Zero;}}
        }
        delegate void WinEvent(IntPtr hook,uint kind,IntPtr window,int objectId,int childId,uint thread,uint time);
        [DllImport("user32.dll")]static extern IntPtr SetWinEventHook(uint first,uint last,IntPtr module,WinEvent callback,uint process,uint thread,uint flags);
        [DllImport("user32.dll")]static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
        [DllImport("kernel32.dll",SetLastError=true)]static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
        [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")]static extern bool GetExitCodeProcess(IntPtr handle,out uint code);
        [DllImport("kernel32.dll",SetLastError=true)]static extern bool TerminateProcess(IntPtr handle,uint code);
        [DllImport("kernel32.dll")]static extern bool ProcessIdToSessionId(uint pid,out uint session);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern bool QueryFullProcessImageName(IntPtr handle,uint flags,StringBuilder image,ref uint length);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern int GetPackageFamilyName(IntPtr handle,ref uint length,StringBuilder family);
    }
}
