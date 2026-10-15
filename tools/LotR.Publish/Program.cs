using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using LotR.Releases.Install;
using LotR.Releases.Json;
using LotR.Releases.Model;
using LotR.Releases.Publishing;
using LotR.Releases.Signing;

namespace LotR.Publish
{
    /// <summary>lotr-publish: release key, plan, publish, verify, channel, security, site data. See docs/MANIFEST.md.</summary>
    internal static class Program
    {
        private const string Usage = @"lotr-publish - LotR MP Event release tool (docs/MANIFEST.md)

Locations (pick one style):
  --root <deployRoot>        uses <root>\files\releases, <root>\data\secrets\release-signing.key/.pub.pem,
                             <root>\files\updater\latest.json
  --releases <dir>           the files\releases folder        --key <file>   private key (repeatable)
  --pub <file.pem>           trusted public key (repeatable; default: the key's own public part)

Commands:
  init-key   [--root R | --out <key>] [--pub-out <pem>] [--protection dpapi|plain] [--grant <account>|current]...
             [--no-acl] [--force]
  show-key   (--key <file> | --pub <pem>)                 prints keyId and fingerprint
  export-key [--root R | --key <file>] --out <file> [--protection plain|dpapi] [--grant ..] [--no-acl] [--force]
             copies the SAME key (backup to offline media = plain; restore on a server = dpapi)
  plan       <modules> [--channel stable] [--exact] [--json]           dry run: diff vs the channel's release
  publish    <modules> [--channel stable] [--notes ""text""] [--note fr=...] [--by admin-panel:name]
             [--game-min v1.2.12] [--game-max v..] [--base-url https://domain] [--no-channel] [--no-verify] [--scan] [--json]
  verify     <releaseDir> [--pub ..] [--json]              signature + every object decoded and hashed
  set-channel <channel> <releaseId>                         point a channel (rollback); checks the signature
  scan       <releaseDir> [--updater-exe <exe>] [--source-modules <dir>] [--script <Invoke-ReleaseScans.ps1>]
             runs tools/security (VirusTotal / Hybrid Analysis) -> <releaseDir>\security.json (unsigned sidecar);
             services without an API key are skipped by that script; exit 3 = run again later
  export-site-json --out <site>\src\data\releases.json [--channel stable] [--updater-latest <latest.json>]
  verify-install <moduleFolder> --release <releaseDir> [--quick]   compare an installed module with a release

<modules>: --event <dir> [--core <dir>] [--admin-client <dir>] [--workshop event=3774022783] [--exclude <glob>]
Common: --threads N (default min(cores,8))
Exit codes: 0 ok, 1 failed / verification error, 2 usage error.";

        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            if (args.Length == 0 || args[0] == "-h" || args[0] == "--help" || args[0] == "help")
            {
                Console.WriteLine(Usage);
                return args.Length == 0 ? 2 : 0;
            }

            Args a;
            try
            {
                a = Args.Parse(args.Skip(1));
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine("usage error: " + e.Message);
                return 2;
            }

            try
            {
                switch (args[0])
                {
                    case "init-key": return InitKey(a);
                    case "show-key": return ShowKey(a);
                    case "export-key": return ExportKey(a);
                    case "plan": return Plan(a);
                    case "publish": return Publish(a);
                    case "verify": return Verify(a);
                    case "set-channel": return SetChannel(a);
                    case "scan": return Scan(a, a.Positional(0, "releaseDir"), null);
                    case "export-site-json": return ExportSite(a);
                    case "verify-install": return VerifyInstall(a);
                    default:
                        Console.Error.WriteLine("unknown command '" + args[0] + "'\n\n" + Usage);
                        return 2;
                }
            }
            catch (UsageException e)
            {
                Console.Error.WriteLine("usage error: " + e.Message);
                return 2;
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                Console.Error.WriteLine("error: " + e.Message);
                if (Environment.GetEnvironmentVariable("LOTR_PUBLISH_DEBUG") == "1")
                {
                    Console.Error.WriteLine(e);
                }

                return 1;
            }
        }

        // ---------------------------------------------------------------- keys

        private static int InitKey(Args a)
        {
            string keyPath = a.One("out") ?? (a.Root != null ? a.DefaultKey : throw new UsageException("give --out <file> or --root <deployRoot>"));
            string pubPath = a.One("pub-out") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(keyPath)), Path.GetFileNameWithoutExtension(keyPath) + ".pub.pem");
            var protection = (a.One("protection") ?? "dpapi") switch
            {
                "dpapi" => KeyProtection.DpapiMachine,
                "plain" => KeyProtection.Plain,
                var p => throw new UsageException("--protection must be dpapi or plain, not " + p),
            };
            bool acl = !a.Flag("no-acl");
            if (acl && !KeyStore.IsElevated() && !a.Many("grant").Any())
            {
                Console.Error.WriteLine("warning: not elevated and no --grant: you will not be able to read the key back without elevation.");
            }

            using (var key = EcKeys.Generate())
            {
                KeyStore.Save(keyPath, key, protection, acl, a.Many("grant"), a.Flag("force"));
                byte[] spki = EcKeys.ExportSpki(key);
                AtomicFile.Write(pubPath, Encoding.ASCII.GetBytes(EcKeys.ToPem("PUBLIC KEY", spki)));
                Console.WriteLine("private key : " + Path.GetFullPath(keyPath) + " (" + (protection == KeyProtection.DpapiMachine ? "DPAPI machine scope" : "plain PKCS#8") + (acl ? ", ACL Administrators+SYSTEM" + string.Concat(a.Many("grant").Select(g => "+" + g)) : ", inherited ACL") + ")");
                Console.WriteLine("public key  : " + Path.GetFullPath(pubPath));
                Console.WriteLine("keyId       : " + EcKeys.KeyId(spki));
                Console.WriteLine("fingerprint : " + EcKeys.Fingerprint(spki));
                Console.WriteLine("Back up the private key offline (init-key --protection plain to a USB stick, or export before rotation).");
            }

            return 0;
        }

        private static int ExportKey(Args a)
        {
            string src = a.One("key") ?? a.DefaultKey ?? throw new UsageException("give --key <file> or --root");
            string dst = a.One("out") ?? throw new UsageException("--out <file>");
            var protection = (a.One("protection") ?? "plain") == "dpapi" ? KeyProtection.DpapiMachine : KeyProtection.Plain;
            using (var key = KeyStore.Load(src))
            {
                KeyStore.Save(dst, key, protection, !a.Flag("no-acl"), a.Many("grant"), a.Flag("force"));
                Console.WriteLine("exported " + EcKeys.Fingerprint(EcKeys.ExportSpki(key)) + " to " + Path.GetFullPath(dst) + " (" + (protection == KeyProtection.Plain ? "plain PKCS#8: keep it OFFLINE" : "DPAPI machine scope") + ")");
            }

            return 0;
        }

        private static int ShowKey(Args a)
        {
            byte[] spki;
            if (a.One("pub") != null)
            {
                spki = EcKeys.SpkiFromPublicPem(File.ReadAllText(a.One("pub")));
            }
            else
            {
                string keyPath = a.One("key") ?? a.DefaultKey ?? throw new UsageException("give --key or --pub");
                using (var key = KeyStore.Load(keyPath))
                {
                    spki = EcKeys.ExportSpki(key);
                }
            }

            Console.WriteLine("keyId       : " + EcKeys.KeyId(spki));
            Console.WriteLine("fingerprint : " + EcKeys.Fingerprint(spki));
            Console.Write(EcKeys.ToPem("PUBLIC KEY", spki));
            return 0;
        }

        // ---------------------------------------------------------------- plan / publish

        private static PublishRequest Request(Args a, bool needKeys)
        {
            var req = new PublishRequest
            {
                ReleasesRoot = a.Releases,
                Channel = a.One("channel") ?? "stable",
                PublishedBy = a.One("by") ?? "lotr-publish",
                GameMinVersion = a.One("game-min"),
                GameMaxVersion = a.One("game-max"),
                BaseUrl = a.One("base-url"),
                Threads = a.Int("threads", Math.Min(Environment.ProcessorCount, 8)),
                UpdateChannel = !a.Flag("no-channel"),
                VerifyAfterPublish = !a.Flag("no-verify"),
            };

            var workshop = a.Many("workshop").Select(w => w.Split(new[] { '=' }, 2)).ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : throw new UsageException("--workshop role=id"));
            var excludes = a.Many("exclude").ToList();
            foreach (var role in ModuleRoles.All)
            {
                foreach (var dir in a.Many(role))
                {
                    req.Modules.Add(new ModuleSource { Folder = dir, Role = role, WorkshopItem = workshop.TryGetValue(role, out var w) ? w : null, Excludes = excludes });
                }
            }

            if (req.Modules.Count == 0)
            {
                throw new UsageException("give at least one module folder: --event <dir> [--core <dir>] [--admin-client <dir>]");
            }

            if (a.One("notes") != null)
            {
                req.Notes["en"] = a.One("notes");
            }

            foreach (var n in a.Many("note"))
            {
                var p = n.Split(new[] { '=' }, 2);
                if (p.Length != 2)
                {
                    throw new UsageException("--note lang=text");
                }

                req.Notes[p[0]] = p[1];
            }

            req.TrustedKeys.AddRange(a.Many("pub").Select(TrustedKey.FromPemFile));
            if (needKeys)
            {
                var keys = a.Many("key").ToList();
                if (keys.Count == 0 && a.DefaultKey != null)
                {
                    keys.Add(a.DefaultKey);
                }

                if (keys.Count == 0)
                {
                    throw new UsageException("publish needs --key <file> (or --root)");
                }

                foreach (var k in keys)
                {
                    req.Signers.Add(new ReleaseSigner(KeyStore.Load(k)));
                }
            }
            else if (a.DefaultPub != null && File.Exists(a.DefaultPub))
            {
                req.TrustedKeys.Add(TrustedKey.FromPemFile(a.DefaultPub));
            }

            if (!a.Flag("json"))
            {
                req.Progress = new ConsoleProgress();
                req.Log = new Progress<string>(s => Console.Error.WriteLine(s));
            }

            return req;
        }

        private static int Plan(Args a)
        {
            var req = Request(a, needKeys: false);
            var plan = ReleasePublisher.Plan(req, a.Flag("exact"));
            if (a.Flag("json"))
            {
                var o = new JsonObject
                {
                    ["releaseId"] = plan.Manifest.ReleaseId,
                    ["channel"] = plan.Manifest.Channel,
                    ["filesHashed"] = plan.FilesHashed,
                    ["bytesHashed"] = plan.BytesHashed,
                    ["hashMs"] = (long)plan.HashElapsed.TotalMilliseconds,
                    ["currentSignature"] = plan.CurrentSignature?.Message,
                    ["diff"] = plan.Diff.ToJson(),
                    ["warnings"] = new JsonArray(plan.Warnings.Select(w => (JsonNode)w)),
                };
                Console.Write(o.ToPrettyString());
                return 0;
            }

            PrintPlan(plan);
            return 0;
        }

        private static int Publish(Args a)
        {
            var req = Request(a, needKeys: true);
            try
            {
                if (req.BaseUrl == null)
                {
                    Console.Error.WriteLine("note: no --base-url; endpoints are inherited from the current release (or left out).");
                }

                var r = ReleasePublisher.Publish(req);
                if (a.Flag("json"))
                {
                    var o = new JsonObject
                    {
                        ["releaseId"] = r.ReleaseId,
                        ["releaseDir"] = r.ReleaseDir,
                        ["channelUpdated"] = r.ChannelUpdated,
                        ["writtenObjects"] = r.WrittenObjects,
                        ["writtenBytes"] = r.WrittenBytes,
                        ["linkedObjects"] = r.LinkedObjects,
                        ["linkedBytes"] = r.LinkedBytes,
                        ["copiedObjects"] = r.CopiedObjects,
                        ["ms"] = (long)r.Elapsed.TotalMilliseconds,
                        ["verified"] = r.Verification?.Ok,
                        ["diff"] = r.Plan.Diff.ToJson(),
                        ["warnings"] = new JsonArray(r.Plan.Warnings.Select(w => (JsonNode)w)),
                    };
                    Console.Write(o.ToPrettyString());
                    return 0;
                }

                PrintPlan(r.Plan);
                Console.WriteLine();
                Console.WriteLine("published   : " + r.ReleaseId + " -> " + r.ReleaseDir);
                Console.WriteLine("objects     : " + r.WrittenObjects + " written (" + Mb(r.WrittenBytes) + "), " + r.LinkedObjects + " hard-linked from older releases (" + Mb(r.LinkedBytes) + ")" + (r.CopiedObjects > 0 ? ", " + r.CopiedObjects + " copied (no hard link possible)" : string.Empty));
                if (r.Verification != null)
                {
                    Console.WriteLine("verify      : OK, " + r.Verification.ObjectsChecked + " objects decoded in " + r.Verification.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
                }

                Console.WriteLine("channel     : " + (r.ChannelUpdated ? req.Channel + " -> " + r.ReleaseId : "not changed (--no-channel)"));
                Console.WriteLine("time        : " + r.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
                if (a.Flag("scan"))
                {
                    // A scan problem never undoes a publication: the sidecar is optional (CONTRACTS §8).
                    Scan(a, r.ReleaseDir, Path.GetDirectoryName(Path.GetFullPath(req.Modules[0].Folder).TrimEnd('\\', '/')));
                }

                return 0;
            }
            finally
            {
                foreach (var s in req.Signers)
                {
                    s.Dispose();
                }
            }
        }

        private static void PrintPlan(PlanResult plan)
        {
            var d = plan.Diff;
            Console.WriteLine("release     : " + plan.Manifest.ReleaseId + " (channel " + plan.Manifest.Channel + ")");
            Console.WriteLine("compared to : " + (d.FromReleaseId ?? "(nothing published yet)") + (plan.CurrentSignature != null ? " - signature: " + plan.CurrentSignature.Message : string.Empty));
            Console.WriteLine("hashed      : " + plan.FilesHashed + " files, " + Mb(plan.BytesHashed) + " in " + plan.HashElapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
            foreach (var m in d.Modules)
            {
                Console.WriteLine();
                Console.WriteLine("module " + m.Id + " [" + m.Folder + "] " + (m.OldVersion ?? "-") + " -> " + (m.NewVersion ?? "-") + " : " + m.Status + ", " + m.FileCount + " files, " + Mb(m.TotalSize));
                Console.WriteLine("  added " + m.Added.Count + " (" + Mb(m.Added.Sum(x => x.NewSize)) + "), changed " + m.Changed.Count + " (" + Mb(m.Changed.Sum(x => x.NewSize)) + "), removed " + m.Removed.Count + ", unchanged " + m.Unchanged);
                foreach (var x in m.Added.Take(15))
                {
                    Console.WriteLine("    + " + x.Path + "  " + Mb(x.NewSize));
                }

                foreach (var x in m.Changed.Take(15))
                {
                    Console.WriteLine("    * " + x.Path + "  " + Mb(x.OldSize) + " -> " + Mb(x.NewSize));
                }

                foreach (var x in m.Removed.Take(15))
                {
                    Console.WriteLine("    - " + x.Path);
                }

                int more = Math.Max(0, m.Added.Count - 15) + Math.Max(0, m.Changed.Count - 15) + Math.Max(0, m.Removed.Count - 15);
                if (more > 0)
                {
                    Console.WriteLine("    ... " + more + " more (use --json for the full list)");
                }
            }

            Console.WriteLine();
            string est = d.Estimated ? " (upper bound: new objects counted uncompressed; --exact compresses them)" : string.Empty;
            Console.WriteLine("download    : update from current " + Mb(d.UpdateDownloadBytes) + " in " + d.NewObjects + " new objects; fresh install " + Mb(d.FreshInstallBytes) + " for " + Mb(d.TotalSize) + " of files" + est);
            foreach (var w in plan.Warnings.Take(30))
            {
                Console.WriteLine("warning: " + w);
            }
        }

        // ---------------------------------------------------------------- verify / channel / security / site

        private static List<TrustedKey> Trusted(Args a)
        {
            var list = a.Many("pub").Select(TrustedKey.FromPemFile).ToList();
            if (list.Count == 0 && a.DefaultPub != null && File.Exists(a.DefaultPub))
            {
                list.Add(TrustedKey.FromPemFile(a.DefaultPub));
            }

            foreach (var k in a.Many("key"))
            {
                using (var key = KeyStore.Load(k))
                {
                    list.Add(new TrustedKey(EcKeys.ExportSpki(key)));
                }
            }

            return list;
        }

        private static int Verify(Args a)
        {
            string dir = a.Positional(0, "releaseDir");
            var trusted = Trusted(a);
            if (trusted.Count == 0)
            {
                throw new UsageException("verify needs a trusted key: --pub <pem> (or --root)");
            }

            var r = ReleaseVerifier.Verify(dir, trusted, a.Int("threads", Math.Min(Environment.ProcessorCount, 8)));
            if (a.Flag("json"))
            {
                Console.Write(new JsonObject
                {
                    ["releaseId"] = r.ReleaseId,
                    ["ok"] = r.Ok,
                    ["signature"] = r.Signature?.Message,
                    ["files"] = r.FilesChecked,
                    ["objects"] = r.ObjectsChecked,
                    ["objectBytes"] = r.ObjectBytes,
                    ["decodedBytes"] = r.BytesDecoded,
                    ["ms"] = (long)r.Elapsed.TotalMilliseconds,
                    ["errors"] = new JsonArray(r.Errors.Select(e => (JsonNode)e)),
                    ["warnings"] = new JsonArray(r.Warnings.Select(e => (JsonNode)e)),
                }.ToPrettyString());
            }
            else
            {
                Console.WriteLine("release   : " + r.ReleaseId);
                Console.WriteLine("signature : " + r.Signature?.Message);
                Console.WriteLine("checked   : " + r.FilesChecked + " files, " + r.ObjectsChecked + " objects (" + Mb(r.ObjectBytes) + " stored, " + Mb(r.BytesDecoded) + " decoded) in " + r.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
                foreach (var e in r.Errors)
                {
                    Console.WriteLine("ERROR: " + e);
                }

                foreach (var w in r.Warnings.Take(30))
                {
                    Console.WriteLine("warning: " + w);
                }

                Console.WriteLine(r.Ok ? "RESULT: OK" : "RESULT: FAILED");
            }

            return r.Ok ? 0 : 1;
        }

        private static int SetChannel(Args a)
        {
            string channel = a.Positional(0, "channel");
            string id = a.Positional(1, "releaseId");
            var trusted = Trusted(a);
            var rel = ReleaseFolder.Open(Path.Combine(a.Releases, id));
            var check = rel.SignatureBytes == null ? null : ManifestSignature.Verify(rel.ManifestBytes, rel.SignatureBytes, trusted);
            if (check == null || !check.Valid)
            {
                throw new InvalidOperationException("release " + id + " signature does not verify (" + (check?.Message ?? "no signature") + "); refusing to point a channel at it");
            }

            if (rel.Manifest.Channel != channel)
            {
                Console.Error.WriteLine("warning: the manifest was published for channel '" + rel.Manifest.Channel + "'; updaters check that field and will refuse it on '" + channel + "'.");
            }

            ReleasePublisher.SetChannel(a.Releases, channel, id);
            Console.WriteLine(channel + " -> " + id);
            return 0;
        }

        /// <summary>
        /// Runs tools/security/Invoke-ReleaseScans.ps1 for a release (CONTRACTS §8: results go to the unsigned
        /// security.json sidecar; the manifest is never touched). The script finds its own API keys
        /// (env LOTR_VT_APIKEY/LOTR_HA_APIKEY or the secrets file, docs/SECURITY-AUTOMATION.md §2) and skips a
        /// service cleanly when it has none. This tool never reads those keys itself.
        /// REVIEW-AFTER(security-automation): objects are .zst/.raw, so the scanner needs -SourceModules (the parent of
        /// the module folders, passed automatically after publish); not run against real keys in this workstream.
        /// </summary>
        private static int Scan(Args a, string releaseDir, string sourceModules)
        {
            string script = a.One("script") ?? FindScanScript();
            if (script == null || !File.Exists(script))
            {
                Console.Error.WriteLine("scan: tools\\security\\Invoke-ReleaseScans.ps1 not found (give --script); skipped.");
                return 0;
            }

            var psArgs = new StringBuilder("-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ");
            psArgs.Append(Quote(script)).Append(" -ReleaseDir ").Append(Quote(Path.GetFullPath(releaseDir)));
            string updater = a.One("updater-exe");
            if (updater != null)
            {
                psArgs.Append(" -UpdaterExe ").Append(Quote(Path.GetFullPath(updater)));
            }

            sourceModules = a.One("source-modules") ?? sourceModules;
            if (sourceModules != null)
            {
                psArgs.Append(" -SourceModules ").Append(Quote(Path.GetFullPath(sourceModules)));
            }

            var psi = new ProcessStartInfo("powershell.exe", psArgs.ToString()) { UseShellExecute = false };
            Console.Error.WriteLine("scan: " + script);
            using (var p = Process.Start(psi))
            {
                p.WaitForExit();
                Console.Error.WriteLine("scan: exit " + p.ExitCode + (p.ExitCode == 3 ? " (incomplete: run 'lotr-publish scan' again later)" : string.Empty));
                return p.ExitCode;
            }
        }

        private static string FindScanScript()
        {
            // The tool runs from <repo>/tools/LotR.Publish/bin/<cfg>/net48 or from a deployed <root>/tools/... folder.
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 6 && dir != null; i++)
            {
                foreach (var candidate in new[] { Path.Combine(dir, "security", "Invoke-ReleaseScans.ps1"), Path.Combine(dir, "tools", "security", "Invoke-ReleaseScans.ps1") })
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                dir = Path.GetDirectoryName(dir.TrimEnd('\\'));
            }

            return null;
        }

        private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

        private static int ExportSite(Args a)
        {
            string output = a.One("out") ?? throw new UsageException("--out <site>\\src\\data\\releases.json");
            var warnings = new List<string>();
            string latest = a.One("updater-latest") ?? (a.Root != null ? Path.Combine(a.Root, "files", "updater", "latest.json") : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(a.Releases)), "updater", "latest.json"));
            var data = SiteExporter.Build(a.Releases, a.One("channel") ?? "stable", Trusted(a), latest, warnings);
            SiteExporter.Write(output, data);
            foreach (var w in warnings)
            {
                Console.Error.WriteLine("warning: " + w);
            }

            Console.WriteLine("wrote " + Path.GetFullPath(output) + " (current: " + (data["current"] is JsonObject c ? c["releaseId"].AsString() : "null") + ")");
            return 0;
        }

        private static int VerifyInstall(Args a)
        {
            string folder = a.Positional(0, "moduleFolder");
            string relDir = a.One("release") ?? throw new UsageException("--release <releaseDir>");
            var rel = ReleaseFolder.Open(relDir);
            var trusted = Trusted(a);
            if (trusted.Count > 0)
            {
                var check = ManifestSignature.Verify(rel.ManifestBytes, rel.SignatureBytes ?? new byte[0], trusted);
                Console.WriteLine("signature : " + check.Message);
                if (!check.Valid)
                {
                    return 1;
                }
            }

            string name = Path.GetFileName(Path.GetFullPath(folder).TrimEnd('\\', '/'));
            var mod = rel.Manifest.Modules.FirstOrDefault(m => string.Equals(m.Folder, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("release " + rel.Id + " has no module with folder '" + name + "'");
            var sw = Stopwatch.StartNew();
            var r = InstalledModuleVerifier.Verify(folder, mod, a.Flag("quick") ? InstallCheckMode.SizeOnly : InstallCheckMode.Full, a.Int("threads", 4));
            Console.WriteLine("module    : " + mod.Id + " " + mod.Version + " in " + Path.GetFullPath(folder));
            Console.WriteLine("checked   : " + r.Files.Count + " files, " + Mb(r.BytesHashed) + " hashed in " + sw.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
            foreach (var p in r.Problems)
            {
                Console.WriteLine("  " + p.State + ": " + p.Path + (p.Error != null ? " (" + p.Error + ")" : string.Empty));
            }

            foreach (var x in r.Extra.Take(50))
            {
                Console.WriteLine("  extra: " + x);
            }

            Console.WriteLine(r.Ok ? "RESULT: OK" : "RESULT: " + r.Problems.Count() + " problem(s)");
            return r.Ok ? 0 : 1;
        }

        internal static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
    }

    internal sealed class UsageException : Exception
    {
        public UsageException(string message)
            : base(message)
        {
        }
    }

    internal sealed class ConsoleProgress : IProgress<ProcessProgress>
    {
        private readonly Stopwatch sw = Stopwatch.StartNew();

        public void Report(ProcessProgress p)
        {
            double mbs = p.BytesDone / 1048576.0 / Math.Max(0.001, sw.Elapsed.TotalSeconds);
            string line = string.Format(CultureInfo.InvariantCulture, "\r[{0}] {1}/{2} files  {3}/{4}  {5:0} MB/s   ", p.Phase, p.FilesDone, p.FilesTotal, Program.Mb(p.BytesDone), Program.Mb(p.BytesTotal), mbs);
            Console.Error.Write(line);
            if (p.FilesDone == p.FilesTotal)
            {
                Console.Error.WriteLine();
                sw.Restart();
            }
        }
    }

    internal sealed class Args
    {
        private static readonly HashSet<string> Flags = new HashSet<string> { "exact", "json", "no-channel", "no-verify", "no-acl", "force", "quick", "scan" };
        private readonly Dictionary<string, List<string>> named = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private readonly List<string> positional = new List<string>();

        public static Args Parse(IEnumerable<string> args)
        {
            var a = new Args();
            var list = args.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                string s = list[i];
                if (!s.StartsWith("--", StringComparison.Ordinal))
                {
                    a.positional.Add(s);
                    continue;
                }

                string name = s.Substring(2);
                string value = null;
                int eq = name.IndexOf('=');
                if (eq > 0 && !Flags.Contains(name.Substring(0, eq)))
                {
                    value = name.Substring(eq + 1);
                    name = name.Substring(0, eq);
                }
                else if (!Flags.Contains(name))
                {
                    if (i + 1 >= list.Count)
                    {
                        throw new ArgumentException("--" + name + " needs a value");
                    }

                    value = list[++i];
                }

                if (!a.named.TryGetValue(name, out var values))
                {
                    a.named[name] = values = new List<string>();
                }

                values.Add(value ?? "true");
            }

            return a;
        }

        public string Root => One("root");

        public string Releases => One("releases") ?? (Root != null ? Path.Combine(Root, "files", "releases") : throw new UsageException("give --releases <dir> or --root <deployRoot>"));

        public string DefaultKey => Root != null ? Path.Combine(Root, "data", "secrets", "release-signing.key") : null;

        public string DefaultPub => Root != null ? Path.Combine(Root, "data", "secrets", "release-signing.pub.pem") : null;

        public string One(string name) => named.TryGetValue(name, out var v) ? v[v.Count - 1] : null;

        public IEnumerable<string> Many(string name) => named.TryGetValue(name, out var v) ? (IEnumerable<string>)v : new string[0];

        public bool Flag(string name) => named.ContainsKey(name);

        public int Int(string name, int fallback) => One(name) is string s ? int.Parse(s, CultureInfo.InvariantCulture) : fallback;

        public string Positional(int index, string what) => index < positional.Count ? positional[index] : throw new UsageException("missing <" + what + ">");
    }
}
