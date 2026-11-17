"""User-owned finite baseline job. Launched from the Windows desktop, outside tool jobs."""
from pathlib import Path
import argparse,ctypes,hashlib,json,os,subprocess,sys,time,traceback,uuid
ROOT=Path(__file__).resolve().parents[1];OUT=ROOT/'build/test-results/optimization/stage0'
FLAGS={'DOTNET_CLI_TELEMETRY_OPTOUT':'1','OTEL_SDK_DISABLED':'true','APPLICATIONINSIGHTS_ENABLE_AGENT':'false','COR_ENABLE_PROFILING':'0','CORECLR_ENABLE_PROFILING':'0','POWERSHELL_TELEMETRY_OPTOUT':'1','OTEL_TRACES_EXPORTER':'none','OTEL_METRICS_EXPORTER':'none','OTEL_LOGS_EXPORTER':'none'}
def environment():
 env=os.environ.copy();env.update(FLAGS)
 for key in ('APPLICATIONINSIGHTS_CONNECTION_STRING','APPINSIGHTS_INSTRUMENTATIONKEY'):env.pop(key,None)
 return env

def write(path,data):
 temporary=path.with_suffix(path.suffix+'.tmp');temporary.write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8');os.replace(temporary,path)

def in_job():
 value=ctypes.c_int();k=ctypes.windll.kernel32;k.GetCurrentProcess.restype=ctypes.c_void_p
 if not k.IsProcessInJob(ctypes.c_void_p(k.GetCurrentProcess()),None,ctypes.byref(value)):raise ctypes.WinError()
 return bool(value.value)

def process_alive(pid):
 if not isinstance(pid,int) or pid<=0:return False
 kernel=ctypes.WinDLL('kernel32',use_last_error=True)
 kernel.OpenProcess.argtypes=[ctypes.c_ulong,ctypes.c_int,ctypes.c_ulong];kernel.OpenProcess.restype=ctypes.c_void_p
 kernel.GetExitCodeProcess.argtypes=[ctypes.c_void_p,ctypes.POINTER(ctypes.c_ulong)]
 kernel.CloseHandle.argtypes=[ctypes.c_void_p]
 handle=kernel.OpenProcess(0x1000,False,pid)
 if not handle:return False
 try:
  code=ctypes.c_ulong();return bool(kernel.GetExitCodeProcess(handle,ctypes.byref(code))) and code.value==259
 finally:kernel.CloseHandle(handle)

def wait_for_worker(process,run_id,timeout=15):
 deadline=time.monotonic()+timeout
 while time.monotonic()<deadline:
  try:state=json.loads((OUT/'independent-run.json').read_text(encoding='utf-8'))
  except (OSError,ValueError):state={}
  if state.get('run_id')==run_id:
   if state.get('status') in ('error','inconclusive'):raise RuntimeError(state.get('error','Worker startup failed'))
   if state.get('status')=='running' and process.poll() is None:return state
  if process.poll() is not None:raise RuntimeError('Worker exited during startup. See independent.stderr.log.')
  time.sleep(.1)
 raise RuntimeError('Worker did not confirm startup. See independent.stderr.log.')

def start():
 OUT.mkdir(parents=True,exist_ok=True)
 try:previous=json.loads((OUT/'independent-run.json').read_text(encoding='utf-8'))
 except (OSError,ValueError):previous={}
 if previous.get('status')=='running' and process_alive(previous.get('pid')):
  print('Test already running. PID:',previous['pid']);return
 # Membership in a Windows Job is informational. It is not evidence of a
 # restricted tool job, nor a reason to reject a desktop Explorer launch.
 run_id=uuid.uuid4().hex
 with (OUT/'independent.stdout.log').open('ab') as stdout,(OUT/'independent.stderr.log').open('ab') as stderr:
  process=subprocess.Popen([sys.executable,str(Path(__file__).resolve()),'--worker','--run-id',run_id],cwd=ROOT,env=environment(),stdin=subprocess.DEVNULL,stdout=stdout,stderr=stderr,close_fds=True,creationflags=subprocess.DETACHED_PROCESS|subprocess.CREATE_NEW_PROCESS_GROUP)
 state=wait_for_worker(process,run_id)
 write(OUT/'independent-launch.json',dict(pid=process.pid,run_id=run_id,started_utc=state['started_utc'],owner='separate local Windows process',startup_confirmed=True,telemetry_disabled_flags=list(FLAGS),finite_idle_seconds=1800))
 print('Started local 30-minute test. PID:',process.pid);print('Report:',ROOT/'docs/OPTIMIZATION-STAGE-0-BASELINE.md')

def valid_window(data):
 samples=data.get('idle_samples',[]);gaps=[b['elapsed_seconds']-a['elapsed_seconds'] for a,b in zip(samples,samples[1:])]
 return data.get('elapsed_idle_seconds',0)>=1800 and len(samples)>=340 and max(gaps,default=0)<=15

def worker(run_id):
 OUT.mkdir(parents=True,exist_ok=True);receipt=OUT/'independent-run.json';state=dict(status='running',started_utc=time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime()),pid=os.getpid(),run_id=run_id,windows_job_member=in_job(),chatgpt_required=False,disconnect_survival='not_yet_observed',parallel_checks_may_affect_cpu=True,telemetry_disabled_flags=list(FLAGS));write(receipt,state)
 try:
  frozen=OUT/'frozen-source.json';state['frozen_manifest_sha256']=hashlib.sha256(frozen.read_bytes()).hexdigest();write(receipt,state)
  # Detached Windows children must have explicit standard handles too; otherwise
  # a final print/traceback can be lost instead of reaching the worker log.
  with (OUT/'independent-runner.stdout.log').open('ab') as stdout,(OUT/'independent-runner.stderr.log').open('ab') as stderr:
   completed=subprocess.run([sys.executable,str(ROOT/'tools/benchmark_responsiveness.py'),'--suite','idle','--reuse-frozen','--idle-seconds','1800'],cwd=ROOT,env=environment(),stdin=subprocess.DEVNULL,stdout=stdout,stderr=stderr,timeout=1950,creationflags=subprocess.CREATE_NO_WINDOW)
  data=json.loads((OUT/'idle-20-1-fullhd.json').read_text(encoding='utf-8-sig'));samples=data.get('idle_samples',[]);gaps=[b['elapsed_seconds']-a['elapsed_seconds'] for a,b in zip(samples,samples[1:])]
  continuous=valid_window(data)
  state.update(status='complete' if completed.returncode==0 and data.get('fixture_unchanged') and continuous else 'inconclusive',exit_code=completed.returncode,observed_seconds=data.get('elapsed_idle_seconds'),samples=len(samples),max_sample_gap_seconds=max(gaps,default=0),continuous_window=continuous)
 except Exception:state.update(status='error',error=traceback.format_exc())
 finally:
  state['finished_utc']=time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime());write(receipt,state)
  try:
   import report_responsiveness_baseline as report
   report.summary()
  except Exception:(OUT/'independent-report-error.txt').write_text(traceback.format_exc(),encoding='utf-8')
  print(json.dumps(state,ensure_ascii=False),flush=True)
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('--worker',action='store_true');p.add_argument('--run-id',default='manual');a=p.parse_args()
 if a.worker:worker(a.run_id)
 else:start()
