"""Stage-0 native fixture baseline. Never loads installed manager data or starts VMs."""
from pathlib import Path
import argparse,hashlib,json,os,subprocess,sys,time,shutil,tempfile
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'tools'))
from build import compile_program,GUI,HIDDEN,SOURCES
from test import fixture

def main():
 parser=argparse.ArgumentParser();parser.add_argument('--suite',choices=['matrix','lifecycle','transport','idle','all'],default='matrix');parser.add_argument('--stage',type=int,choices=range(7),default=0);parser.add_argument('--idle-seconds',type=int,default=1800);parser.add_argument('--compile-only',action='store_true');parser.add_argument('--reuse-frozen',action='store_true');args=parser.parse_args()
 out=ROOT/f'build/test-results/optimization/stage{args.stage}';out.mkdir(parents=True,exist_ok=True);exe=ROOT/'build/tests'/('ResponsivenessBaseline.exe' if args.stage==0 else f'ResponsivenessBaseline-stage{args.stage}.exe')
 if args.stage==0 and (out/'baseline-acceptance.json').exists() and not args.reuse_frozen:raise RuntimeError('Accepted stage-0 evidence is immutable. Select --stage 1..6 for a new measurement.')
 if not args.reuse_frozen:
  compile_program(exe,'ResponsivenessBaseline',(ROOT/'tests/gui/ResponsivenessBaseline.cs',),manifest=GUI/'app.manifest');shutil.copy2(GUI/'VMManager.exe.config',str(exe)+'.config')
 manifest=dict(created=time.time(),app_sources={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in SOURCES},executable_sha256=hashlib.sha256(exe.read_bytes()).hexdigest(),harness_sha256=hashlib.sha256((ROOT/'tests/gui/ResponsivenessBaseline.cs').read_bytes()).hexdigest(),python=sys.executable,claims='synthetic native rendering and IPC timing, not monitor FPS')
 if args.reuse_frozen:
  prior=json.loads((out/'frozen-source.json').read_text(encoding='utf-8'));assert prior['app_sources']==manifest['app_sources'] and prior['executable_sha256']==manifest['executable_sha256'] and prior['harness_sha256']==manifest['harness_sha256'],'Frozen baseline source or harness changed'
 else:(out/'frozen-source.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
 if args.compile_only:print('Compiled frozen stage-0 harness');return
 cases=[]
 if args.suite in ('matrix','all'):
  for count in (20,200):
   for scale,display in ((1,'fullhd'),(1.5,'fullhd'),(2,'fullhd'),(1,'4k')):cases.append(('matrix',count,scale,display))
 if args.suite in ('lifecycle','all'):cases.append(('lifecycle',200,1,'fullhd'))
 if args.suite in ('transport','all'):cases.append(('transport',20,1,'fullhd'))
 if args.suite in ('idle','all'):cases.append(('idle',20,1,'fullhd'))
 env=os.environ.copy()
 env.update(DOTNET_CLI_TELEMETRY_OPTOUT='1',OTEL_SDK_DISABLED='true',APPLICATIONINSIGHTS_ENABLE_AGENT='false',COR_ENABLE_PROFILING='0',CORECLR_ENABLE_PROFILING='0',POWERSHELL_TELEMETRY_OPTOUT='1',OTEL_TRACES_EXPORTER='none',OTEL_METRICS_EXPORTER='none',OTEL_LOGS_EXPORTER='none')
 for key in ('APPLICATIONINSIGHTS_CONNECTION_STRING','APPINSIGHTS_INSTRUMENTATIONKEY'):env.pop(key,None)
 for key in ('QEMUM_APP_DIR','QEMUM_DATA_DIR','QEMUM_PYTHON','QEMUM_INSTALL_DIR'):env.pop(key,None)
 failures=0
 for mode,count,scale,display in cases:
  label=f'{mode}-{count}-{scale}-{display}';receipt=out/(label+'.json');start=time.time()
  with tempfile.TemporaryDirectory(prefix='baseline-',dir=ROOT/'build') as folder:
   root=Path(folder);iso,sentinel=fixture(root);template=json.loads((root/'machines/test.json').read_text(encoding='utf-8'));(root/'machines/test.json').unlink()
   for i in range(count):(root/f'machines/perf-node-{i}.json').write_text(json.dumps(dict(template,label=f'Synthetic VM {i}'),ensure_ascii=False),encoding='utf-8')
   before={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in (root/'machines').glob('*.json')}
   receipt.write_text(json.dumps(dict(status='starting',scenario=mode)),encoding='utf-8');print('START',label,flush=True)
   timeout=args.idle_seconds+90 if mode=='idle' else 300
   try:
    completed=subprocess.run([str(exe),mode,str(root),str(receipt),sys.executable,str(count),str(scale),display,str(args.idle_seconds)],env=env,creationflags=HIDDEN,timeout=timeout)
    assert iso.read_bytes()==sentinel,'fixture ISO changed'
    assert all(hashlib.sha256((root/'machines'/name).read_bytes()).hexdigest()==digest for name,digest in before.items()),'fixture VM config changed'
    data=json.loads(receipt.read_text(encoding='utf-8-sig'));data.update(exit_code=completed.returncode,wall_seconds=round(time.time()-start,2),fixture_unchanged=True);receipt.write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8')
    print('END',label,data['status'],data['wall_seconds'],flush=True)
    if completed.returncode or data.get('status')!='pass':failures+=1;print(data.get('error',''),flush=True)
   except Exception as error:
    failures+=1;receipt.with_suffix('.runner-error.txt').write_text(str(error),encoding='utf-8');print('ERROR',label,str(error),flush=True)
 return 1 if failures else 0
if __name__=='__main__':raise SystemExit(main())
