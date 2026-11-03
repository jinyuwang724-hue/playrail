from pathlib import Path
import hashlib,json,os,shutil,subprocess,sys
ROOT=Path(__file__).resolve().parents[1];OUT=ROOT/'artifacts/special-keys-197';UPDATE=OUT/'navigation-update';HOME=Path(os.environ['LOCALAPPDATA'])/'ClawToolbox/app'
names=('claw_service.py','claw_input_layer.py','claw_gamebar_visibility.py')
target=UPDATE/'src/plugin';target.mkdir(parents=True,exist_ok=True)
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
changes={}
for name in names:
    shutil.copyfile(OUT/'src/plugin'/name,target/name)
    changes['plugin/'+name]={'baseSha256':sha(HOME/name) if (HOME/name).exists() else None,'candidateSha256':sha(target/name)}
(UPDATE/'integration.json').write_text(json.dumps({'changes':changes},indent=2),encoding='utf-8')
subprocess.run([sys.executable,str(ROOT/'tools/Deploy-EnhancedSleep.py'),str(UPDATE)],check=True)
