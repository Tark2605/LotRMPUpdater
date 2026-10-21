using System;
using System.Collections.Generic;
using System.IO;
using Lotr.Updater.Core.Model;
using Lotr.Updater.Core.SelfUpdate;
using Xunit;

namespace Lotr.Updater.Tests
{
    /// <summary>Start-up decisions of the exe (0.2.0): .NET gate, self-install, probation/rollback of a new updater,
    /// second start (ack / take-over / hung), software rendering.</summary>
    public class StartupPolicyTests
    {
        [Theory]
        [InlineData(null, NetFrameworkRequirement.Verdict.Unknown)]
        [InlineData(394802, NetFrameworkRequirement.Verdict.TooOld)] // 4.6.2
        [InlineData(461808, NetFrameworkRequirement.Verdict.TooOld)] // 4.7.2
        [InlineData(528039, NetFrameworkRequirement.Verdict.TooOld)]
        [InlineData(528040, NetFrameworkRequirement.Verdict.Ok)]     // 4.8 on Windows 10 1903+
        [InlineData(528449, NetFrameworkRequirement.Verdict.Ok)]
        [InlineData(533325, NetFrameworkRequirement.Verdict.Ok)]     // 4.8.1
        public void Net_framework_release_gate(int? release, NetFrameworkRequirement.Verdict expected) =>
            Assert.Equal(expected, NetFrameworkRequirement.Check(release));

        [Fact]
        public void Net_framework_release_names()
        {
            Assert.Equal("4.7.2", NetFrameworkRequirement.Describe(461814));
            Assert.Equal("4.6.2", NetFrameworkRequirement.Describe(394806));
            Assert.Equal("4.8", NetFrameworkRequirement.Describe(528372));
            Assert.Equal("4.8.1", NetFrameworkRequirement.Describe(533320));
            Assert.Equal("unknown", NetFrameworkRequirement.Describe(null));
        }

        [Theory]
        [InlineData(true, "0.2.0", true, "0.1.0", SelfInstallAction.RunHere)]            // already the installed copy
        [InlineData(false, "0.2.0", false, null, SelfInstallAction.CopyThenRelaunch)]    // first start from Downloads
        [InlineData(false, "0.2.0", true, "0.1.2", SelfInstallAction.CopyThenRelaunch)]  // newer download replaces the installed one
        [InlineData(false, "0.2.0", true, "0.2.0", SelfInstallAction.RelaunchInstalled)] // same version: just start it
        [InlineData(false, "0.2.0", true, "0.2.3", SelfInstallAction.RelaunchInstalled)] // an old download never downgrades
        [InlineData(false, "0.2.0", true, null, SelfInstallAction.CopyThenRelaunch)]     // unreadable installed exe: repaired
        public void Self_install_decision(bool installedCopy, string running, bool exists, string installed, SelfInstallAction expected) =>
            Assert.Equal(expected, SelfInstallPolicy.Decide(installedCopy, running, exists, installed));

        [Theory]
        [InlineData("0.2.0.0", "0.2.0")]
        [InlineData("0.2.1.3", "0.2.1.3")]
        [InlineData("0.2.0", "0.2.0")]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void File_version_normalised(string v, string expected) => Assert.Equal(expected, SelfInstallPolicy.NormalizeFileVersion(v));

        [Fact]
        public void Probation_round_trip_and_verdicts()
        {
            var home = Path.Combine(Path.GetTempPath(), "lotr-probation-" + Guid.NewGuid().ToString("N"));
            try
            {
                var p = new UpdaterPaths(home);
                Assert.Null(SelfUpdateProbation.Load(p));
                new SelfUpdateProbation { Version = "0.2.1", PreviousVersion = "0.2.0", Starts = 0, SwappedAtUtc = "x" }.Save(p);
                var pr = SelfUpdateProbation.Load(p);
                Assert.Equal("0.2.1", pr.Version);
                Assert.Equal("0.2.0", pr.PreviousVersion);

                Assert.Equal(SelfUpdateProbation.Verdict.None, SelfUpdateProbation.AtStart(pr, "0.2.0", true)); // another version
                Assert.Equal(SelfUpdateProbation.Verdict.OnProbation, SelfUpdateProbation.AtStart(pr, "0.2.1", true));
                pr.Starts = 1;
                Assert.Equal(SelfUpdateProbation.Verdict.OnProbation, SelfUpdateProbation.AtStart(pr, "0.2.1", true));
                pr.Starts = SelfUpdateProbation.MaxStarts; // two starts already and still not healthy
                Assert.Equal(SelfUpdateProbation.Verdict.RollBack, SelfUpdateProbation.AtStart(pr, "0.2.1", true));
                Assert.Equal(SelfUpdateProbation.Verdict.None, SelfUpdateProbation.AtStart(pr, "0.2.1", false)); // nothing to go back to
                Assert.Equal(SelfUpdateProbation.Verdict.None, SelfUpdateProbation.AtStart(null, "0.2.1", true));

                SelfUpdateProbation.Clear(p);
                Assert.Null(SelfUpdateProbation.Load(p));
                File.WriteAllText(SelfUpdateProbation.FileOf(p), "{ damaged");
                Assert.Null(SelfUpdateProbation.Load(p)); // a damaged file never blocks a start
            }
            finally { try { Directory.Delete(home, true); } catch (Exception) { } }
        }

        private sealed class FakeClock
        {
            public TimeSpan Now = TimeSpan.Zero;
            public TimeSpan Elapsed() => Now;
            public void Sleep(TimeSpan d) => Now += d;
        }

        [Fact]
        public void Health_wait_healthy_when_the_marker_goes()
        {
            var c = new FakeClock();
            bool ok = HealthWait.WaitHealthy(() => c.Now < TimeSpan.FromSeconds(3), () => false, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(20), c.Elapsed, c.Sleep);
            Assert.True(ok);
            Assert.True(c.Now < TimeSpan.FromSeconds(4));
        }

        [Fact]
        public void Health_wait_times_out_when_the_new_exe_hangs()
        {
            var c = new FakeClock();
            Assert.False(HealthWait.WaitHealthy(() => true, () => false, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(20), c.Elapsed, c.Sleep));
            Assert.True(c.Now >= TimeSpan.FromSeconds(90) && c.Now < TimeSpan.FromSeconds(91));
        }

        [Fact]
        public void Health_wait_gives_up_after_the_grace_when_the_new_exe_dies()
        {
            var c = new FakeClock();
            Assert.False(HealthWait.WaitHealthy(() => true, () => c.Now >= TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(20), c.Elapsed, c.Sleep));
            Assert.True(c.Now >= TimeSpan.FromSeconds(22) && c.Now < TimeSpan.FromSeconds(23));
        }

        [Fact]
        public void Health_wait_allows_one_restart_in_software_rendering()
        {
            // The first process exits at 9 s (render watchdog), its restart reports healthy at 15 s: no rollback.
            var c = new FakeClock();
            Assert.True(HealthWait.WaitHealthy(() => c.Now < TimeSpan.FromSeconds(15), () => c.Now >= TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(20), c.Elapsed, c.Sleep));
        }

        /// <summary>Scripted running copy.</summary>
        private sealed class FakeChannel : IInstanceChannel
        {
            public PeerReply Reply = PeerReply.None;
            public bool Releases;
            public List<string> Calls = new List<string>();
            public void RequestShow() => Calls.Add("show");
            public void RequestExit() => Calls.Add("exit");
            public PeerReply WaitReply(TimeSpan timeout) { Calls.Add("wait " + timeout.TotalSeconds); return Reply; }
            public bool WaitOwnership(TimeSpan timeout) { Calls.Add("own " + timeout.TotalSeconds); return Releases; }
        }

        [Fact]
        public void Second_start_same_version_shows_the_running_copy()
        {
            var c = new FakeChannel { Reply = PeerReply.Ack };
            Assert.Equal(SecondStartOutcome.PeerShown, SecondStartPolicy.Run(c, false, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)));
            Assert.Equal(new[] { "show", "wait 5" }, c.Calls);
        }

        [Fact]
        public void Second_start_without_answer_means_hung()
        {
            var c = new FakeChannel { Reply = PeerReply.None };
            Assert.Equal(SecondStartOutcome.NoAnswer, SecondStartPolicy.Run(c, false, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)));
            c = new FakeChannel { Reply = PeerReply.None };
            Assert.Equal(SecondStartOutcome.NoAnswer, SecondStartPolicy.Run(c, true, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)));
            Assert.DoesNotContain("own 30", c.Calls);
        }

        [Fact]
        public void Newer_start_takes_over_when_the_old_one_says_bye()
        {
            var c = new FakeChannel { Reply = PeerReply.Bye, Releases = true };
            Assert.Equal(SecondStartOutcome.TookOver, SecondStartPolicy.Run(c, true, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)));
            Assert.Equal(new[] { "exit", "wait 5", "own 30" }, c.Calls);
            c = new FakeChannel { Reply = PeerReply.Bye, Releases = false };
            Assert.Equal(SecondStartOutcome.NoAnswer, SecondStartPolicy.Run(c, true, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)));
        }

        [Fact]
        public void Busy_old_copy_stays_and_shows_itself()
        {
            var c = new FakeChannel { Reply = PeerReply.Ack };
            Assert.Equal(SecondStartOutcome.PeerShown, SecondStartPolicy.Run(c, true, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)));
        }

        [Theory]
        [InlineData("0.2.1", "0.2.0", true)]
        [InlineData("0.2.0", "0.2.0", false)]
        [InlineData("0.2.0", "0.2.1", false)]
        [InlineData("0.2.0", null, true)] // no instance file: a 0.1.x copy
        public void Take_over_only_from_an_older_copy(string mine, string peer, bool expected) =>
            Assert.Equal(expected, SecondStartPolicy.ShouldTakeOver(mine, peer));

        [Fact]
        public void Render_mode_choices()
        {
            Assert.False(RenderModePolicy.Decide(false, null, false, false).Software);
            Assert.True(RenderModePolicy.Decide(true, null, false, false).Software);
            Assert.True(RenderModePolicy.Decide(false, "1", false, false).Software);
            Assert.False(RenderModePolicy.Decide(false, "0", false, false).Software);
            var saved = RenderModePolicy.Decide(false, null, true, false);
            Assert.True(saved.Software);
            Assert.False(saved.Persist);
            var neverRendered = RenderModePolicy.Decide(false, null, false, true);
            Assert.True(neverRendered.Software);
            Assert.True(neverRendered.Persist); // the next starts go straight to software rendering
            Assert.False(RenderModePolicy.Decide(true, null, false, true).Persist); // --safe-mode is a one-off, not saved
        }
    }
}
