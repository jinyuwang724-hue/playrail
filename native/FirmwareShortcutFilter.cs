using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ClawRouter {
    // The Claw's Win+G keyboard report precedes its MSI_Event notification
    // (normally by about 300 ms). Never replay that report as a second press.
    public sealed class GameBarShortcutCorrelation {
        public const int ReplayDelayMs = 700;
        readonly object gate = new object();
        readonly List<double> quickSettings = new List<double>();
        int generation;
        public int Generation { get { lock(gate)return generation; } }
        public void QuickSettings(double atMs) {
            lock(gate) {
                quickSettings.RemoveAll(delegate(double at){return atMs-at>ReplayDelayMs;});
                quickSettings.Add(atMs);
            }
        }
        public bool ShouldReplay(double shortcutMs, double nowMs, int epoch) {
            lock(gate) {
                if(epoch!=generation || nowMs-shortcutMs<ReplayDelayMs)return false;
                foreach(double at in quickSettings)
                    if(Math.Abs(at-shortcutMs)<=ReplayDelayMs)return false;
                return true;
            }
        }
        public void Reset() {lock(gate){generation++;quickSettings.Clear();}}
    }
    public sealed class SpecialShortcutCorrelation {
        readonly object gate=new object();
        readonly List<KeyValuePair<int,double>> pending=new List<KeyValuePair<int,double>>();
        public void Keyboard(int code,double at){lock(gate){pending.RemoveAll(x=>at-x.Value>900);pending.Add(new KeyValuePair<int,double>(code,at));}}
        public bool Consume(int code,double at){lock(gate){pending.RemoveAll(x=>at-x.Value>900);for(int i=0;i<pending.Count;i++)if(pending[i].Key==code&&at>=pending[i].Value){pending.RemoveAt(i);return true;}return false;}}
    }
    public struct KeyboardStroke {
        public uint Key, Scan, Flags, Time;
        public ulong Extra;
        public bool Up { get { return (Flags & 128) != 0; } }
    }
    public sealed class ShortcutDecision {
        public bool Swallow, GameBar, Switcher, ObservedGameBar;
    }
    // Steam ownership disables the Game Bar shortcut itself. Downstream hooks
    // can consume a modifier before raw device input arrives; replay on a missing
    // report would launch Game Bar again. Other keys keep their normal delivery.
    public sealed class FirmwareShortcutState {
        int windows;
        bool blockedG, blockedTab, observedG;
        public ShortcutDecision Key(KeyboardStroke key, bool enabled) { return Key(key,enabled,false); }
        public ShortcutDecision Key(KeyboardStroke key, bool enabled, bool taskView) {
            var result = new ShortcutDecision();
            if(key.Extra==0x434C4157)return result; // our intentional mapped shortcut, not the firmware copy
            if(key.Key==0x5b || key.Key==0x5c) {
                int bit=key.Key==0x5b ? 1 : 2;
                if(key.Up) windows &= ~bit; else windows |= bit;
                return result;
            }
            if(key.Key==0x09) {
                if(key.Up){result.Swallow=blockedTab;blockedTab=false;return result;}
                if(taskView && windows!=0){result.Switcher=!blockedTab;result.Swallow=true;blockedTab=true;}
                else blockedTab=false;
                return result;
            }
            if(key.Key!=0x47)return result;
            if(key.Up) {
                result.Swallow=blockedG;blockedG=false;observedG=false;
                return result;
            }
            result.ObservedGameBar=windows!=0&&!observedG;
            if(windows!=0)observedG=true;
            if(enabled && windows!=0) {
                result.GameBar=!blockedG;
                result.Swallow=true;blockedG=true;
            } else {
                // A lost release must not swallow later ordinary typing.
                blockedG=false;
            }
            return result;
        }
        public void Disabled() { windows=0; }
    }
    public sealed class FirmwareShortcutFilter : NativeWindow, IDisposable {
        [StructLayout(LayoutKind.Sequential)] struct HookKey {public uint key,scan,flags,time;public UIntPtr extra;}
        delegate IntPtr Hook(int code,IntPtr message,IntPtr data);
        [DllImport("user32.dll",SetLastError=true)]static extern IntPtr SetWindowsHookEx(int type,Hook hook,IntPtr module,uint thread);
        [DllImport("user32.dll")]static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
        [DllImport("user32.dll")]static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")]static extern bool PostMessage(IntPtr window,uint message,IntPtr param,IntPtr data);
        readonly FirmwareShortcutState state=new FirmwareShortcutState();
        readonly Hook callback;
        IntPtr hook;
        volatile bool enabled;
        readonly Func<bool> gameMode;
        readonly RawKeyboardSource keyboardSource;
        public event Action<string,IntPtr,bool> ShortcutPressed;
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra);
        public bool RawReady {get{return keyboardSource!=null&&keyboardSource.Ready;}}
        public int FirmwareWindows {get{return keyboardSource==null?0:keyboardSource.FirmwareWindows;}}
        public long Suppressed {get;private set;}
        public long ReplayFailures {get{return 0;}}
        public event Action GameBarSuppressed;
        /// <summary>保留 explorer 的游戏模式：单按 Win 不能弹开始菜单（会把藏着的任务栏一起带出来）。</summary>
        public static volatile bool CoveredShell;
        bool winHeld, winTainted;
        public FirmwareShortcutFilter() : this(null) {}
        public FirmwareShortcutFilter(Func<bool> gameMode) {
            this.gameMode=gameMode;
            if(gameMode!=null)keyboardSource=new RawKeyboardSource();
            callback=OnKey;
            CreateHandle(new CreateParams {Parent=new IntPtr(-3)});
            hook=SetWindowsHookEx(13,callback,GetModuleHandle(null),0);
            if(hook==IntPtr.Zero){DestroyHandle();throw new InvalidOperationException("Game Bar 快捷键过滤不可用");}
        }
        public void SetEnabled(bool value) {enabled=value;if(!value)PostMessage(Handle,0x8001,IntPtr.Zero,IntPtr.Zero);}
        IntPtr OnKey(int code,IntPtr message,IntPtr data) {
            if(code<0)return CallNextHookEx(hook,code,message,data);
            var key=(HookKey)Marshal.PtrToStructure(data,typeof(HookKey));
            var stroke=new KeyboardStroke {Key=key.key,Scan=key.scan,Flags=key.flags,Time=key.time,Extra=key.extra.ToUInt64()};
            bool game=gameMode!=null&&gameMode();
            if(keyboardSource!=null&&(key.key==91||key.key==92)&&!stroke.Up)keyboardSource.Begin(key.time);
            bool firmwareTab=keyboardSource!=null&&!game&&key.key==9&&!stroke.Up&&keyboardSource.Firmware(key.time);
            var decision=state.Key(stroke,gameMode==null?enabled:game,game||firmwareTab);
            string action=null;
            if(decision.GameBar){Suppressed++;if(gameMode==null){var old=GameBarSuppressed;if(old!=null)old();}else action="controlCenter";}
            if(decision.Switcher) {
                action=game?"windowSwitcher":"gamebar";
                // A swallowed firmware Tab must not turn the trailing Windows
                // release into a Start menu tap. Preserve real Ctrl state.
                if(!game&&GetAsyncKeyState(0x11)>=0){var tag=new UIntPtr(0x434C4157u);keybd_event(0x11,0,0,tag);keybd_event(0x11,0,2,tag);}
            }
            if(gameMode!=null&&!game&&decision.ObservedGameBar)action="gamebarInput";
            if(action!=null){var handler=ShortcutPressed;if(handler!=null)handler(action,GetForegroundWindow(),game);}
            if(key.key==0x5b||key.key==0x5c){
                if(!stroke.Up){if(!winHeld){winHeld=true;winTainted=false;}}
                else{
                    // 中间没有别的键（固件的 G / Tab 被我们吞了）：先插一个 Ctrl 再放 Win 抬起，系统就不当成单按 Win
                    if(CoveredShell&&winHeld&&!winTainted){var tag=new UIntPtr(0x434C4157u);keybd_event(0x11,0,0,tag);keybd_event(0x11,0,2,tag);}
                    winHeld=false;
                }
            } else if(winHeld&&!decision.Swallow)winTainted=true;
            return decision.Swallow ? new IntPtr(1) : CallNextHookEx(hook,code,message,data);
        }
        protected override void WndProc(ref Message message) {
            if(message.Msg==0x8001 && !enabled)state.Disabled();
            base.WndProc(ref message);
        }
        public void Dispose() {
            enabled=false;state.Disabled();
            if(keyboardSource!=null)keyboardSource.Dispose();
            if(hook!=IntPtr.Zero){UnhookWindowsHookEx(hook);hook=IntPtr.Zero;}
            DestroyHandle();
        }
    }
}
