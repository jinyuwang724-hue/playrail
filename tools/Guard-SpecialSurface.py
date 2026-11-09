from pathlib import Path
from importlib.util import spec_from_file_location,module_from_spec
s=spec_from_file_location('edit',Path(__file__).with_name('Edit-SpecialKeys.py'));m=module_from_spec(s);s.loader.exec_module(m)
LIB=m.LIB
p=LIB/'Stage/WallStage.ControlCenter.cs';t=p.read_text(encoding='utf-8');assert t.count('        CloseSwitcher(instant: true);')==2;t=t.replace('        CloseSwitcher(instant: true);','        if (!_wsStandalone) CloseSwitcher(instant: true);');p.write_text(t,encoding='utf-8')
p=LIB/'Stage/WallStage.ShortcutSurfaces.cs';m.edit(p,'        if (_wsOpen) CloseSwitcher(instant: true);','        if (_wsOpen) CloseSwitcher(instant: true);\n        _wsStandalone = false;')
