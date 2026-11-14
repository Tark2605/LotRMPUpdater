using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LotR.Releases.Model;

namespace Lotr.Updater.Cli
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            using (var cts = new CancellationTokenSource())
            {
                var host = new CliHost
                {
                    Terminal = !Console.IsInputRedirected && !Console.IsOutputRedirected,
                    Cancel = cts.Token,
                };
                var o = CliOptions.Parse(args);
                if (o.Error != null)
                {
                    Console.Error.WriteLine(o.Error);
                    Console.Error.WriteLine("Run lotr-updater --help for the list of commands.");
                    return ExitCodes.Usage;
                }
                var app = new CliApp(host, o);

                // Ctrl+C: while the updater works, cancel through the token (downloads stop, a started swap is rolled back by
                // the engine); while it only waits for an answer, quit at once like any terminal program.
                int presses = 0;
                Console.CancelKeyPress += (s, e) =>
                {
                    if (!app.Busy) { e.Cancel = false; return; }
                    e.Cancel = true;
                    if (Interlocked.Increment(ref presses) == 1)
                    {
                        Console.Error.WriteLine();
                        Console.Error.WriteLine("Stopping... (anything already changed is put back)");
                        cts.Cancel();
                    }
                    else Console.Error.WriteLine("Finishing safely, please wait...");
                };
                PosixSignalRegistration term = null, hup = null;
                if (!OsPath.IsWindows)
                {
                    // Terminal closed (SIGHUP) or the system asks to stop (SIGTERM): same safe stop as Ctrl+C.
                    Action<PosixSignalContext> stop = ctx => { if (app.Busy) { ctx.Cancel = true; cts.Cancel(); } };
                    term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, stop);
                    hup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, stop);
                }
                try
                {
                    return await app.RunAsync().ConfigureAwait(false);
                }
                finally
                {
                    term?.Dispose();
                    hup?.Dispose();
                }
            }
        }
    }
}
