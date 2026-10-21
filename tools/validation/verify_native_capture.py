import argparse
import json
import os
from pathlib import Path
import selectors
import subprocess
import time
from verify_native_paths import native_view,host_view,translate,phase_equivalent,DOTNET,WORK

parser=argparse.ArgumentParser();parser.add_argument('--capture',type=Path,required=True);parser.add_argument('--save',type=Path,required=True);parser.add_argument('--output',type=Path,required=True);parser.add_argument('--fixture-host',action='store_true');args=parser.parse_args();args.output.mkdir();tmp=args.output/'tmp';tmp.mkdir()
data=json.loads(args.capture.read_text());stderr=(args.output/'stderr.log').open('w')
binary=WORK/('tools/validation/FixtureHost/bin/Release/net9.0/FixtureHost.dll' if args.fixture_host else 'addons/public-observation/Sts2PolicyHost/bin/Release/net9.0/Sts2PolicyHost.dll')
p=subprocess.Popen([DOTNET,str(binary),str(args.save),data['seed']],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=stderr,text=True,env=os.environ|{'TMPDIR':str(tmp)})
selector=selectors.DefaultSelector();selector.register(p.stdout,selectors.EVENT_READ);responses=[];commands=[];checks=[];transport=[];started=time.monotonic();report={'scope':'Restore exact saved native111 state; replay captured native actions. Native process cleanup status is separate.','capture':str(args.capture),'save':str(args.save),'nativeExitCode':data['exitCode']}
def read():
    if not selector.select(10):raise TimeoutError('no host response')
    line=p.stdout.readline();responses.append(line);value=json.loads(line)
    if 'error' in value:raise RuntimeError(value['error'])
    return value
def compare(index,boundary,n,m):
    a=native_view(n);b=host_view(m,n['phase']=='combat');delta={k:{'native':v,'host':b[k]} for k,v in a.items() if v!=b[k]}
    phase_ok=phase_equivalent(n,m)
    if not phase_ok:delta['phase']={'native':n['phase'],'host':m['state']['phase']}
    checks.append({'index':index,'boundary':boundary,'differences':delta,'equal':not delta,'nativePhase':n['phase'],'hostPhase':m['state']['phase']})
    if delta:raise ValueError('native/host state differs')
def settle(m,n,index,boundary):
    if n['phase']=='treasure' and m['state']['phase']=='Treasure':
        missing=set(n['relics'])-set(m['state']['players'][0]['relics'])
        takes=[i for i,o in enumerate(m['options']) if o['kind']=='TakeTreasureRelic' and o.get('treasureRelicId') in missing]
        if len(missing)==1 and len(takes)==1:
            command={'cmd':'step','index':takes[0]};commands.append(command);p.stdin.write(json.dumps(command)+'\n');p.stdin.flush();m=read();transport.append({'index':index,'boundary':boundary,'reason':'native auto-claimed the exact relic offered by host; transport-only acknowledgement'})
    if n['phase'] not in ['rewards','card_reward'] and m['state']['phase']=='Reward':
        rewards=m['state']['rewards']['rewards'];proceed=[i for i,o in enumerate(m['options']) if o['kind']=='ProceedFromRewards']
        if rewards and all(r['taken'] for r in rewards) and len(proceed)==1:
            command={'cmd':'step','index':proceed[0]};commands.append(command);p.stdin.write(json.dumps(command)+'\n');p.stdin.flush();m=read();transport.append({'index':index,'boundary':boundary,'reason':'native auto-closes a fully claimed reward set; host explicit proceed; no reward skipped'})
    return m
try:
    message=read()
    for step in data['actions']:
        message=settle(message,step['before']['observation'],step['index'],'before')
        compare(step['index'],'before',step['before']['observation'],message)
        if step['chosen']['action_type']=='fixture_kill_all':
            if not args.fixture_host or data.get('normalGameplay') is not False:raise ValueError('fixture command requires non-normal capture and fixture-only host')
            command,note={'cmd':'fixture_kill_all'},'explicit engine-kill fixture; not a legal policy action or natural combat win'
        else:command,note=translate(message,step)
        if note:transport.append({'index':step['index'],'reason':note})
        if command:commands.append(command);p.stdin.write(json.dumps(command)+'\n');p.stdin.flush();message=read()
        message=settle(message,step['after']['observation'],step['index'],'after')
        compare(step['index'],'after',step['after']['observation'],message)
    report['outcome']='matched';report['actions']=len(data['actions'])
except Exception as error:report['outcome']='mismatch_or_error';report['error']=repr(error)
finally:
    if p.poll() is None:
        p.stdin.write('{"cmd":"close"}\n');p.stdin.flush();p.stdin.close()
        try:p.wait(timeout=3)
        except subprocess.TimeoutExpired:p.kill();p.wait()
    selector.close();stderr.close()
report.update(exitCode=p.returncode,seconds=time.monotonic()-started,checks=checks,transport=transport)
(args.output/'report.json').write_text(json.dumps(report,indent=2)+'\n');(args.output/'responses.private.jsonl').write_text(''.join(responses));(args.output/'commands.private.jsonl').write_text(''.join(json.dumps(c)+'\n' for c in commands));print(json.dumps(report))
