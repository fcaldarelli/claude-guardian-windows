using System;
using System.Threading;
using System.Windows.Forms;

namespace ClaudeGuardian
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // One instance per user: a second copy launched (e.g. from the Start menu) exits immediately.
            using (var mutex = new Mutex(true, @"Local\ClaudeGuardian", out var first))
            {
                if (!first) return;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var app = new TrayApp())
                {
                    Application.Run(app);
                }
            }
        }
    }
}
