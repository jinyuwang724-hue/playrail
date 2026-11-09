using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
namespace ClawShell {
    public sealed partial class WindowsShellSystem {
        const string SurfaceMessage="ClawToolbox.Library.Activation.v1";
        delegate bool SurfaceEnum(IntPtr window,IntPtr data);
        [DllImport("user32.dll")] static extern bool EnumWindows(SurfaceEnum callback,IntPtr data);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetProp(IntPtr window,string name);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsHungAppWindow(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window,uint message,IntPtr wp,IntPtr lp);
        IntPtr FindSurface() {
            IntPtr found=IntPtr.Zero;
            EnumWindows(delegate(IntPtr window,IntPtr ignored){
                if(GetProp(window,SurfaceMessage)==IntPtr.Zero)return true;
                uint pid;GetWindowThreadProcessId(window,out pid);
                try {using(var process=Process.GetProcessById((int)pid)){
                    if(process.SessionId==_session&&process.ProcessName.Equals("ClawLibrary",StringComparison.OrdinalIgnoreCase)){found=window;return false;}
                }}catch{}
                return true;
            },IntPtr.Zero);
            return found;
        }
        private void RevealGameModeLibrary() {
            IntPtr window = FindSurface();
            uint message = RegisterWindowMessage(SurfaceMessage);
            if (window != IntPtr.Zero && message != 0)
                PostMessage(window, message, new IntPtr(10), IntPtr.Zero);
        }
        public bool EnsureLibrarySurface(bool activate) {
            IntPtr window=FindSurface();
            if(window==IntPtr.Zero)return false;
            bool ready=IsWindowVisible(window)&&!IsIconic(window)&&!IsHungAppWindow(window);
            if(ready&&!activate)return true;
            uint message=RegisterWindowMessage(SurfaceMessage);
            if(message==0||!PostMessage(window,message,new IntPtr(activate?8:9),IntPtr.Zero))return false;
            for(int waited=0;waited<(activate?3000:500);waited+=50){
                System.Threading.Thread.Sleep(50);
                if(IsWindowVisible(window)&&!IsIconic(window)&&!IsHungAppWindow(window)&&(!activate||GetForegroundWindow()==window))return true;
            }
            Log("library surface did not become visible");return false;
        }
    }
}
