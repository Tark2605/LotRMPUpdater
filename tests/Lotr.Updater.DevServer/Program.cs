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
    ///   serve &lt;filesRoot&gt; [port] [KB/s] [--queue ok|404|503|530|hang] [--busy N] [--lock]
    ///                                               serves &lt;filesRoot&gt; at http://localhost:port/files/ (+ /api stubs, /hub.json = &lt;filesRoot&gt;\site\hub.json);
    ///                                               --queue: how /api/queue/slot answers, --busy: 429 answers before a slot,
    ///                                               --lock: event lock (every path 423 except /status.json = &lt;filesRoot&gt;\site\status.json)
    ///   demo &lt;dir&gt;                                    a small signed test release (throw-away key) + a fake game + the expected
    ///                                               module files, for tools/linux/e2e-local.sh (Linux or Windows)
    ///   install &lt;game&gt; &lt;baseUrl&gt; &lt;home&gt; &lt;pub.pem&gt;   headless check + install + verify through the engine
    /// Everything lives in temp folders. Never the real game, never a real server, never a real signing key.
    /// </summary>
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("usage: fakegame <dir> | fakecore <dir> <1|2> | serve <filesRoot> [port] [KBps] [--queue ok|404|503|530|hang] [--busy N] [--lock] | demo <dir> | install <game> <baseUrl> <home> <pub.pem>"); return 2; }
            switch (args[0])
            {
                case "fakegame": Console.WriteLine(FakeGame.Create(Path.GetFullPath(args[1]))); return 0;
                case "fakecore": FakeCore(Path.GetFullPath(args[1]), int.Parse(args[2])); return 0;
                case "demo": return Demo(Path.GetFullPath(args[1]));
                case "serve":
                    var positional = args.Skip(2).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
                    var flags = args.Skip(2 + positional.Length).ToArray();
                    string Flag(string name) { int i = Array.IndexOf(flags, name); return i >= 0 && i + 1 < flags.Length ? flags[i + 1] : null; }
                    return Serve(Path.GetFullPath(args[1]), positional.Length > 0 ? int.Parse(positional[0]) : 8790, positional.Length > 1 ? int.Parse(positional[1]) : 0,
                        Flag("--queue") ?? "ok", int.Parse(Flag("--busy") ?? "0"), flags.Contains("--lock"));
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

        private static int Serve(string filesRoot, int port, int kbps, string queue = "ok", int busy = 0, bool eventLock = false)
        {
            using (var s = new TestHttpServer(filesRoot, port) { ThrottleBytesPerSecond = kbps * 1024, QueueBusyAnswers = busy })
            {
                switch (queue)
                {
                    case "ok": break;
                    case "hang": s.QueueHang = true; break;
                    default: s.QueueFailStatus = int.Parse(queue); break;
                }
                // Event lock (site deploy Set-EventNightMode.ps1): /status.json explains, everything else answers 423.
                if (eventLock) s.Faults = (path, hit) => path == "/status.json" ? null : new Fault { Status = 423 };
                s.OnRequest = r => Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + r);
                Console.WriteLine("serving " + s.BaseUrl + "files/ from " + filesRoot + " (queue " + queue + (busy > 0 ? ", " + busy + " busy answers" : "") + (eventLock ? ", event lock" : "") + "; Ctrl+C or SIGTERM to stop)");
                var done = new ManualResetEventSlim();
                Console.CancelKeyPress += (a, e) => { e.Cancel = true; done.Set(); };
                AppDomain.CurrentDomain.ProcessExit += (a, e) => done.Set();
                done.Wait();
            }
            return 0;
        }

        /// <summary>
        /// A complete local test bench in &lt;dir&gt; for tools/linux/e2e-local.sh: server\files (a release signed with a throw-away
        /// key: an event module and a small Core module with one 17 MiB chunked pack), test-key.pub.pem, a fake game in a fake
        /// Steam library (game\steamapps\common\Mount &amp; Blade II Bannerlord) and expected\&lt;folder&gt; (the module files, to compare).
        /// </summary>
        private static int Demo(string dir)
        {
            if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()) { Console.Error.WriteLine(dir + " is not empty."); return 2; }
            var files = Path.Combine(dir, "server", "files");
            var releases = Path.Combine(files, "releases");
            Directory.CreateDirectory(releases);
            Directory.CreateDirectory(Path.Combine(files, "site"));
            var game = FakeGame.Create(Path.Combine(dir, "game", "steamapps", "common", "Mount & Blade II Bannerlord"));
            using (var key = ReleaseBuilder.NewTestKey())
            {
                var r = new ReleaseBuilder("demo") { SigningKey = key, Notes = "Local test release (lotr-updater e2e)" };
                var ev = r.AddModule("OrderMiddleEarthEvent", "LotRMPEvent", "v1.0.2.7", "LotRMPEvent").SubModule();
                ev.File("bin/Win64_Shipping_Client/OrderMiddleEarthEvent.dll", Rnd(200_000, 1));
                var items = new StringBuilder("<Items>\n");
                for (int i = 0; i < 2000; i++) items.Append($"  <Item id=\"event_item_{i}\" name=\"Event item {i}\"/>\n");
                ev.File("ModuleData/event_items.xml", items.Append("</Items>\n").ToString());
                ev.File("SceneObj/ForestBattle/scene.xscene", "<scene unique_token=\"{00000000-0000-0000-0000-000000000001}\" revision=\"1\"/>\n");
                var core = r.AddModule("LotRMPCore", "LotRMPCore", "v1.4.7.9", "LotRMPCore", "core").SubModule();
                core.File("bin/Win64_Shipping_Client/LotRMPCore.dll", Rnd(300_000, 2));
                core.File("AssetPackages/lotr_core_assets.tpac", Rnd(17 << 20, 3));
                core.File("ModuleData/core_items.xml", "<Items>\n  <Item id=\"core_item\"/>\n</Items>\n");
                r.WriteTo(releases);
                File.WriteAllText(Path.Combine(dir, "test-key.pub.pem"), ReleaseBuilder.PublicPem(key));
                foreach (var m in r.Modules)
                    foreach (var f in m.Files)
                    {
                        var p = Path.Combine(dir, "expected", m.Folder, f.Path.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(p));
                        File.WriteAllBytes(p, f.Content);
                    }
                // The pause notice of an event lock; the e2e script copies it to server\files\site\status.json for that scenario only.
                File.WriteAllText(Path.Combine(dir, "status-lock.json"),
                    "{\"schema\":1,\"state\":\"event-lock\",\"title\":\"Downloads are paused\",\"message\":\"Battle night: downloads are paused until the end of the event.\"}");
                Console.WriteLine("release=" + r.ReleaseId);
            }
            Console.WriteLine("files=" + files);
            Console.WriteLine("game=" + game);
            Console.WriteLine("key=" + Path.Combine(dir, "test-key.pub.pem"));
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
