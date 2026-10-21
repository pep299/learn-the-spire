import argparse
from collections import Counter
import hashlib
import json
import os
from pathlib import Path
import selectors
import subprocess
import time

ROOT=Path(os.environ.get('LEARN_REFERENCE_ROOT', '.')).resolve()
WORK=Path(__file__).resolve().parents[2]
DOTNET=os.environ.get('LEARN_DOTNET', 'dotnet')


def native_view(o):
    v=dict(isTerminal=o['is_terminal'],isVictory=o['is_victory'],act=o['act'],floor=o['floor'],hp=o['player_hp'],maxHp=o['player_max_hp'],gold=o['gold'],
        deck=sorted(o['deck_cards']),relics=sorted(o['relics']),potions=o['potions'])
    if o['phase']!='combat':return v
    c=o['combat']
    def intent(i):return (i['type'],i['damage'] if i['type'].startswith('Attack') else None,i['hits'] if i['type'].startswith('Attack') else None)
    v.update(block=o['player_block'],energy=o['player_energy'],turn=c['turn'],powers=sorted(o['player_powers'].items()),
        hand=[(x['card_id'],bool(x['upgrades']),'X' if x['costs_x'] else str(x['current_cost'])) for x in c['hand']],
        piles=[c[k+'_pile_count'] for k in ('draw','discard','exhaust')],
        enemies=[(e['combat_id'],e['model_id'],e['hp'],e['max_hp'],e['block'],sorted(e['powers'].items()),[intent(i) for i in e['intents']]) for e in c['enemies'] if e['is_alive']])
    return v


def host_view(m,active):
    s=m['state'];p=s['players'][0]
    v=dict(isTerminal=s['isGameOver'],isVictory=s['isVictory'],act=s['actIndex']+1,floor=s['floor'],hp=p['currentHp'],maxHp=p['maxHp'],gold=p['gold'],
        deck=sorted(c['cardId']+('+' if c['upgraded'] else '') for c in p['deck']),relics=sorted(p['relics']),potions=[v for v in p['potions'] if v is not None])
    if not active:return v
    c=p['combatState']
    if c is None:raise ValueError('native combat but host has no combat')
    def intent(i):return (i['type'],i.get('damage') if i['type'].startswith('Attack') else None,i.get('hits') if i['type'].startswith('Attack') else None)
    v.update(block=p['block'],energy=c['energy'],turn=c['turnNumber'],powers=sorted((v['powerId'],v['amount']) for v in c['powers']),
        hand=[(x['cardId'],x['upgraded'],'X' if x['costsX'] else str(max(0,x['energyCost']) if not x['canPlay'] else x['energyCost'])) for x in c['hand']],
        piles=[c['drawPileCount'],len(c['discardPile']),len(c['exhaustPile'])],
        enemies=[(e['combatId'],e['monsterId'],e['currentHp'],e['maxHp'],e['block'],sorted((v['powerId'],v['amount']) for v in e['powers']),[intent(i) for i in e['intents']]) for e in s['combat']['enemies'] if e['currentHp']>0])
    return v


def phase_equivalent(native,message):
    allowed={'combat':['Combat'],'card_select':['Choice'],'card_reward':['Reward'],'rewards':['Reward','Treasure'],'map':['Map','Shop'],'shop':['Shop'],'rest_site':['RestSite'],'treasure':['Treasure','Map'],'game_over':['GameOver'],'terminal':['GameOver'],'event':['Event']}
    host_phase=message['state']['phase']
    if native['phase']=='event' and host_phase in ['Map','Shop']:
        return all(v.get('proceed') or v.get('locked') for v in native['room']['details']['options'])
    return host_phase in allowed.get(native['phase'],[])


def translate(m,step):
    chosen=step['chosen'];typ=chosen['action_type'];d=chosen.get('metadata') or {};opts=m['options'];o=step['before']['observation'];phase=m['state']['phase']
    def one(test):
        indices=[i for i,x in enumerate(opts) if test(x)]
        if len(indices)!=1:raise ValueError(f'expected one option for {chosen}; matches={indices}')
        return {'cmd':'step','index':indices[0]},None
    if typ=='play_card':return one(lambda x:x['kind']=='PlayCard' and x.get('handIndex')==d['card_index'] and x.get('targetCombatId')==d.get('target_id'))
    if typ=='end_turn':return one(lambda x:x['kind']=='EndTurn')
    if typ=='use_potion':
        target=d.get('target_id')
        enemies={e['combat_id'] for e in (o.get('combat') or {}).get('enemies',[])}
        if target not in enemies:target=None
        return one(lambda x:x['kind']=='UsePotion' and x.get('potionSlot')==d.get('potion_index',d.get('slot')) and x.get('targetCombatId')==target)
    if typ=='shop_buy':
        t={'MerchantCardEntry':'Card','MerchantRelicEntry':'Relic','MerchantPotionEntry':'Potion','MerchantCardRemovalEntry':'CardRemoval'}[d['entry_type']]
        return one(lambda x:x['kind']=='BuyShopItem' and x.get('shopItemType')==t and (t=='CardRemoval' or x.get('shopItemId')==d['item_id']) and x.get('shopItemCost')==d['price'])
    if typ=='choose_map':return one(lambda x:x['kind']=='MoveTo' and x['coord']=={'col':d['col'],'row':d['row']})
    if typ=='choose_event':
        if phase in ('Map','Shop') and o['room']['details']['options'][d['option_index']]['proceed']:
            return None,'native event exit acknowledgement; host already resolved event'
        native_event=o['room']['details'];host_event=m['state'].get('event')
        if not host_event or native_event['event_id']!=host_event['eventId']:raise ValueError('event model differs')
        chosen_key=native_event['options'][d['option_index']]['text_key']
        keys={v['index']:v['textKey'] for v in host_event['options']}
        return one(lambda x:x['kind']=='ChooseEventOption' and keys.get(x.get('eventOptionIndex'))==chosen_key)
    if typ in ('choose_card_select','choose_cards'):
        pending=m['state']['pendingChoice'];cards=o['room']['details']['cards']
        expected=[c['card_id'] for c in cards];actual=[c['cardId'] for c in pending['options']]
        if expected!=actual:raise ValueError('card selection candidates differ: '+str((expected,actual)))
        if o['room']['details'].get('min') is not None:
            if [o['room']['details']['min'],o['room']['details']['max']]!=[pending['minSelect'],pending['maxSelect']]:raise ValueError('selection cardinality differs')
        return {'cmd':'step','cardIndices':d.get('indices',[d.get('card_index')])},None
    if typ=='choose_reward':
        rt=d['reward_type']
        if rt=='Card':return None,'native opens card reward screen; host advertises offered cards directly'
        if rt=='Gold':
            ordinal=sum(int(text.split(':')[0])<d['reward_index'] and text.split(':')[1]=='Gold' for text in o['room']['options'])
            candidates=[i for i,x in enumerate(opts) if x['kind']=='TakeReward' and x['description'].endswith(' gold')]
            if ordinal>=len(candidates):raise ValueError('native gold reward ordinal absent in host')
            return {'cmd':'step','index':candidates[ordinal]},None
        key={'Potion':'potion_id','Relic':'relic_id'}[rt];model=d.get(key)
        if model is None:
            field={'Potion':'potions','Relic':'relics'}[rt]
            added=Counter(step['after']['observation'][field])-Counter(o[field])
            if sum(added.values())!=1:raise ValueError('recorded reward has no unique acquired model')
            model=next(iter(added))
        return one(lambda x:x['kind']=='TakeReward' and x['description']==f'Take {rt.lower()} {model}')
    if typ=='choose_card':return one(lambda x:x['kind']=='TakeReward' and x.get('card',{}).get('cardId')==d['card_id'])
    if typ=='skip_card':return None,'native dismisses card screen; explicit rewards proceed will skip it'
    if typ=='shop_leave':return None,'native shop exit acknowledgement; host exposes subsequent map travel directly'
    if typ=='choose_rest':return one(lambda x:x['kind']=='ChooseRestOption' and x.get('restOptionId')==d['option_key'])
    if typ=='proceed':
        if phase=='Reward':return one(lambda x:x['kind']=='ProceedFromRewards')
        if phase=='Treasure':return one(lambda x:x['kind']=='TakeTreasureRelic')
        return None,'native room continuation; host already at map'
    raise NotImplementedError(typ)


def run(source,case,repeat,out):
    folder=out/f'case{case}-repeat{repeat}';folder.mkdir();tmp=folder/'tmp';tmp.mkdir()
    data=json.loads(source.read_text());seed=data['seed'];stderr=(folder/'stderr.log').open('w')
    proc=subprocess.Popen([DOTNET,str(WORK/'addons/public-observation/Sts2PolicyHost/bin/Release/net9.0/Sts2PolicyHost.dll')],
        stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=stderr,text=True,env=os.environ|{'TMPDIR':str(tmp)})
    selector=selectors.DefaultSelector();selector.register(proc.stdout,selectors.EVENT_READ)
    responses=[];commands=[];checks=[];transport=[];began=time.monotonic();message=None
    def send(command):
        commands.append(command);proc.stdin.write(json.dumps(command)+'\n');proc.stdin.flush()
        if not selector.select(10):raise TimeoutError('host response exceeded 10 seconds')
        line=proc.stdout.readline();responses.append(line);m=json.loads(line)
        if 'error' in m:raise RuntimeError(m['error'])
        return m
    def compare(index,boundary,native,m):
        a=native_view(native);b=host_view(m,native['phase']=='combat');delta={k:{'native':v,'host':b[k]} for k,v in a.items() if v!=b[k]}
        if not phase_equivalent(native,m):delta['phase']={'native':native['phase'],'host':m['state']['phase']}
        checks.append({'index':index,'boundary':boundary,'nativePhase':native['phase'],'hostPhase':m['state']['phase'],'equal':not delta,'differences':delta})
        if delta:raise ValueError('public observation differs')
    def sync_treasure(index,boundary,native,m):
        if m['state']['phase']=='Treasure' and native['phase']=='treasure':
            missing=set(native['relics'])-set(m['state']['players'][0]['relics'])
            takes=[i for i,x in enumerate(m['options']) if x['kind']=='TakeTreasureRelic' and x.get('treasureRelicId') in missing]
            if len(takes)==1:
                m=send({'cmd':'step','index':takes[0]});transport.append({'index':index,'boundary':boundary,'reason':'native had already auto-claimed this exact offered treasure relic'})
        return m
    result={'source':str(source),'sourceSha256':hashlib.sha256(source.read_bytes()).hexdigest(),'case':case,'repeat':repeat,'seed':seed,'scope':'Native111 saved reference trace; natural seeded all-unlocked start; source bridge uses presentation/AutoSlay patches.'}
    try:
        message=send({'cmd':'reset','seed':seed,'character':'IRONCLAD','ascension':0,'actGeneration':'seeded_all_unlocked'})
        for step in data['actions']:
            n=step['before']['observation']
            message=sync_treasure(step['index'],'before',n,message)
            compare(step['index'],'before',n,message)
            command,note=translate(message,step)
            if note:transport.append({'index':step['index'],'reason':note})
            if command:message=send(command)
            message=sync_treasure(step['index'],'after',step['after']['observation'],message)
            compare(step['index'],'after',step['after']['observation'],message)
        final=data['final']['observation'];compare('terminal','final',final,message)
        if not message['done'] or message['state']['isVictory']!=final['is_victory']:raise ValueError('terminal mismatch')
        result.update(outcome='matched',hostActions=len(commands)-1,nativeActions=len(data['actions']))
    except Exception as error:
        result.update(outcome='mismatch_or_error',error=repr(error))
        if message:(folder/'last-state.json').write_text(json.dumps(message,indent=2)+'\n')
    finally:
        if proc.poll() is None:
            proc.stdin.write('{"cmd":"close"}\n');proc.stdin.flush();proc.stdin.close()
            try:proc.wait(timeout=3)
            except subprocess.TimeoutExpired:proc.kill();proc.wait()
        stderr.close();selector.close()
    result.update(exitCode=proc.returncode,seconds=time.monotonic()-began,checkedBoundaries=len(checks),phaseCounts=dict(Counter(c['nativePhase'] for c in checks)),transport=transport,
        firstDifference=next((c for c in checks if not c['equal']),None))
    (folder/'responses.private.jsonl').write_text(''.join(responses));(folder/'commands.private.jsonl').write_text(''.join(json.dumps(x)+'\n' for x in commands));(folder/'checks.json').write_text(json.dumps(checks,indent=2)+'\n');(folder/'report.json').write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(result),flush=True)
    return result


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--output',type=Path,required=True);parser.add_argument('--cases',type=int,nargs='+',default=[1,2,3,4,5,6]);parser.add_argument('--repeats',type=int,default=1);parser.add_argument('--corpus',type=Path,required=True);args=parser.parse_args();args.output.mkdir()
    original={'native':[{'case':row['case'],'source':row['capture']['path']} for row in json.loads(args.corpus.read_text())['fresh']]}
    results=[run(ROOT/row['source'],row['case'],repeat,args.output) for row in original['native'] if row['case'] in args.cases for repeat in range(1,args.repeats+1)]
    (args.output/'summary.json').write_text(json.dumps(results,indent=2)+'\n')
