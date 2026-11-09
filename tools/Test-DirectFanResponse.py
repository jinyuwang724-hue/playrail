"""Bounded direct-output response test, restoring the active fan curve in finally.

Optional CPU warming uses ordinary hashing threads, no power/frequency changes.
The loop stops warming on temperature/error/timeout; the service retains its own
critical-temperature hand-back. It is not a substitute for long gameplay.
"""
import argparse
import asyncio
import copy
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import threading
import time

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('verify_fan',ROOT/'tools/Verify-FanRecovery.py')
verify=importlib.util.module_from_spec(spec);spec.loader.exec_module(verify)

async def update(fan,revision):
    proc=await asyncio.create_subprocess_exec(str(verify.EXE),'--client',stdin=asyncio.subprocess.PIPE,stdout=asyncio.subprocess.PIPE,stderr=asyncio.subprocess.PIPE,creationflags=0x08000000)
    try:
        out,_=await asyncio.wait_for(proc.communicate((json.dumps({'command':'update','revision':revision,'fan':fan})+'\n').encode()),15)
        reply=json.loads(out.decode('utf-8-sig'))
        if not reply.get('ok'):raise RuntimeError(reply.get('error'))
        return reply['fan']
    finally:
        if proc.returncode is None:
            proc.kill()
            await proc.wait()

def warming(stop):
    block=bytes(1024*1024)
    deadline=time.monotonic()+120
    while not stop.is_set() and time.monotonic()<deadline:hashlib.sha256(block).digest()

async def main(warm,seconds,worker_count):
    before=await verify.state();original=copy.deepcopy(before['settings'])
    if before.get('serviceVersion')!='1.1.1' or before.get('controlStrategy')!='ec_output':raise RuntimeError('Direct service is not installed')
    if before.get('rpmLimit')!=4700:raise RuntimeError('The trial requires an existing 4700 RPM policy')
    if original['control']!='custom' or original['full']:raise RuntimeError('Test requires active custom control without full speed')
    if max(before.get('temperature',999),before.get('ecTemperature',999))>80:raise RuntimeError('Initial temperature is too high for the trial')
    power=Path(os.environ['LOCALAPPDATA'])/'ClawToolbox/data/power.json'
    power_hash=hashlib.sha256(power.read_bytes()).hexdigest()
    report={'test':'direct-4700-response','warm':warm,'before':before,'high':[],'restored':[]}
    stop=threading.Event();workers=[]
    try:
        candidate=copy.deepcopy(original);candidate['curves'][original['mode']]=[76]*6
        await update(candidate,before['revision'])
        if warm:
            workers=[threading.Thread(target=warming,args=(stop,),daemon=True) for _ in range(min(worker_count,os.cpu_count() or 1))]
            for worker in workers:worker.start()
        started=time.monotonic()
        while time.monotonic()-started<seconds:
            sample=await verify.state();sample['elapsed']=time.monotonic()-started;report['high'].append(sample)
            if sample.get('temperature',999)>=88 or sample.get('ecTemperature',999)>=85:stop.set()
            if sample.get('error') or sample.get('rpmCeilingThermalOverride') or max(sample.get('temperature',999),sample.get('ecTemperature',999))>=100:raise RuntimeError('Temperature/controller guard interrupted the trial')
            if len(report['high'])%8==1:print(json.dumps({'phase':'4700',**{k:sample.get(k) for k in ['temperature','ecTemperature','target','duty1','duty2','rpm1','rpm2','error']}}),flush=True)
            await asyncio.sleep(1)
    except Exception as error:report['error']=str(error)
    finally:
        stop.set()
        for worker in workers:worker.join(2)
        current=await verify.state();await update(original,current['revision'])
    for _ in range(25):
        sample=await verify.state();report['restored'].append(sample)
        if len(report['restored'])%8==1:print(json.dumps({'phase':'restored',**{k:sample.get(k) for k in ['temperature','target','rpm1','rpm2','error']}}),flush=True)
        await asyncio.sleep(1)
    settled=[s for s in report['high'] if s['elapsed']>=20]
    report['capConfirmed']=len(settled)>=10 and all(0<s.get(k,0)<=4700 for s in settled for k in ['rpm1','rpm2'])
    report['speedReached']=len(settled)>=10 and all(s.get(k,0)>=4200 for s in settled[-10:] for k in ['rpm1','rpm2'])
    report['factoryTablesPreserved']=all(s.get('cpu')==before['cpu'] and s.get('gpu')==before['gpu'] for s in report['high'])
    report['settingsRestored']=report['restored'][-1]['settings']==original
    report['powerPreserved']=hashlib.sha256(power.read_bytes()).hexdigest()==power_hash
    report['temperatureMax']=max((s.get('temperature',0) for s in report['high']),default=0)
    report['ecTemperatureMax']=max((s.get('ecTemperature',0) for s in report['high']),default=0)
    report['rpmMaxAfterSettling']=max((s.get(k,0) for s in settled for k in ['rpm1','rpm2']),default=0)
    report['passed']=not report.get('error') and all(report[k] for k in ['capConfirmed','speedReached','factoryTablesPreserved','settingsRestored','powerPreserved'])
    folder=ROOT/'diagnostics/fan-direct-20261023';folder.mkdir(exist_ok=True,parents=True)
    (folder/('response-warm.json' if warm else 'response.json')).write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({k:v for k,v in report.items() if k not in ['before','high','restored']},ensure_ascii=False),flush=True)
    return 0 if report['passed'] else 1

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--warm',action='store_true');parser.add_argument('--seconds',type=int,default=50);parser.add_argument('--workers',type=int,default=2);args=parser.parse_args()
    raise SystemExit(asyncio.run(main(args.warm,max(35,min(args.seconds,90)),max(1,min(args.workers,4)))))
