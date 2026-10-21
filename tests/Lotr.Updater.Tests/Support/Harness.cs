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
        /// <summary>Paths whose reading fails (e.g. a locked or unreadable Steam Workshop file).</summary>
        public Func<string, bool> Unreadable { get; set; }
        /// <summary>Thrown when the free space is read (drive that cannot be queried).</summary>
        public Exception FreeSpaceError { get; set; }

        public override long GetAvailableFreeSpace(string path)
        {
            if (FreeSpaceError != null) throw FreeSpaceError;
            return FreeSpace ?? base.GetAvailableFreeSpace(path);
        }

        /// <summary>Called before every read open: may act (e.g. delete the file, as Steam does on unsubscribe) and/or return an
        /// exception to throw.</summary>
        public Func<string, Exception> OnOpenRead { get; set; }
        /// <summary>Called before every write open (e.g. a full disk).</summary>
        public Func<string, Exception> OnOpenWrite { get; set; }
        /// <summary>Pretend every path pair is on different drives (game on another drive than the backup).</summary>
        public bool AllDifferentVolumes { get; set; }

        public override Stream OpenRead(string path)
        {
            if (Unreadable != null && Unreadable(path)) throw new IOException("The process cannot access the file '" + path + "' (simulated).");
            var ex = OnOpenRead?.Invoke(path);
            if (ex != null) throw ex;
            return base.OpenRead(path);
        }

        public override WriteProbeResult ProbeWritable(string directory) =>
            ReadOnly != null && ReadOnly(directory) ? new WriteProbeResult(false, "Access denied (simulated read-only folder)") : base.ProbeWritable(directory);

        public override Stream OpenWrite(string path, bool append)
        {
            if (ReadOnly != null && ReadOnly(path)) throw new UnauthorizedAccessException("Access to the path '" + path + "' is denied.");
            var ex = OnOpenWrite?.Invoke(path);
            if (ex != null) throw ex;
            return base.OpenWrite(path, append);
        }

        public override bool SameVolume(string a, string b) => !AllDifferentVolumes && base.SameVolume(a, b);

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
        /// <summary>null = Restart Manager cannot tell who holds the file.</summary>
        public List<RunningProcessInfo> Lockers { get; set; } = new List<RunningProcessInfo> { new RunningProcessInfo { Name = "Bannerlord.exe", Pid = 4242 } };
        public IReadOnlyList<RunningProcessInfo> WhoLocks(IReadOnlyList<string> paths) => (IReadOnlyList<RunningProcessInfo>)Lockers ?? new RunningProcessInfo[0];
    }

    /// <summary>Where the fake game lives and which store it looks like.</summary>
    public enum FakeStore { Steam, SteamOtherLibrary, Epic, Gog, Xbox }

    /// <summary>Temp sandbox: fake server root, fake game, updater home. Everything is deleted on Dispose.</summary>
    public sealed class Harness : IDisposable
    {
        public Harness() : this(FakeStore.Steam) { }

        public Harness(FakeStore store)
        {
            Root = Path.Combine(Path.GetTempPath(), "lotr-updater-tests", Guid.NewGuid().ToString("N").Substring(0, 10));
            ServerFiles = Path.Combine(Root, "server", "files");
            Releases = Path.Combine(ServerFiles, "releases");
            Directory.CreateDirectory(Releases);
            switch (store)
            {
                case FakeStore.SteamOtherLibrary:
                    // A second Steam library (e.g. D:\SteamLibrary): the Workshop folder is next to the game in that library.
                    SteamApps = Path.Combine(Root, "OtherDrive", "SteamLibrary", "steamapps");
                    GameRoot = FakeGame.Create(Path.Combine(SteamApps, "common", "Mount & Blade II Bannerlord"));
                    break;
                case FakeStore.Epic:
                case FakeStore.Gog:
                case FakeStore.Xbox:
                    GameRoot = FakeGame.Create(Path.Combine(Root, "Games", store + " Games", "MountAndBlade2"));
                    File.Delete(Path.Combine(GameRoot, "Modules", "Native", "steam.target"));
                    if (store == FakeStore.Epic) Directory.CreateDirectory(Path.Combine(GameRoot, ".egstore"));
                    if (store == FakeStore.Gog) File.WriteAllText(Path.Combine(GameRoot, "goggame-1802539526.info"), "{}");
                    if (store == FakeStore.Xbox) File.WriteAllText(Path.Combine(GameRoot, "MicrosoftGame.config"), "<Game/>");
                    break;
                default:
                    SteamApps = Path.Combine(Root, "game", "steamapps");
                    GameRoot = FakeGame.Create(Path.Combine(SteamApps, "common", "Mount & Blade II Bannerlord"));
                    break;
            }
            Home = Path.Combine(Root, "home");
            Server = new TestHttpServer(ServerFiles);
            Key = ReleaseBuilder.NewTestKey();
        }

        public string Root { get; }
        public string ServerFiles { get; }
        public string Releases { get; }
        public string GameRoot { get; }
        /// <summary>The fake Steam library's steamapps folder (null for other stores).</summary>
        public string SteamApps { get; }
        /// <summary>Folder of a Workshop item of Bannerlord in this library (Steam only).</summary>
        public string WorkshopItem(string item) => Path.Combine(SteamApps, "workshop", "content", "261550", item);
        public FakeLockFinder LockFinder { get; } = new FakeLockFinder();
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
            var s = new UpdaterService(cfg, new UpdaterPaths(Home), Fs, null, new DetectionGameLocator(), Processes, LockFinder, null, retry);
            s.Engine.Options.Sleep = ms => { lock (Delays) Delays.Add(TimeSpan.FromMilliseconds(ms)); };
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
