using System;
using System.IO;
using QemuM.Update;
static class SwitchUpdateLockTests {
    static int Main(string[] args){
        bool blocked=false;try{Core.AssertNoStandOperations(args[0]);}catch(IOException){blocked=true;}
        if(blocked!=(args[1]=="blocked"))return 1;return 0;
    }
}
