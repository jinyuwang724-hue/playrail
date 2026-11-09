"""Capture only shortcut results and firmware event codes, never typed keys."""
import json,os,socket,time,sys
from pathlib import Path
home=Path(os.environ['LOCALAPPDATA'])/'ClawToolbox';out=Path(__file__).resolve().parents[2]/'artifacts/special-keys-197'/('physical-events-'+sys.argv[1]+'.jsonl' if len(sys.argv)>1 else 'physical-events.jsonl')
ep=json.loads((home/'agent.json').read_text());deadline=time.monotonic()+240
with socket.create_connection(('127.0.0.1',ep['port']),15) as sock,out.open('w',encoding='utf-8') as log:
    f=sock.makefile('rwb');f.write((json.dumps({'hello':ep['token'],'client':'special-key-verification'})+'\n').encode());f.flush();f.readline()
    while time.monotonic()<deadline:
        line=f.readline()
        if not line:break
        msg=json.loads(line);kind=msg.get('event');value=msg.get('value',{})
        if kind=='button_mapping_result' or kind=='session_mode' or kind=='router_event' and value.get('type')=='button':
            log.write(json.dumps({'at':time.time(),'event':kind,'value':value},ensure_ascii=False)+'\n');log.flush()
