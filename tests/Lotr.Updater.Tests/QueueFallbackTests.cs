using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lotr.Updater.Core;
using Lotr.Updater.Testing;
using Xunit;

namespace Lotr.Updater.Tests
{
    /// <summary>
    /// The Core download queue is advisory: the files are static (R2 in production) and /api/queue goes through a tunnel to a
    /// server that may be down, which Cloudflare answers with 530/502. A queue that does not answer properly must never stop
    /// an install: one more try after a short pause, then the download goes ahead directly. 429 still means "wait in line",
    /// 404 still means "no queue here", and the player's own cancellation still stops everything.
    /// </summary>
    public class QueueFallbackTests
    {
        private static readonly byte[] CorePack = EngineTests.Random(200_000, 91);

        private static ReleaseBuilder CoreRelease(Harness h)
        {
            var r = h.NewRelease("core");
            r.AddModule("LotRMPCore", "LotRMPCore", "v1.4.7.9", "LotRMPCore", "core").SubModule()
                .File("AssetPackages/big.tpac", CorePack);
            return r;
        }

        private static async Task InstallAndAssert(Harness h, UpdaterService s)
        {
            var c = await s.CheckAsync(null, CancellationToken.None);
            Assert.Null(c.Error);
            Assert.True(c.Plan.NeedsCoreSlot);
            await s.InstallAsync(c.Plan, null, CancellationToken.None);
            Assert.Equal(CorePack, File.ReadAllBytes(h.ModuleFile("LotRMPCore", "AssetPackages/big.tpac")));
            Assert.Equal(c.Manifest.ReleaseId, s.State.InstalledReleaseId);
        }

        [Theory]
        [InlineData(530)] // Cloudflare: origin unreachable (the dormant server behind the tunnel)
        [InlineData(502)]
        [InlineData(503)]
        [InlineData(500)]
        [InlineData(520)]
        [InlineData(403)] // e.g. a Cloudflare challenge in front of /api
        [InlineData(408)]
        [InlineData(200)] // 200 with an HTML page instead of a grant
        public async Task A_broken_queue_gets_one_more_try_then_the_core_is_downloaded_directly(int status)
        {
            using (var h = new Harness())
            {
                CoreRelease(h).WriteTo(h.Releases);
                h.Server.QueueFailStatus = status;
                var s = h.Service();
                await InstallAndAssert(h, s);
                Assert.Equal(2, h.Server.QueueSlotRequests);
                Assert.Equal(0, h.Server.QueueSlotsGranted);
                Assert.Contains(TimeSpan.FromSeconds(2), h.Delays); // the pause before the second try
                Assert.Contains(s.Log.Session, l => l.Level == Core.Logging.LogLevel.Warn && l.Text.Contains("downloading directly"));
            }
        }

        [Fact]
        public async Task A_queue_that_never_answers_is_given_up_after_the_timeout()
        {
            using (var h = new Harness())
            {
                CoreRelease(h).WriteTo(h.Releases);
                h.Server.QueueHang = true;
                var s = h.Service();
                s.Queue.RequestTimeout = TimeSpan.FromMilliseconds(400);
                var sw = Stopwatch.StartNew();
                await InstallAndAssert(h, s);
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), "took " + sw.Elapsed);
                Assert.Equal(2, h.Server.QueueSlotRequests);
                Assert.Contains(s.Log.Session, l => l.Text.Contains("no answer within"));
            }
        }

        [Fact]
        public async Task No_queue_on_the_server_downloads_directly_without_a_second_try()
        {
            using (var h = new Harness())
            {
                CoreRelease(h).WriteTo(h.Releases);
                h.Server.QueueFailStatus = 404;
                var s = h.Service();
                await InstallAndAssert(h, s);
                Assert.Equal(1, h.Server.QueueSlotRequests);
                Assert.DoesNotContain(s.Log.Session, l => l.Level == Core.Logging.LogLevel.Warn);
            }
        }

        [Fact]
        public async Task A_busy_queue_is_still_waited_for_and_the_slot_is_used()
        {
            using (var h = new Harness())
            {
                CoreRelease(h).WriteTo(h.Releases);
                h.Server.QueueBusyAnswers = 3;
                var s = h.Service();
                await InstallAndAssert(h, s);
                Assert.Equal(4, h.Server.QueueSlotRequests);
                Assert.Equal(1, h.Server.QueueSlotsGranted);
                Assert.Equal(1, h.Server.QueueSlotsReleased);
                Assert.Equal(3, h.Delays.Count(d => d == TimeSpan.FromSeconds(1))); // retryAfter 1 s, three times
            }
        }

        [Fact]
        public async Task Cancelling_while_waiting_in_the_queue_stops()
        {
            using (var h = new Harness())
            {
                CoreRelease(h).WriteTo(h.Releases);
                h.Server.QueueBusyAnswers = 1000;
                var s = h.Service();
                using (var cts = new CancellationTokenSource())
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Queue.AcquireAsync(s.State.InstallId, (pos, wait) => cts.Cancel(), cts.Token));
                }
                Assert.True(h.Server.QueueSlotRequests >= 1);
                Assert.Equal(0, h.Server.QueueSlotsGranted);
            }
        }

        [Fact]
        public async Task Cancelling_while_the_queue_hangs_stops_at_once()
        {
            using (var h = new Harness())
            {
                CoreRelease(h).WriteTo(h.Releases);
                h.Server.QueueHang = true;
                var s = h.Service(); // default 15 s timeout: the cancellation must not wait for it
                using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
                {
                    var sw = Stopwatch.StartNew();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Queue.AcquireAsync(s.State.InstallId, null, cts.Token));
                    Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "took " + sw.Elapsed);
                }
            }
        }

        [Fact]
        public async Task A_later_hiccup_after_waiting_in_line_gets_its_own_second_try()
        {
            using (var h = new Harness())
            {
                CoreRelease(h).WriteTo(h.Releases);
                var s = h.Service();
                // 503, then 429 (in line), then 503 again, then a grant: the 429 resets the count, so the slot is used.
                int n = 0;
                h.Server.Faults = (path, hit) =>
                {
                    if (path != "/api/queue/slot") return null;
                    n++;
                    return n == 1 || n == 3 ? new Fault { Status = 503 } : null;
                };
                h.Server.QueueBusyAnswers = 1;
                await InstallAndAssert(h, s);
                Assert.Equal(1, h.Server.QueueSlotsGranted);
            }
        }
    }
}
