using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lotr.Updater.Core;
using Lotr.Updater.Core.Game;
using Lotr.Updater.Core.Install;
using Lotr.Updater.Core.Model;
using Lotr.Updater.Testing;

namespace Lotr.Updater.DevServer
{
    /// <summary>
    /// Dev helper for the end-to-end run against releases made by the REAL publish tool (tools/LotR.Publish):
    ///   fakegame &lt;dir&gt;                              a fake Bannerlord folder (never the real game)
    ///   fakecore &lt;dir&gt; &lt;1|2&gt;                         a fake LotRMPCore module (24 MiB pack, variant 2 = a 3 MiB region changed)
    ///   serve &lt;filesRoot&gt; [port] [KB/s]               serves &lt;filesRoot&gt; at http://localhost:port/files/ (+ /api stubs)
    ///   install &lt;game&gt; &lt;baseUrl&gt; &lt;home&gt; &lt;pub.pem&gt;   headless check + install + verify through the engine
    /// Everything lives in temp folders. Never the real game, never a real server, never a real signing key.
    /// </summary>
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("usage: fakegame <dir> | fakecore <dir> <1|2> | serve <filesRoot> [port] [KBps] | install <game> <baseUrl> <home> <pub.pem>"); return 2; }
            switch (args[0])
            {
                case "fakegame": Console.WriteLine(FakeGame.Create(Path.GetFullPath(args[1]))); return 0;
                case "fakecore": FakeCore(Path.GetFullPath(args[1]), int.Parse(args[2])); return 0;
                case "serve": return Serve(Path.GetFullPath(args[1]), args.Length > 2 ? int.Parse(args[2]) : 8790, args.Length > 3 ? int.Parse(args[3]) : 0);
                case "install": return await Install(args[1], args[2], args[3], args[4]);
            }
            return 2;
        }

        private static byte[] Rnd(int size, int seed) { var b = new byte[size]; new Random(seed).NextBytes(b); return b; }

        private static void FakeCore(string dir, int variant)
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(Path.Combine(dir, "bin", "Win64_Shipping_Client"));
            Directory.CreateDirectory(Path.Combine(dir, "AssetPackages"));
            Directory.CreateDirectory(Path.Combine(dir, "ModuleData"));
            var version = variant == 1 ? "v1.4.7.9" : "v1.4.8.0";
            File.WriteAllText(Path.Combine(dir, "SubModule.xml"),
                $"<Module>\n  <Name value=\"LotRMPCore\"/>\n  <Id value=\"LotRMPCore\"/>\n  <Version value=\"{version}\"/>\n  <DefaultModule value=\"false\"/>\n  <ModuleCategory value=\"Multiplayer\"/>\n</Module>\n");
            File.WriteAllBytes(Path.Combine(dir, "bin", "Win64_Shipping_Client", "LotRMPCore.dll"), Rnd(600_000, 100));
            var pack = Rnd(24 << 20, 101);
            if (variant == 2) Array.Copy(Rnd(3 << 20, 102), 0, pack, 9 << 20, 3 << 20);
            File.WriteAllBytes(Path.Combine(dir, "AssetPackages", "lotr_core_assets.tpac"), pack);
            var sb = new StringBuilder("<Items>\n");
            for (int i = 0; i < 4000; i++) sb.Append($"  <Item id=\"core_item_{i}\" name=\"Core item {i}\" weight=\"{i % 17}.5\"/>\n");
            File.WriteAllText(Path.Combine(dir, "ModuleData", "core_items.xml"), sb.Append("</Items>\n").ToString());
            Console.WriteLine("fake core " + version + " in " + dir);
        }

        private sealed class NoProcesses : IProcessProbe
        {
            // The dev PC may be running the REAL game; installing into the FAKE folder must not be blocked by it.
            public System.Collections.Generic.IReadOnlyList<RunningProcessInfo> RunningGameProcesses() => new RunningProcessInfo[0];
        }

        private static int Serve(string filesRoot, int port, int kbps)
        {
            using (var s = new TestHttpServer(filesRoot, port) { ThrottleBytesPerSecond = kbps * 1024 })
            {
                s.OnRequest = r => Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + r);
                Console.WriteLine("serving " + s.BaseUrl + "files/ from " + filesRoot + " (Ctrl+C to stop)");
                var done = new ManualResetEventSlim();
                Console.CancelKeyPress += (a, e) => { e.Cancel = true; done.Set(); };
                done.Wait();
            }
            return 0;
        }

        private static async Task<int> Install(string game, string baseUrl, string home, string pem)
        {
            var cfg = new UpdaterConfig { BaseUrl = baseUrl, FixedGamePath = game, Dev = true, AppVersion = "devserver" };
            cfg.TrustedKeysPem.Add(File.ReadAllText(pem));
            var svc = new UpdaterService(cfg, new UpdaterPaths(home), locator: new DetectionGameLocator(), processes: new NoProcesses());
            svc.Log.Line += l => Console.WriteLine(l);
            svc.LocateGame();
            if (svc.Game == null) { Console.Error.WriteLine("fake game not found: " + svc.LastSearch?.Message); return 1; }
            var c = await svc.CheckAsync(null, CancellationToken.None);
            if (c.Error != null) { Console.Error.WriteLine("CHECK FAILED: " + c.Error); return 1; }
            foreach (var i in c.Plan.Issues) Console.WriteLine("issue: " + i);
            var actions = c.Plan.Modules.SelectMany(m => m.Actions).ToList();
            Console.WriteLine($"PLAN add={actions.Count(a => a.Kind == Lotr.Updater.Core.Planning.FileActionKind.Add)} replace={actions.Count(a => a.Kind == Lotr.Updater.Core.Planning.FileActionKind.Replace)} remove={actions.Count(a => a.Kind == Lotr.Updater.Core.Planning.FileActionKind.Remove)} download={Units.Bytes(c.Plan.DownloadBytes)} in {c.Plan.Downloads.Count} objects, reused locally {Units.Bytes(c.Plan.ReusedLocalBytes)}");
            if (c.Plan.HasChanges) await svc.InstallAsync(c.Plan, null, CancellationToken.None);
            var v = await svc.VerifyAsync(null, CancellationToken.None);
            Console.WriteLine($"RESULT installed={svc.State.InstalledReleaseId} healthy={v.Healthy} checked={v.Checked} extra={v.Extra.Count}");
            return v.Healthy ? 0 : 1;
        }
    }
}
