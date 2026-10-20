using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ClawRouter {
    public struct KeyboardStroke {
        public uint Key, Scan, Flags, Time;
        public ulong Extra;
        public bool Up { get { return (Flags & 128) != 0; } }
    }
    public sealed class ShortcutDecision {
        public bool Swallow, GameBar;
    }
    // Steam ownership disables the Game Bar shortcut itself. Downstream hooks
    // can consume a modifier before raw device input arrives; replay on a missing
    // report would launch Game Bar again. Other keys keep their normal delivery.
    public sealed class FirmwareShortcutState {
        int windows;
        bool blockedG;
        public ShortcutDecision Key(KeyboardStroke key, bool enabled) {
            var result = new ShortcutDecision();
            if(key.Extra==0x434C4157)return result; // our intentional mapped shortcut, not the firmware copy
            if(key.Key==0x5b || key.Key==0x5c) {
                int bit=key.Key==0x5b ? 1 : 2;
                if(key.Up) windows &= ~bit; else windows |= bit;
                return result;
            }
            if(key.Key!=0x47)return result;
            if(key.Up) {
                result.Swallow=blockedG;blockedG=false;
                return result;
            }
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
        public long Suppressed {get;private set;}
        public long ReplayFailures {get{return 0;}}
        public event Action GameBarSuppressed;
        public FirmwareShortcutFilter() {
            callback=OnKey;
            CreateHandle(new CreateParams {Parent=new IntPtr(-3)});
            hook=SetWindowsHookEx(13,callback,GetModuleHandle(null),0);
            if(hook==IntPtr.Zero){DestroyHandle();throw new InvalidOperationException("Game Bar 快捷键过滤不可用");}
        }
        public void SetEnabled(bool value) {enabled=value;if(!value)PostMessage(Handle,0x8001,IntPtr.Zero,IntPtr.Zero);}
        IntPtr OnKey(int code,IntPtr message,IntPtr data) {
            if(code<0)return CallNextHookEx(hook,code,message,data);
            var key=(HookKey)Marshal.PtrToStructure(data,typeof(HookKey));
            var decision=state.Key(new KeyboardStroke {Key=key.key,Scan=key.scan,Flags=key.flags,Time=key.time,Extra=key.extra.ToUInt64()},enabled);
            if(decision.GameBar){Suppressed++;var handler=GameBarSuppressed;if(handler!=null)handler();}
            return decision.Swallow ? new IntPtr(1) : CallNextHookEx(hook,code,message,data);
        }
        protected override void WndProc(ref Message message) {
            if(message.Msg==0x8001 && !enabled)state.Disabled();
            base.WndProc(ref message);
        }
        public void Dispose() {
            enabled=false;state.Disabled();
            if(hook!=IntPtr.Zero){UnhookWindowsHookEx(hook);hook=IntPtr.Zero;}
            DestroyHandle();
        }
    }
}
