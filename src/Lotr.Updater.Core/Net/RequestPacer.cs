using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Lotr.Updater.Core.Net
{
    /// <summary>
    /// One pace for every download connection: request starts are spaced so that, together, they never go over
    /// <see cref="RequestsPerSecond"/>, and a "slow down" answer (429) pauses ALL connections at once instead of each one
    /// running into the block on its own.
    /// </summary>
    /// <remarks>
    /// Why: the server limits how fast one address may ask for objects. Objects already in the CDN cache come back fast enough
    /// for 24 connections to be told to slow down. Objects not in the cache take ~140-190 ms each, so 24 connections stay near
    /// this pace on their own and it rarely waits.
    /// </remarks>
    public sealed class RequestPacer
    {
        /// <summary>Request starts per second for the whole updater: leaves room for a second player behind the same address.</summary>
        public const double DefaultRequestsPerSecond = 150;

        private readonly object _lock = new object();
        private readonly TimeSpan _interval;
        private TimeSpan _next;
        private TimeSpan _holdUntil;

        public RequestPacer(double requestsPerSecond)
        {
            if (!(requestsPerSecond > 0)) throw new ArgumentOutOfRangeException(nameof(requestsPerSecond));
            RequestsPerSecond = requestsPerSecond;
            _interval = TimeSpan.FromTicks((long)Math.Ceiling(TimeSpan.TicksPerSecond / requestsPerSecond));
        }

        public double RequestsPerSecond { get; }

        /// <summary>Monotonic clock (injectable for tests).</summary>
        public Func<TimeSpan> Now { get; set; } = DefaultClock;

        /// <summary>Wait (injectable for tests).</summary>
        public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (t, ct) => Task.Delay(t, ct);

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static TimeSpan DefaultClock() => Clock.Elapsed;

        /// <summary>Waits for this request's turn: after any pause asked by the server, and one interval after the previous
        /// request start. Each call reserves its own start time, so waiting callers leave one by one (no burst after a pause).</summary>
        public Task WaitTurnAsync(CancellationToken ct)
        {
            TimeSpan wait;
            lock (_lock)
            {
                var now = Now();
                var start = now;
                if (_next > start) start = _next;
                if (_holdUntil > start) start = _holdUntil;
                _next = start + _interval;
                wait = start - now;
            }
            return wait > TimeSpan.Zero ? Delay(wait, ct) : Task.CompletedTask;
        }

        /// <summary>The server asked to slow down: no connection starts a request before <paramref name="pause"/> has passed.
        /// Returns true when this call started or extended the pause (false: an equal or longer pause was already set).</summary>
        public bool HoldOff(TimeSpan pause)
        {
            lock (_lock)
            {
                var until = Now() + pause;
                if (until <= _holdUntil) return false;
                _holdUntil = until;
                return true;
            }
        }
    }
}
