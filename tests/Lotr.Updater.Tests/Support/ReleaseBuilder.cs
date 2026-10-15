using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using LotR.Releases.Publishing;
using LotR.Releases.Signing;
using Lotr.Updater.Core.Objects;

namespace Lotr.Updater.Testing
{
    /// <summary>
    /// Builds a REAL release with the shared publish library (LotR.Releases.ReleasePublisher: canonical manifest, zstd/raw
    /// objects, FastCDC chunks for files >= 16 MiB, ES256 signature file, channels.json) from module folders written to a temp
    /// folder. Signs with a throw-away test key. Each release gets a later publishedAt than the previous one.
    /// </summary>
    public sealed class ReleaseBuilder
    {
        private static long _clock;
        private static readonly DateTime BaseTime = new DateTime(2026, 10, 15, 14, 0, 0, DateTimeKind.Utc);

        public ReleaseBuilder(string label, string channel = "stable")
        {
            Label = label;
            Channel = channel;
        }

        public string Label { get; }
        public string Channel { get; }
        public string ReleaseId { get; private set; }
        public string ReleaseDir { get; private set; }
        public string GameMin { get; set; } = "v1.2.12";
        public string GameMax { get; set; }
        public string Notes { get; set; } = "Test release";
        public ECDsa SigningKey { get; set; }
        /// <summary>Override the publication time (anti-rollback tests).</summary>
        public DateTime? PublishedAtUtc { get; set; }
        public List<ModuleBuilder> Modules { get; } = new List<ModuleBuilder>();
        public byte[] ManifestBytes { get; private set; }

        public ModuleBuilder AddModule(string id, string name, string version, string folder, string role = "event")
        {
            var m = new ModuleBuilder { Id = id, Name = name, Version = version, Folder = folder, Role = role };
            Modules.Add(m);
            return m;
        }

        public static ECDsa NewTestKey() => EcKeys.Generate();

        public static string PublicPem(ECDsa key) => EcKeys.ToPem("PUBLIC KEY", EcKeys.ExportSpki(key));

        /// <summary>Publishes under &lt;releasesRoot&gt; (= what the server serves at /files/releases/) and points the channel to it.</summary>
        public void WriteTo(string releasesRoot, bool updateChannel = true)
        {
            var src = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(releasesRoot).TrimEnd('\\'))), "module-sources", Guid.NewGuid().ToString("N").Substring(0, 8));
            var sources = new List<ModuleSource>();
            foreach (var m in Modules)
            {
                var dir = Path.Combine(src, m.Folder);
                foreach (var f in m.Files)
                {
                    var p = Path.Combine(dir, f.Path.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(p));
                    File.WriteAllBytes(p, f.Content);
                }
                sources.Add(new ModuleSource { Folder = dir, Role = m.Role });
            }
            var now = PublishedAtUtc ?? BaseTime.AddHours(Interlocked.Increment(ref _clock));
            using (var signer = new ReleaseSigner(EcKeys.ImportPkcs8(EcKeys.ExportPkcs8(SigningKey))))
            {
                var req = new PublishRequest
                {
                    ReleasesRoot = releasesRoot,
                    Modules = sources,
                    Channel = Channel,
                    PublishedBy = "test:" + Label,
                    GameMinVersion = GameMin,
                    GameMaxVersion = GameMax,
                    BaseUrl = "http://localhost/",
                    NowUtc = now,
                    UpdateChannel = updateChannel,
                    Threads = 4,
                };
                req.Notes["en"] = Notes;
                req.Signers.Add(signer);
                var res = ReleasePublisher.Publish(req);
                ReleaseId = res.ReleaseId;
                ReleaseDir = res.ReleaseDir;
            }
            ManifestBytes = File.ReadAllBytes(Path.Combine(ReleaseDir, "manifest.json"));
            try { Directory.Delete(src, true); } catch { }
        }

        public static string Sha(byte[] b)
        {
            using (var s = SHA256.Create()) return ObjectCodec.Hex(s.ComputeHash(b));
        }

        /// <summary>Server path of the whole-file object of some content (ext chosen by the publisher's 3 % rule).</summary>
        public string ObjectUrlPath(byte[] content)
        {
            var sha = Sha(content);
            var dir = Path.Combine(ReleaseDir, "objects", sha.Substring(0, 2));
            var file = Directory.GetFiles(dir, sha + ".*").Single();
            return "/files/releases/" + ReleaseId + "/objects/" + sha.Substring(0, 2) + "/" + Path.GetFileName(file);
        }
    }

    public sealed class ModuleBuilder
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public string Folder { get; set; }
        public string Role { get; set; }
        public List<FileSpec> Files { get; } = new List<FileSpec>();

        public ModuleBuilder File(string path, byte[] content)
        {
            Files.Add(new FileSpec { Path = path, Content = content });
            return this;
        }

        public ModuleBuilder File(string path, string text) => File(path, Encoding.UTF8.GetBytes(text));

        public ModuleBuilder SubModule() => File("SubModule.xml", $"<Module>\n  <Name value=\"{Name}\"/>\n  <Id value=\"{Id}\"/>\n  <Version value=\"{Version}\"/>\n</Module>\n");
    }

    public sealed class FileSpec
    {
        public string Path { get; set; }
        public byte[] Content { get; set; }
    }

    /// <summary>A fake Bannerlord install in a temp folder (never the real game): enough for LotR.GameDetection.Validate.</summary>
    public static class FakeGame
    {
        public static string Create(string root, string version = "v1.2.12")
        {
            var bin = Path.Combine(root, "bin", "Win64_Shipping_Client");
            Directory.CreateDirectory(bin);
            System.IO.File.WriteAllBytes(Path.Combine(bin, "Bannerlord.exe"), new byte[] { 0x4d, 0x5a, 0, 0 });
            System.IO.File.WriteAllText(Path.Combine(bin, "Version.xml"), $"<Version>\n  <Singleplayer Value=\"{version}\" />\n</Version>\n");
            var native = Path.Combine(root, "Modules", "Native");
            Directory.CreateDirectory(native);
            System.IO.File.WriteAllText(Path.Combine(native, "SubModule.xml"), $"<Module><Name value=\"Native\"/><Id value=\"Native\"/><Version value=\"{version}\"/></Module>");
            System.IO.File.WriteAllText(Path.Combine(root, "Modules", "Native", "steam.target"), "");
            return root;
        }
    }
}
