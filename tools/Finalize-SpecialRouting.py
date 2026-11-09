from pathlib import Path
from importlib.util import spec_from_file_location,module_from_spec
s=spec_from_file_location('edit',Path(__file__).with_name('Edit-SpecialKeys.py'));m=module_from_spec(s);s.loader.exec_module(m)
edit=m.edit;SRC=m.SRC;LIB=m.LIB
p=LIB/'MainWindow.Overlay.cs'
edit(p,'            if (special && !game)','            if (!game && route.StartsWith("minimize-", StringComparison.Ordinal))\n            { _stage?.CloseSpecialSurfaces(); CompleteShortcutHide(returnToPrevious: false); return; }\n            if (special && !game)')
p=SRC/'plugin/claw_service.py'
edit(p,'                else:\n                    await self.set_overlay_hold(False)\n                if epoch', '''                else:
                    await self.set_overlay_hold(False)
                    # The login shell may still be the older in-memory build.
                    # Complete every exit path with the updated desktop handoff.
                    shell = Path(os.environ['LOCALAPPDATA']) / 'ClawToolbox/shell/ClawShell.exe'
                    if shell.is_file():
                        await asyncio.to_thread(subprocess.run, [str(shell), 'to-desktop'],
                            creationflags=subprocess.CREATE_NO_WINDOW, timeout=15, check=True)
                if epoch''')
p=SRC/'native/ClawRouter.cs'
edit(p,'        long lastKeyboardShortcut;','        readonly SpecialShortcutCorrelation specialKeys = new SpecialShortcutCorrelation();')
edit(p,'            if(action!="gamebarInput")Interlocked.Exchange(ref lastKeyboardShortcut,at);','            if(action!="gamebarInput")specialKeys.Keyboard(action=="windowSwitcher"||action=="gamebar"?42:88,at*1000.0/Stopwatch.Frequency);')
edit(p,'                    double age=(now-Interlocked.Read(ref lastKeyboardShortcut))*1000.0/Stopwatch.Frequency;\n                    if(age>=0&&age<900)', '                    if(specialKeys.Consume(code,now*1000.0/Stopwatch.Frequency))')
edit(p,'                if(diagnostic || maintenance!=null)return;','                if(diagnostic || maintenance!=null || suspended)return;')
p=SRC/'native/FirmwareShortcutFilter.cs'
edit(p,'    public struct KeyboardStroke {','''    public sealed class SpecialShortcutCorrelation {
        readonly object gate=new object();
        readonly List<KeyValuePair<int,double>> pending=new List<KeyValuePair<int,double>>();
        public void Keyboard(int code,double at){lock(gate){pending.RemoveAll(x=>at-x.Value>900);pending.Add(new KeyValuePair<int,double>(code,at));}}
        public bool Consume(int code,double at){lock(gate){pending.RemoveAll(x=>at-x.Value>900);for(int i=0;i<pending.Count;i++)if(pending[i].Key==code&&at>=pending[i].Value){pending.RemoveAt(i);return true;}return false;}}
    }
    public struct KeyboardStroke {''')
