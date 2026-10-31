"""Build and run all isolated switch checks; --qemu adds diskless paused guests."""
import argparse,json,subprocess,sys
from build import ROOT,compiler,HIDDEN

def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--qemu',action='store_true');args=parser.parse_args()
    subprocess.run([sys.executable,str(ROOT/'tools/test_local_switch.py')]+(['--qemu'] if args.qemu else []),check=True)
    for name in ('test_switch_guard.py','test_switch_frames.py'):
        subprocess.run([sys.executable,str(ROOT/'tools'/name)],check=True)
    results=[]
    for file,main in [('CoreTests.cs','SwitchCoreTests'),('StressTests.cs','SwitchStressTests')]:
        output=ROOT/'build/switch'/file.replace('.cs','.exe')
        subprocess.run([compiler(),'/nologo','/target:exe','/platform:x64','/main:'+main,'/reference:System.Web.Extensions.dll','/out:'+str(output)]+[str(ROOT/p) for p in ('src/switch/LocalSwitch.cs','src/switch/WindowsPeer.cs','tests/switch/'+file)],check=True)
        result=subprocess.run([str(output)],capture_output=True,text=True,timeout=30,creationflags=HIDDEN)
        if result.returncode:raise RuntimeError(result.stdout+result.stderr)
        print(result.stdout.strip(),flush=True);results.append(dict(suite=main,passed=True,output=result.stdout.strip()))
    (ROOT/'build/switch/native-results.json').write_text(json.dumps(results,indent=2),encoding='utf-8')
    print('All switch suites passed; guest OS packet delivery is not tested.')

if __name__=='__main__':main()
