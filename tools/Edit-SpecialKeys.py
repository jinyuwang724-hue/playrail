from pathlib import Path
import re

ROOT=Path(__file__).resolve().parents[1]
SRC=ROOT/'artifacts/special-keys-197/src'
LIB=SRC/'frontend/library-native/ClawLibrary'

def edit(path,old,new):
    text=path.read_text(encoding='utf-8-sig')
    if text.count(old)!=1:raise RuntimeError(str(path)+': '+old[:90])
    path.write_text(text.replace(old,new,1),encoding='utf-8')

def native():
    p=SRC/'native/FirmwareShortcutFilter.cs'
    edit(p,'public bool Swallow, GameBar;','public bool Swallow, GameBar, Switcher, ObservedGameBar;')
    edit(p,'        bool blockedG;','        bool blockedG, blockedTab, observedG;')
    edit(p,'        public ShortcutDecision Key(KeyboardStroke key, bool enabled) {','        public ShortcutDecision Key(KeyboardStroke key, bool enabled) { return Key(key,enabled,false); }\n        public ShortcutDecision Key(KeyboardStroke key, bool enabled, bool taskView) {')
    edit(p,'            if(key.Key!=0x47)return result;', '''            if(key.Key==0x09) {
                if(key.Up){result.Swallow=blockedTab;blockedTab=false;return result;}
                if(taskView && windows!=0){result.Switcher=!blockedTab;result.Swallow=true;blockedTab=true;}
                else blockedTab=false;
                return result;
            }
            if(key.Key!=0x47)return result;''')
    edit(p,'                result.Swallow=blockedG;blockedG=false;','                result.Swallow=blockedG;blockedG=false;observedG=false;')
    edit(p,'            if(enabled && windows!=0) {','            result.ObservedGameBar=windows!=0&&!observedG;\n            if(windows!=0)observedG=true;\n            if(enabled && windows!=0) {')
    edit(p,'        volatile bool enabled;','        volatile bool enabled;\n        readonly Func<bool> gameMode;\n        readonly RawKeyboardSource keyboardSource;\n        public event Action<string,IntPtr,bool> ShortcutPressed;\n        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();\n        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);\n        [DllImport("user32.dll")] static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra);')
    edit(p,'        public FirmwareShortcutFilter() {','        public FirmwareShortcutFilter() : this(null) {}\n        public FirmwareShortcutFilter(Func<bool> gameMode) {\n            this.gameMode=gameMode;\n            if(gameMode!=null)keyboardSource=new RawKeyboardSource();')
    start=p.read_text(encoding='utf-8').index('            var decision=state.Key(')
    end=p.read_text(encoding='utf-8').index('            return decision.Swallow',start)
    text=p.read_text(encoding='utf-8')
    text=text[:start]+'''            var stroke=new KeyboardStroke {Key=key.key,Scan=key.scan,Flags=key.flags,Time=key.time,Extra=key.extra.ToUInt64()};
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
''' + text[end:]
    p.write_text(text,encoding='utf-8')
    edit(p,'            enabled=false;state.Disabled();','            enabled=false;state.Disabled();\n            if(keyboardSource!=null)keyboardSource.Dispose();')

    p=SRC/'native/ClawRouter.cs'
    edit(p,'        FirmwareShortcutFilter shortcutFilter;','        FirmwareShortcutFilter shortcutFilter;\n        SessionModeProbe sessionMode;\n        volatile bool sessionGameMode;\n        long lastKeyboardShortcut;')
    edit(p,'try {shortcutFilter=new FirmwareShortcutFilter();shortcutFilter.GameBarSuppressed+=OnSuppressedGamebar;}',
         'try {sessionMode=new SessionModeProbe(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ClawToolbox","shell","state.json"),OnSessionMode);sessionGameMode=sessionMode.Game;shortcutFilter=new FirmwareShortcutFilter(delegate{return sessionGameMode&&!suspended&&maintenance==null;});shortcutFilter.GameBarSuppressed+=OnSuppressedGamebar;shortcutFilter.ShortcutPressed+=OnImmediateShortcut;}')
    edit(p,'        bool GeneralWanted() { return !diagnostic && !generalFailed && !(mode=="Unknown" && steamWindowOpen) && Policy.GeneralContext(mode,suspended,generalMappings.Count!=0,steamActive); }',
         '        bool GeneralWanted() { return !diagnostic && !generalFailed && !suspended && (sessionGameMode || !(mode=="Unknown" && steamWindowOpen) && Policy.GeneralContext(mode,suspended,generalMappings.Count!=0,steamActive)); }')
    edit(p,'        bool CanFilter() { return Policy.CanRoute(enabled,ready,mode,released) || GeneralWanted() && released && !Policy.IsGameBarCommand(Get(generalMappings,"QS")); }',
         '        bool CanFilter() { return sessionGameMode && !suspended && maintenance==null; }')
    edit(p,'gameBarGuard.SetEnabled(Policy.CanRoute(enabled,ready,mode,released) && !AllowsConfiguredGamebar() && !gamebarPermission.Requested);',
         'gameBarGuard.SetEnabled(sessionGameMode && !gamebarPermission.Requested);')
    at='        void OnSuppressedGamebar() {'
    edit(p,at,'''        void OnSessionMode(bool game) {
            sessionGameMode=game;
            Task.Run((Action)delegate {lock(gate){if(disposed)return;gamebarShortcuts.Reset();Reconcile();Emit(new Dictionary<string,object>{{"type","session_mode"},{"game",game}});Status();}});
        }
        void OnImmediateShortcut(string action,IntPtr foreground,bool game) {
            long at=Stopwatch.GetTimestamp();
            if(action!="gamebarInput")Interlocked.Exchange(ref lastKeyboardShortcut,at);
            Task.Run((Action)delegate {lock(gate){
                if(disposed||maintenance!=null||suspended||game!=sessionGameMode)return;
                if(action=="gamebarInput"){Emit(new Dictionary<string,object>{{"type","desktop_gamebar_input"}});return;}
                routed++;
                Emit(new Dictionary<string,object>{{"type","mapped_action"},{"source","QS"},{"scope","general"},
                    {"command",new Dictionary<string,object>{{"type","system"},{"action",action}}},
                    {"gesture",action=="windowSwitcher"?"long":"short"},{"gameMode",game},{"foregroundWindow",foreground.ToInt64()},
                    {"nativeMs",(Stopwatch.GetTimestamp()-at)*1000.0/Stopwatch.Frequency}});
            }});
        }
''' + at)
    edit(p,'if(code != 41 && code != 88)return;','if(code != 41 && code != 88 && code != 42)return;')
    edit(p,'                if(ReadMode()=="Unknown" && steamActive) {', '''                if(code==88 || code==42) {
                    double age=(now-Interlocked.Read(ref lastKeyboardShortcut))*1000.0/Stopwatch.Frequency;
                    if(age>=0&&age<900){Log("special-key-correlated","delayed WMI "+code+" already handled by keyboard path");return;}
                    if(sessionGameMode)EmitMapping("QS",new Dictionary<string,object>{{"type","system"},{"action",code==42?"windowSwitcher":"controlCenter"}},"general");
                    else if(code==42)EmitMapping("QS",new Dictionary<string,object>{{"type","system"},{"action","gamebar"}},"general");
                    else Emit(new Dictionary<string,object>{{"type","desktop_gamebar_input"}});
                    return;
                }
                if(ReadMode()=="Unknown" && steamActive) {''')
    edit(p,'            if(shortcutFilter!=null){shortcutFilter.Dispose();shortcutFilter=null;}',
         '            if(shortcutFilter!=null){shortcutFilter.Dispose();shortcutFilter=null;}\n            if(sessionMode!=null){sessionMode.Dispose();sessionMode=null;}')

if __name__=='__main__':native()
