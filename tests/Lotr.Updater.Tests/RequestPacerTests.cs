using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lotr.Updater.Core.Net;
using Xunit;

namespace Lotr.Updater.Tests
{
    /// <summary>The shared request pace.</summary>
    public class RequestPacerTests
    {
        /// <summary>Fake clock: a wait moves the clock forward, so each call's start time is "now" after its wait.</summary>
        private sealed class FakeTime
        {
            public TimeSpan Now;
            public readonly List<TimeSpan> Waits = new List<TimeSpan>();
            public RequestPacer Pacer(double rate) => new RequestPacer(rate) { Now = () => Now, Delay = (t, ct) => { Waits.Add(t); return Task.CompletedTask; } };
        }

        [Fact]
        public async Task A_burst_never_goes_over_the_rate_in_any_10_s_window()
        {
            var t = new FakeTime();
            var p = t.Pacer(RequestPacer.DefaultRequestsPerSecond);
            var starts = new List<TimeSpan>();
            for (int i = 0; i < 6000; i++)
            {
                int before = t.Waits.Count;
                await p.WaitTurnAsync(CancellationToken.None);
                // Every caller asks at the same instant (a burst of fast, cached objects); its start is now + its wait.
                starts.Add(t.Now + (t.Waits.Count > before ? t.Waits[t.Waits.Count - 1] : TimeSpan.Zero));
            }
            int maxInWindow = 0;
            for (int i = 0, j = 0; i < starts.Count; i++)
            {
                while (starts[i] - starts[j] >= TimeSpan.FromSeconds(10)) j++;
                maxInWindow = Math.Max(maxInWindow, i - j + 1);
            }
            Assert.True(maxInWindow <= 1500, "max requests in 10 s: " + maxInWindow);
        }

        [Fact]
        public async Task A_slow_request_stream_never_waits()
        {
            var t = new FakeTime();
            var p = t.Pacer(150);
            for (int i = 0; i < 100; i++)
            {
                await p.WaitTurnAsync(CancellationToken.None);
                t.Now += TimeSpan.FromMilliseconds(170); // one cold object per connection, ~170 ms each
            }
            Assert.Empty(t.Waits);
        }

        [Fact]
        public async Task A_slow_down_pauses_every_connection_then_they_leave_one_by_one()
        {
            var t = new FakeTime();
            var p = t.Pacer(150);
            Assert.True(p.HoldOff(TimeSpan.FromSeconds(10)));
            Assert.False(p.HoldOff(TimeSpan.FromSeconds(5)));   // a shorter pause from another connection changes nothing
            for (int i = 0; i < 24; i++) await p.WaitTurnAsync(CancellationToken.None);
            Assert.Equal(24, t.Waits.Count);
            Assert.All(t.Waits, w => Assert.True(w >= TimeSpan.FromSeconds(10)));
            var sorted = t.Waits.OrderBy(w => w).ToList();
            for (int i = 1; i < sorted.Count; i++) Assert.True(sorted[i] - sorted[i - 1] >= TimeSpan.FromTicks(66_000)); // 6.6 ms (FromMilliseconds rounds to 7 ms on .NET Framework)
            Assert.True(p.HoldOff(TimeSpan.FromSeconds(12)));   // a longer pause extends it
        }

        [Fact]
        public void Rate_must_be_positive()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RequestPacer(0));
        }

        [Fact]
        public void Slow_down_wait_has_a_floor_a_default_and_a_ceiling()
        {
            var r = new RetryPolicy();
            Assert.Equal(TimeSpan.FromSeconds(2), r.RateLimitWait(TimeSpan.Zero));
            Assert.Equal(TimeSpan.FromSeconds(10), r.RateLimitWait(null));
            Assert.Equal(TimeSpan.FromSeconds(9), r.RateLimitWait(TimeSpan.FromSeconds(9)));
            Assert.Equal(r.MaxRetryAfter, r.RateLimitWait(TimeSpan.FromHours(5)));
        }
    }
}
