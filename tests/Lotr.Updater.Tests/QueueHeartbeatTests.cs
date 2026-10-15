using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lotr.Updater.Testing;
using Xunit;

namespace Lotr.Updater.Tests
{
    /// <summary>
    /// Core download slot (CONTRACTS §4, §8 15/10) with lotr-services semantics in the test server: a slot dies TTL after its
    /// grant or last heartbeat; with enforcement, a Core object request with a dead slot gets 403.
    /// </summary>
    public class QueueHeartbeatTests
    {
        private static readonly byte[] CorePack = EngineTests.Random(300_000, 77); // incompressible: stored .raw, 300 KB

        private static ReleaseBuilder CoreRelease(Harness h)
        {
            var r = h.NewRelease("core");
            r.AddModule("LotRMPCore", "LotRMPCore", "v1.4.7.9", "LotRMPCore", "core").SubModule()
                .File("AssetPackages/big.tpac", CorePack);
            return r;
        }

        [Fact]
        public async Task Heartbeats_keep_the_slot_alive_during_a_long_throttled_download()
        {
            using (var h = new Harness())
            {
                CoreRelease(h).WriteTo(h.Releases);
                h.Server.SlotTtl = TimeSpan.FromMilliseconds(700);
                h.Server.EnforceSlots = true;
                h.Server.ThrottleBytesPerSecond = 100 * 1024; // ≈ 3 s for the pack: 4 TTLs
                var s = h.Service();
                s.Queue.HeartbeatIntervalOverride = TimeSpan.FromMilliseconds(150);
                var c = await s.CheckAsync(null, CancellationToken.None);
                Assert.Null(c.Error);
                Assert.True(c.Plan.NeedsCoreSlot);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                Assert.True(sw.Elapsed > TimeSpan.FromMilliseconds(2100), "the download must outlast several slot TTLs (" + sw.Elapsed + ")");
                Assert.Equal(CorePack, File.ReadAllBytes(h.ModuleFile("LotRMPCore", "AssetPackages/big.tpac")));
                Assert.True(h.Server.Heartbeats >= 5, "heartbeats: " + h.Server.Heartbeats);
                Assert.Equal(0, h.Server.ExpiredHeartbeats);
                Assert.Equal(0, h.Server.ForbiddenObjectRequests);
                Assert.Equal(1, h.Server.QueueSlotsGranted); // the same slot the whole time
                Assert.Equal(1, h.Server.QueueSlotsReleased);
                Assert.Equal(0, h.Server.LiveSlots);
            }
        }

        [Fact]
        public async Task A_slot_expires_when_the_client_stops_sending_heartbeats()
        {
            using (var h = new Harness())
            {
                CoreRelease(h).WriteTo(h.Releases);
                h.Server.SlotTtl = TimeSpan.FromMilliseconds(400);
                h.Server.EnforceSlots = true;
                var s = h.Service();
                // A client that got a slot and then died (no heartbeat, no release).
                var token = await s.Queue.AcquireAsync(s.State.InstallId, null, CancellationToken.None);
                Assert.Equal(1, h.Server.LiveSlots);
                Assert.True(s.Queue.HeartbeatInterval <= TimeSpan.FromSeconds(60));
                await Task.Delay(700);
                Assert.Equal(0, h.Server.LiveSlots);
                Assert.False(await s.Queue.HeartbeatAsync(token, CancellationToken.None)); // 410 slot_expired
                Assert.Equal(1, h.Server.ExpiredHeartbeats);

                // Its token no longer opens Core objects (enforce mode).
                var c = await s.CheckAsync(null, CancellationToken.None);
                var obj = c.Plan.Downloads.First();
                var uri = new Uri(s.Source.ObjectUri(c.Manifest.ReleaseId, obj.ObjectName) + "?slot=" + token);
                using (var resp = await s.Fetcher.Client.GetAsync(uri))
                    Assert.Equal(403, (int)resp.StatusCode);
                Assert.Equal(1, h.Server.ForbiddenObjectRequests);
            }
        }

        [Fact]
        public async Task A_slot_lost_during_the_download_is_requested_again()
        {
            using (var h = new Harness())
            {
                CoreRelease(h).WriteTo(h.Releases);
                h.Server.SlotTtl = TimeSpan.FromSeconds(30);
                h.Server.ThrottleBytesPerSecond = 150 * 1024; // ≈ 2 s
                var s = h.Service();
                s.Queue.HeartbeatIntervalOverride = TimeSpan.FromMilliseconds(150);
                var c = await s.CheckAsync(null, CancellationToken.None);
                var expire = Task.Run(async () => { await Task.Delay(500); h.Server.ExpireAllSlots(); }); // e.g. the service restarted
                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                await expire;
                Assert.Equal(CorePack, File.ReadAllBytes(h.ModuleFile("LotRMPCore", "AssetPackages/big.tpac")));
                Assert.True(h.Server.ExpiredHeartbeats >= 1);
                Assert.Equal(2, h.Server.QueueSlotsGranted); // re-acquired after the 410
                Assert.Equal(0, h.Server.LiveSlots);         // the new slot was released at the end
            }
        }
    }
}
