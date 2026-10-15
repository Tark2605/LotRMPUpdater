using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lotr.Updater.Core.Headless;
using Lotr.Updater.Testing;
using Xunit;

namespace Lotr.Updater.Tests
{
    /// <summary>The headless run used by tools/rehearsal (key=value report).</summary>
    public class HeadlessTests
    {
        [Fact]
        public async Task Headless_run_installs_verifies_and_sends_the_support_log()
        {
            using (var h = new Harness())
            {
                var r = h.NewRelease("h1");
                r.AddModule("OrderMiddleEarthEvent", "LotRMPEvent", "v1.0.2.2", "LotRMPEvent").SubModule()
                    .File("ModuleData/items.xml", EngineTests.Text(20_000, "item"));
                r.WriteTo(h.Releases);
                var s = h.Service();
                var w = new StringWriter();
                int code = await HeadlessRunner.RunAsync(s, new HeadlessOptions { SelfUpdate = true, SendSupport = true }, w, CancellationToken.None);
                var report = w.ToString();
                Assert.True(code == 0, report);
                Assert.Contains("selfUpdate=none", report);
                Assert.Contains("install=ok", report);
                Assert.Contains("verifyHealthy=True", report);
                Assert.Contains("supportCode=LOTR-7F3K", report);
                Assert.Contains("result=PASS", report);
                Assert.Contains("App version: test", h.Server.LastSupportBody);

                // Second run: nothing to do, still healthy; check-only never touches the game folder.
                w = new StringWriter();
                Assert.Equal(0, await HeadlessRunner.RunAsync(s, new HeadlessOptions { CheckOnly = true }, w, CancellationToken.None));
                Assert.Contains("install=nothing to do", w.ToString());
            }
        }
    }
}
