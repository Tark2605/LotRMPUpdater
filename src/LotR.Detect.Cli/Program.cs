using System;
using System.Reflection;
using System.Text;
using LotR.GameDetection;

namespace LotR.Detect.Cli
{
    /// <summary>
    /// lotr-detect: prints the Bannerlord detection report as JSON (for support).
    /// Read-only: it never writes to disk or to the registry; output goes to stdout only.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            var options = new DetectionOptions();
            bool mask = false;
            string pick = null;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--deep": options.DeepScan = true; break;
                    case "--no-write-check": options.WriteProbe = WriteProbeMode.None; break;
                    case "--no-process": options.DisabledSources.Add(DiscoverySource.RunningProcess); break;
                    case "--mask-user": mask = true; break;
                    case "--pick":
                        if (i + 1 >= args.Length) return Usage("--pick needs a path");
                        pick = args[++i];
                        break;
                    case "-h":
                    case "--help":
                    case "/?":
                        return Usage(null);
                    default:
                        return Usage("unknown argument: " + args[i]);
                }
            }

            try
            {
                var detector = GameDetector.CreateForThisPc();
                var report = pick != null ? detector.ValidateManualSelection(pick, options) : detector.Detect(options);
                var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                Console.Write(ReportJson.Serialize(report, mask, version));
                return report.Found ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("lotr-detect failed: " + ex);
                return 2;
            }
        }

        private static int Usage(string error)
        {
            if (error != null)
                Console.Error.WriteLine(error);
            Console.Error.WriteLine(@"lotr-detect - finds Mount & Blade II: Bannerlord on this PC and prints a JSON report.
Read-only: nothing is written to disk or to the registry.

Usage: lotr-detect [--deep] [--no-write-check] [--no-process] [--mask-user] [--pick <path>]
  --deep            also scan common game folders on every fixed drive (slower)
  --no-write-check  skip the (read-only) Modules write-permission check
  --no-process      ignore running Bannerlord processes
  --mask-user       replace the Windows user name in paths by <user> (for support tickets)
  --pick <path>     validate a folder or Bannerlord.exe chosen by hand instead of searching

Exit code: 0 = found, 1 = not found, 2 = error.");
            return error == null ? 0 : 2;
        }
    }
}
