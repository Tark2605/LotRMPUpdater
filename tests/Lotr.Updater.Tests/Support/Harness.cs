using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Lotr.Updater.Core;
using Lotr.Updater.Core.Game;
using Lotr.Updater.Core.IO;
using Lotr.Updater.Core.Model;
using Lotr.Updater.Core.Net;
using Lotr.Updater.Testing;

namespace Lotr.Updater.Tests
{
    /// <summary>File system with injectable failures (full disk, read-only folder, failing move).</summary>
    public sealed class FaultyFileSystem : RealFileSystem
    {
        public long? FreeSpace { get; set; }
        public Func<string, bool> ReadOnly { get; set; }
        public Func<string, string, Exception> OnMove { get; set; }

        public override long GetAvailableFreeSpace(string path) => FreeSpace ?? base.GetAvailableFreeSpace(path);

        public override WriteProbeResult ProbeWritable(string directory) =>
            ReadOnly != null && ReadOnly(directory) ? new WriteProbeResult(false, "Access denied (simulated read-only folder)") : base.ProbeWritable(directory);

        public override Stream OpenWrite(string path, bool append)
        {
            if (ReadOnly != null && ReadOnly(path)) throw new UnauthorizedAccessException("Access to the path '" + path + "' is denied.");
            return base.OpenWrite(path, append);
        }

        public override void MoveFile(string source, string destination)
        {
            var ex = OnMove?.Invoke(source, destination);
            if (ex != null) throw ex;
            base.MoveFile(source, destination);
        }
    }

    public sealed class FakeProcesses : IProcessProbe
    {
        public List<RunningProcessInfo> Running { get; } = new List<RunningProcessInfo>();
        public IReadOnlyList<RunningProcessInfo> RunningGameProcesses() => Running;
    }

    public sealed class FakeLockFinder : ILockingProcessFinder
    {
        public IReadOnlyList<RunningProcessInfo> WhoLocks(IReadOnlyList<string> paths) => new[] { new RunningProcessInfo { Name = "Bannerlord.exe", Pid = 4242 } };
    }

    /// <summary>Temp sandbox: fake server root, fake game, updater home. Everything is deleted on Dispose.</summary>
    public sealed class Harness : IDisposable
    {
        public Harness()
        {
            Root = Path.Combine(Path.GetTempPath(), "lotr-updater-tests", Guid.NewGuid().ToString("N").Substring(0, 10));
            ServerFiles = Path.Combine(Root, "server", "files");
            Releases = Path.Combine(ServerFiles, "releases");
            Directory.CreateDirectory(Releases);
            GameRoot = FakeGame.Create(Path.Combine(Root, "game", "steamapps", "common", "Mount & Blade II Bannerlord"));
            Home = Path.Combine(Root, "home");
            Server = new TestHttpServer(ServerFiles);
            Key = ReleaseBuilder.NewTestKey();
        }

        public string Root { get; }
        public string ServerFiles { get; }
        public string Releases { get; }
        public string GameRoot { get; }
        public string Modules => Path.Combine(GameRoot, "Modules");
        public string Home { get; }
        public TestHttpServer Server { get; }
        public ECDsa Key { get; }
        public FaultyFileSystem Fs { get; } = new FaultyFileSystem();
        public FakeProcesses Processes { get; } = new FakeProcesses();
        public List<TimeSpan> Delays { get; } = new List<TimeSpan>();

        public ReleaseBuilder NewRelease(string id)
        {
            return new ReleaseBuilder(id) { SigningKey = Key };
        }

        public UpdaterService Service(Action<UpdaterConfig> configure = null)
        {
            var cfg = new UpdaterConfig { BaseUrl = Server.BaseUrl, FixedGamePath = GameRoot, AppVersion = "test" };
            cfg.TrustedKeysPem.Add(ReleaseBuilder.PublicPem(Key));
            configure?.Invoke(cfg);
            var retry = new RetryPolicy
            {
                Delay = (t, ct) => { lock (Delays) Delays.Add(t); return Task.CompletedTask; },
            };
            var s = new UpdaterService(cfg, new UpdaterPaths(Home), Fs, null, new DetectionGameLocator(), Processes, new FakeLockFinder(), null, retry);
            s.LocateGame();
            if (s.Game == null) throw new InvalidOperationException("fake game not found: " + s.LastSearch?.Message);
            return s;
        }

        public string ModuleFile(string folder, string rel) => Path.Combine(Modules, folder, rel.Replace('/', '\\'));

        public void Dispose()
        {
            Server.Dispose();
            Key.Dispose();
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
