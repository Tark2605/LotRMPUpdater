using System.Linq;
using LotR.GameDetection.Tests.Bench;
using Xunit;

namespace LotR.GameDetection.Tests
{
    /// <summary>The detector must leave the machine exactly as it found it.</summary>
    public class ReadOnlyTests
    {
        private static FakeMachine BusyMachine()
        {
            var m = new FakeMachine().InstallSteam();
            m.AddSteamLibrary(@"D:\SteamLibrary", game: true);
            m.InstallEpicGame(@"E:\Epic\MountAndBlade2");
            m.InstallGogGame(@"F:\GOG\Bannerlord");
            m.InstallXboxGame(@"G:\");
            m.InstallXboxWindowsApps();
            m.AddLnk(m.Desktop("Bannerlord.lnk"), @"Z:\gone\bin\Win64_Shipping_Client\Bannerlord.exe");
            m.AddUrl(m.Desktop("Bannerlord.url"), "steam://rungameid/261550");
            return m;
        }

        [Fact]
        public void Detect_changes_nothing_on_disk()
        {
            var m = BusyMachine();
            var before = m.Fs.Snapshot();
            var r = m.Detect(new DetectionOptions { DeepScan = true });
            Assert.Equal(5, r.Installations.Count);
            Assert.Equal(before, m.Fs.Snapshot());
        }

        [Fact]
        public void The_temp_file_probe_runs_only_in_Modules_folders()
        {
            var m = BusyMachine();
            var r = m.Detect();
            Assert.NotEmpty(m.Fs.ProbedDirectories);
            Assert.All(m.Fs.ProbedDirectories, d => Assert.EndsWith(@"\Modules", d));
            Assert.Equal(r.Installations.Select(i => i.ModulesPath).OrderBy(p => p), m.Fs.ProbedDirectories.OrderBy(p => p));
        }

        [Fact]
        public void AccessCheck_and_None_modes_write_nothing_at_all()
        {
            var m = BusyMachine();
            m.Detect(new DetectionOptions { WriteProbe = WriteProbeMode.AccessCheck });
            m.Detect(new DetectionOptions { WriteProbe = WriteProbeMode.None });
            Assert.Empty(m.Fs.ProbedDirectories);
        }
    }
}

