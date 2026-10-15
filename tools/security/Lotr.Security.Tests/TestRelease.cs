using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Lotr.Security.Tests;

/// <summary>Builds a fake release folder (manifest.json + raw objects) in the shape of docs/CONTRACTS.md §1-2.</summary>
public sealed class TestRelease : IDisposable
{
    public readonly TempDir Dir = new TempDir();
    public string ReleaseDir => Dir.Combine("files", "releases", "20261015-140000-a1b2c3d4");
    public string Manifest => Path.Combine(ReleaseDir, "manifest.json");
    public string SecurityJson => Path.Combine(ReleaseDir, "security.json");
    public string StateDir => Dir.Combine("state");
    public readonly List<(string module, string path, string sha, string local)> Files = new List<(string, string, string, string)>();

    /// <summary>Adds a file. <paramref name="asObject"/>=false keeps it only in a "source Modules" folder.</summary>
    public string Add(string module, string path, int size, int seed, bool exec = true, bool asObject = true)
    {
        string src = Dir.WriteFile(Path.Combine("Modules", module, path.Replace('/', '\\')), size, seed);
        string sha = Hashing.Sha256File(src);
        if (asObject)
        {
            string obj = Path.Combine(ReleaseDir, "objects", sha.Substring(0, 2), sha + ".raw");
            Directory.CreateDirectory(Path.GetDirectoryName(obj));
            if (!File.Exists(obj)) File.Copy(src, obj);
        }
        Files.Add((module, path, sha, src));
        Write(exec);
        return sha;
    }

    private readonly Dictionary<string, bool> _exec = new Dictionary<string, bool>();

    private void Write(bool lastExec)
    {
        _exec[Files.Last().module + "/" + Files.Last().path] = lastExec;
        var sb = new StringBuilder();
        sb.Append("{\"schema\":1,\"releaseId\":\"20261015-140000-a1b2c3d4\",\"channel\":\"stable\",\"modules\":[");
        bool firstM = true;
        foreach (var g in Files.GroupBy(f => f.module))
        {
            if (!firstM) sb.Append(',');
            firstM = false;
            sb.Append("{\"id\":\"").Append(g.Key).Append("\",\"folder\":\"").Append(g.Key).Append("\",\"files\":[");
            bool firstF = true;
            foreach (var f in g)
            {
                if (!firstF) sb.Append(',');
                firstF = false;
                long size = new FileInfo(f.local).Length;
                sb.Append($"{{\"path\":\"{f.path}\",\"size\":{size},\"sha256\":\"{f.sha}\",\"object\":\"{f.sha}.raw\",\"objectSize\":{size},\"exec\":{(_exec[f.module + "/" + f.path] ? "true" : "false")}}}");
            }
            sb.Append("]}");
        }
        sb.Append("]}");
        Directory.CreateDirectory(ReleaseDir);
        File.WriteAllText(Manifest, sb.ToString());
    }

    public ScanRequest Request(Action<ScanRequest> tweak = null)
    {
        var r = new ScanRequest { Manifest = Manifest, StateDir = StateDir };
        r.Vt.PollInterval = TimeSpan.FromSeconds(30);
        tweak?.Invoke(r);
        return r;
    }

    public static Secrets Keys(string vt = "vt-test-key-0123456789", string ha = "ha-test-key-0123456789")
    {
        var env = new Dictionary<string, string> { [Secrets.VtEnv] = vt, [Secrets.HaEnv] = ha };
        return Secrets.Load(Path.Combine(Path.GetTempPath(), "lotr-security-tests", "does-not-exist.json"), n => env.TryGetValue(n, out var v) ? v : null);
    }

    public int RunVt(FakeHttp http, FakeClock clock, MemoryLog log, ScanRequest req, Secrets secrets = null)
    {
        http.Clock = clock;
        return new Runner(secrets ?? Keys(), http, clock, log).VirusTotalAsync(req, CancellationToken.None).GetAwaiter().GetResult();
    }

    public SecuritySidecar Sidecar() => SecuritySidecar.Load(SecurityJson);

    public void Dispose() => Dir.Dispose();
}
