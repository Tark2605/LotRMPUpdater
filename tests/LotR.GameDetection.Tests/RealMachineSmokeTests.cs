using System;
using System.IO;
using System.Runtime.InteropServices;
using Xunit;
using Xunit.Abstractions;

namespace LotR.GameDetection.Tests
{
    /// <summary>Skipped unless running on Windows; set LOTR_SKIP_SMOKE=1 to skip it there too (CI, other PCs).</summary>
    public sealed class RealMachineFactAttribute : FactAttribute
    {
        public RealMachineFactAttribute()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Skip = "Real-machine smoke test: Windows only.";
            else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LOTR_SKIP_SMOKE")))
                Skip = "Real-machine smoke test skipped (LOTR_SKIP_SMOKE is set).";
        }
    }

    /// <summary>
    /// Runs the real detector on the PC executing the tests. Strictly read-only: AccessCheck write mode
    /// (nothing is created, not even the temp-file probe), no deep scan.
    /// </summary>
    public class RealMachineSmokeTests
    {
        private readonly ITestOutputHelper _out;

        public RealMachineSmokeTests(ITestOutputHelper output)
        {
            _out = output;
        }

        [RealMachineFact]
        public void Temp_file_probe_leaves_nothing_behind()
        {
            // in a fresh folder under %TEMP%, never in a game install
            var dir = Path.Combine(Path.GetTempPath(), "lotr-probe-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var fs = new Windows.WindowsFileSystem();
                Assert.True(fs.ProbeWriteWithTempFile(dir, out var detail), detail);
                _out.WriteLine(detail);
                Assert.Empty(Directory.GetFileSystemEntries(dir));
                Assert.Null(fs.ProbeWriteWithTempFile(Path.Combine(dir, "missing"), out _));
                Assert.True(fs.CheckWriteAccess(dir, out _));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [RealMachineFact]
        public void Detect_on_this_PC_read_only()
        {
            var report = GameDetector.CreateForThisPc().Detect(new DetectionOptions { WriteProbe = WriteProbeMode.AccessCheck });
            _out.WriteLine(ReportJson.Serialize(report, maskUserName: true, toolVersion: "smoke-test"));
            Assert.Empty(report.SourceErrors);

            // On the maintainer's PC the Steam copy lives on D:; elsewhere only "no crash" is asserted.
            const string maintainerCopy = @"D:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord";
            if (File.Exists(Path.Combine(maintainerCopy, @"bin\Win64_Shipping_Client\Bannerlord.exe")))
            {
                var i = Assert.Single(report.Installations, x => PathUtil.PathEquals(x.RootPath, maintainerCopy));
                Assert.Equal(GameStore.Steam, i.Store);
                Assert.False(string.IsNullOrEmpty(i.GameVersion));
            }
        }
    }
}
