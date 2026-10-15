using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Lotr.Security;

/// <summary>Persisted quota counters for one API key (shared by every release scanned with that key).</summary>
public sealed class RateLimitState
{
    public List<DateTimeOffset> Recent { get; set; } = new List<DateTimeOffset>();
    public string Day { get; set; }
    public int DayCount { get; set; }
    public DateTimeOffset? NotBefore { get; set; }
    public bool DayExhausted { get; set; }
}

/// <summary>
/// Client-side throttle: at most <c>perMinute</c> requests in any sliding 60 s window and <c>perDay</c> per UTC day
/// (VirusTotal public API: 4/min, 500/day). The state is saved after every request so a crashed or interrupted run
/// never "forgets" what it spent. When the day is used up, <see cref="AcquireAsync"/> throws
/// <see cref="QuotaExhaustedException"/>; the next run on a later UTC day resumes.
/// </summary>
public sealed class RateLimiter
{
    private readonly string _name;
    private readonly int _perMinute;
    private readonly int _perDay;
    private readonly string _statePath;
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly RateLimitState _state;

    public RateLimiter(string name, int perMinute, int perDay, string statePath, IClock clock, ILog log)
    {
        if (perMinute < 1 || perDay < 1) throw new ArgumentOutOfRangeException(nameof(perMinute));
        _name = name; _perMinute = perMinute; _perDay = perDay; _statePath = statePath; _clock = clock; _log = log;
        _state = (statePath != null ? Json.Read<RateLimitState>(statePath) : null) ?? new RateLimitState();
    }

    public int UsedToday { get { Roll(); return _state.DayCount; } }
    public int PerDay => _perDay;
    public RateLimitState Snapshot => _state;

    private void Roll()
    {
        string today = _clock.UtcNow.UtcDateTime.ToString("yyyy-MM-dd");
        if (_state.Day != today)
        {
            _state.Day = today;
            _state.DayCount = 0;
            _state.DayExhausted = false;
        }
    }

    public async Task AcquireAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Roll();
            if (_state.DayExhausted || _state.DayCount >= _perDay)
                throw new QuotaExhaustedException(
                    $"{_name}: daily quota used ({_state.DayCount}/{_perDay} requests on {_state.Day} UTC). Run the same command again after 00:00 UTC; progress is saved.");

            DateTimeOffset now = _clock.UtcNow;
            if (_state.NotBefore.HasValue && _state.NotBefore.Value > now)
            {
                TimeSpan wait = _state.NotBefore.Value - now;
                _log.Info($"{_name}: backing off {wait.TotalSeconds:0} s");
                await _clock.DelayAsync(wait, ct).ConfigureAwait(false);
                continue;
            }

            _state.Recent = _state.Recent.Where(t => now - t < TimeSpan.FromMinutes(1)).OrderBy(t => t).ToList();
            if (_state.Recent.Count >= _perMinute)
            {
                TimeSpan wait = _state.Recent[0] + TimeSpan.FromMinutes(1) - now + TimeSpan.FromMilliseconds(250);
                _log.Info($"{_name}: {_perMinute} requests/min reached, waiting {wait.TotalSeconds:0} s");
                await _clock.DelayAsync(wait, ct).ConfigureAwait(false);
                continue;
            }

            _state.Recent.Add(now);
            _state.DayCount++;
            Save();
            return;
        }
    }

    /// <summary>The server said 429: do not send anything before <paramref name="delay"/> has elapsed.</summary>
    public void BackOff(TimeSpan delay)
    {
        _state.NotBefore = _clock.UtcNow + delay;
        Save();
    }

    /// <summary>The server says the daily quota is spent (even if our local count disagrees).</summary>
    public void MarkDayExhausted()
    {
        Roll();
        _state.DayExhausted = true;
        Save();
    }

    private void Save()
    {
        if (_statePath != null) Json.Write(_statePath, _state);
    }
}
