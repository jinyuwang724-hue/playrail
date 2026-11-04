using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
namespace ClawRouter {
    // Updated off the low-level hook. Actual Explorer ownership wins over stale state.
    public sealed class SessionModeProbe : IDisposable {
        [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
        readonly string path;readonly Action<bool> changed;readonly Timer timer;
        IntPtr lastShell;bool explorer, savedGame, savedCovered;DateTime stamp;int busy;volatile bool game;
        public bool Game {get{return game;}}
        public SessionModeProbe(string path,Action<bool> changed){this.path=path;this.changed=changed;Refresh();timer=new Timer(delegate{Refresh();},null,50,50);}
        void Refresh(){if(Interlocked.Exchange(ref busy,1)!=0)return;try{
            var shell=GetShellWindow();if(shell!=lastShell){lastShell=shell;explorer=false;if(shell!=IntPtr.Zero){uint pid;GetWindowThreadProcessId(shell,out pid);try{using(var p=Process.GetProcessById((int)pid))explorer=p.ProcessName.Equals("explorer",StringComparison.OrdinalIgnoreCase);}catch{}}}
            try{var at=File.GetLastWriteTimeUtc(path);if(at!=stamp){stamp=at;var json=new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string,object>>(File.ReadAllText(path));object value;savedGame=json.TryGetValue("mode",out value)&&Equals(value,"game");savedCovered=json.TryGetValue("covered",out value)&&Equals(value,true);}}catch{}
            // 保留 explorer 的游戏模式（ClawShell 1.0.3，covered:true）：explorer 还是外壳，但任务栏藏着、游戏库盖着，照游戏模式处理
            FirmwareShortcutFilter.CoveredShell=explorer&&savedGame&&savedCovered;
            bool next=savedGame&&(!explorer||savedCovered);if(next!=game){game=next;if(changed!=null)changed(next);}
        }finally{Interlocked.Exchange(ref busy,0);}}
        public void Dispose(){timer.Dispose();}
    }
}
