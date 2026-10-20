using System;
using System.IO;

// A deliberately unrelated frontend: different entrypoint, no WinForms or Python.
static class FutureFrontend {
    static void Main(){
        File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("QEMUM_DATA_DIR"),"future-frontend.txt"),"protocol-1");
        File.WriteAllText(Environment.GetEnvironmentVariable("QEMUM_READY_FILE"),"0.1.0-alpha.2");
    }
}
