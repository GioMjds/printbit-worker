using System;
using System.Diagnostics;

class Program {
    static void Main() {
        foreach (var p in Process.GetProcesses()) {
            try {
                if (!string.IsNullOrEmpty(p.MainWindowTitle)) {
                    Console.WriteLine(p.ProcessName + " - " + p.MainWindowTitle);
                }
            } catch {}
        }
    }
}
