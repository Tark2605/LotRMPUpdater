using System;
using System.Reflection;
using System.Text;
using LotR.GameDetection;

namespace LotR.Detect.Cli
{
    /// <summary>
    /// lotr-detect: prints the Bannerlord detection report as JSON (for support).
    /// Read-only: it never writes to the registry and writes to disk only through the Modules write probe
    /// (a 0-byte delete-on-close temp file, gone immediately; disable with --write-check accesscheck|none). Output goes to stdout only.
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
                    case "--write-check":
                        if (i + 1 >= args.Length) return Usage("--write-check needs tempfile, accesscheck or none");
                        switch (args[++i].ToLowerInvariant())
                        {
                            case "tempfile": options.WriteProbe = WriteProbeMode.TempFile; break;
                            case "accesscheck": options.WriteProbe = WriteProbeMode.AccessCheck; break;
                            case "none": options.WriteProbe = WriteProbeMode.None; break;
                            default: return Usage("--write-check needs tempfile, accesscheck or none");
                        }
                        break;
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
Read-only: nothing is written to the registry; the only disk write is the Modules write probe,
a hidden 0-byte delete-on-close file that Windows removes immediately. Never asks for admin rights.

Usage: lotr-detect [--deep] [--write-check tempfile|accesscheck|none] [--no-process] [--mask-user] [--pick <path>]
  --deep            also scan common game folders on every fixed drive (slower)
  --write-check     tempfile (default): create+auto-delete a 0-byte probe file in Modules
                    accesscheck: ask Windows about the folder permissions, write nothing
                    none: do not check
  --no-write-check  same as --write-check none
  --no-process      ignore running Bannerlord processes
  --mask-user       replace the Windows user name in paths by <user> (for support tickets)
  --pick <path>     validate a folder or Bannerlord.exe chosen by hand instead of searching

Exit code: 0 = found, 1 = not found, 2 = error.");
            return error == null ? 0 : 2;
        }
    }
}
