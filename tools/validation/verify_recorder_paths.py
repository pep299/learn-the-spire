import argparse
import json
import os
from pathlib import Path
import subprocess
import time

from verify_native_paths import WORK,DOTNET
parser=argparse.ArgumentParser();parser.add_argument('--input',type=Path,required=True);parser.add_argument('--output',type=Path,required=True);parser.add_argument('--cases',type=int,nargs='+',default=[1,2,3,4,5,6]);parser.add_argument('--save',type=Path);args=parser.parse_args();args.output.mkdir()
def short(value):return value.split('.')[-1]
def public_view(o):
    s=o['state'];p=s['status'];c=p['combat'];powers=lambda values:sorted((short(v['id']),v['amount']) for v in values)
    return dict(phase=s['phase'],terminal=s['terminal'],act=p['act'],floor=p['floor'],hp=p['hp'],maxHp=p['maxHp'],gold=p['gold'],block=p['block'],
        deck=sorted(map(short,p['deck'])),relics=sorted(map(short,p['relics'])),potions=[short(v['potion']['id']) if v['potion'] else None for v in p['potionBelt']['slots']],
        energy=p['energy'] if c else 0,turn=c['turn'] if c else None,hand=[(short(x['id']),x['cardDetails']['state']['upgrade']>0,x['cost'],x['canPlay']) for x in s['hand']],
        piles=[c[x]['count'] for x in ['draw','discard','exhaust']] if c else None,powers=powers(p['powers']) if c else [],
        enemies=[(short(e['modelId']),e['hp'],e['maxHp'],e['block'],powers(e['powers'])) for e in s['enemies']])
def baseline_view(m):
    s=m['state'];p=s['players'][0];c=p.get('combatState');powers=lambda values:sorted((v['powerId'],v['displayAmount']) for v in values if v['isVisible'])
    phase={'Event':'event','Map':'map','Combat':'combat','Choice':'grid_selection','Reward':'rewards','Shop':'shop','RestSite':'rest','Treasure':'treasure','GameOver':'terminal','BundleChoice':'bundle_selection'}[s['phase']]
    return dict(phase=phase,terminal=('victory' if s['isVictory'] else 'death') if s['isGameOver'] else None,act=s['actIndex']+1,floor=s['floor'],hp=p['currentHp'],maxHp=p['maxHp'],gold=p['gold'],block=p['block'],
        deck=sorted(x['cardId']+('+1' if x['upgraded'] else '') for x in p['deck']),relics=sorted(p['relics']),potions=p['potions'],
        energy=c['energy'] if c else 0,turn=c['turnNumber'] if c else None,hand=[(x['cardId'],x['upgraded'],'X' if x['costsX'] else str(max(0,x['energyCost']) if not x['canPlay'] else x['energyCost']),any(a['kind']=='PlayCard' and a.get('handIndex')==index for a in m['options'])) for index,x in enumerate(c['hand'])] if c else [],
        piles=[c['drawPileCount'],len(c['discardPile']),len(c['exhaustPile'])] if c else None,powers=powers(c['powers']) if c else [],
        enemies=[(e['monsterId'],e['currentHp'],e['maxHp'],e['block'],powers(e['powers'])) for e in (s['combat']['enemies'] if s.get('combat') else [])])

results=[]
for case in ([1] if args.save else args.cases):
    source=args.input if args.save else args.input/f'case{case}-repeat1';out=args.output/f'case{case}';out.mkdir();tmp=out/'tmp';tmp.mkdir()
    raw=(source/'commands.private.jsonl').read_text();expected=[json.loads(x) for x in (source/'responses.private.jsonl').read_text().splitlines()]
    restore=['--restore-save',str(args.save),'--seed',json.loads(args.save.read_text())['rng']['seed']] if args.save else []
    started=time.monotonic();p=subprocess.run([DOTNET,str(WORK/'addons/public-observation/Sts2DllRecorder/bin/Release/net9.0/Sts2DllRecorder.dll'),'--log',str(out/'events.jsonl'),*restore],input=raw+'{"cmd":"close"}\n',env=os.environ|{'TMPDIR':str(tmp)},text=True,capture_output=True,timeout=40)
    (out/'responses.jsonl').write_text(p.stdout);(out/'stderr.log').write_text(p.stderr);observed=[json.loads(x) for x in p.stdout.splitlines()];checks=[]
    for index,(o,b) in enumerate(zip(observed,expected)):
        if 'error' in o:checks.append({'index':index,'equal':False,'error':o['error']});break
        a=public_view(o);v=baseline_view(b);delta={k:{'recorder':x,'baseline':v[k]} for k,x in a.items() if x!=v[k]};checks.append({'index':index,'equal':not delta,'differences':delta})
    events=[json.loads(x) for x in (out/'events.jsonl').read_text().splitlines()];actions=[x for x in events if x['kind']=='action_initiated'];successors=[x for x in events if x['kind']=='action_successor'];keys=set()
    def visit(v):
        if isinstance(v,dict):
            keys.update(v)
            for x in v.values():visit(x)
        elif isinstance(v,list):
            for x in v:visit(x)
    for o in observed:visit(o)
    forbidden=sorted(keys&{'seed','rng','rngState','drawOrder','relicGrabBag'})
    pair=all(sum(s['actionSequence']==a['actionSequence'] for s in successors)==1 and any(c['id']==a['data']['actionId'] for c in a['data']['observation']['actions']) for a in actions)
    row={'case':case,'exitCode':p.returncode,'seconds':time.monotonic()-started,'boundaries':len(observed),'expectedBoundaries':len(expected),'allEqual':all(c['equal'] for c in checks) and len(observed)==len(expected),'actionsPaired':pair,'forbiddenKeys':forbidden,'pendingAtExit':events[-1]['data']['pending'],'firstDifferences':[c for c in checks if not c['equal']][:3]}
    row['passed']=row['exitCode']==0 and row['allEqual'] and pair and not forbidden and not row['pendingAtExit'];results.append(row);(out/'report.json').write_text(json.dumps(row,indent=2)+'\n');print(json.dumps(row),flush=True)
(args.output/'summary.json').write_text(json.dumps(results,indent=2)+'\n')
