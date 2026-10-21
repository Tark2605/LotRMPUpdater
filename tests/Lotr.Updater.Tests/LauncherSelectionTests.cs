using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Lotr.Updater.Core.Game;
using LotR.Releases.Model;
using Xunit;

namespace Lotr.Updater.Tests
{
    /// <summary>Copies of the launcher's own classes (TaleWorlds.MountAndBlade.Launcher.Library.UserDatas): the file we write must
    /// load through the same XmlSerializer the launcher uses.</summary>
    public class UserData
    {
        public GameType GameType { get; set; }
        public UserGameTypeData SingleplayerData { get; set; } = new UserGameTypeData();
        public UserGameTypeData MultiplayerData { get; set; } = new UserGameTypeData();
        public DLLCheckDataCollection DLLCheckData { get; set; } = new DLLCheckDataCollection();
    }

    public enum GameType { Singleplayer, Multiplayer }

    public class UserGameTypeData
    {
        public List<UserModData> ModDatas { get; set; } = new List<UserModData>();
    }

    public class UserModData
    {
        public string Id { get; set; }
        public string LastKnownVersion { get; set; }
        public bool IsSelected { get; set; }
    }

    public class DLLCheckDataCollection
    {
        public List<DLLCheckData> DLLData { get; set; } = new List<DLLCheckData>();
    }

    public class DLLCheckData
    {
        public string DLLName { get; set; }
        public string DLLVerifyInformation { get; set; }
        public uint LatestSizeInBytes { get; set; }
        public bool IsDangerous { get; set; }
    }

    public sealed class LauncherSelectionTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _file;
        private readonly FakeProcesses _procs = new FakeProcesses();
        private DateTime _clock = new DateTime(2026, 10, 21, 20, 0, 0);

        private static readonly List<LauncherModule> Mods = new List<LauncherModule>
        {
            new LauncherModule("LotRMPCore", "v1.4.7.5", "LotRMPCore"),
            new LauncherModule("OrderMiddleEarthEvent", "v1.0.2.3", "LotRMPEvent"),
        };

        public LauncherSelectionTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "lotr-launcherdata-tests", Guid.NewGuid().ToString("N").Substring(0, 10), "Mount and Blade II Bannerlord", "Configs");
            Directory.CreateDirectory(_dir);
            _file = Path.Combine(_dir, LauncherSelection.FileName);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(_dir)), true); } catch (IOException) { }
        }

        private LauncherSelection Sel() => new LauncherSelection(_file, _procs, () => _clock);

        private static string Entry(string id, bool sel, string ver = "v1.2.12") =>
            $"<UserModData><Id>{id}</Id><LastKnownVersion>{ver}</LastKnownVersion><IsSelected>{(sel ? "true" : "false")}</IsSelected></UserModData>";

        /// <summary>A file shaped like the launcher's own (XmlSerializer output).</summary>
        private void WriteLauncherFile(string mpEntries, string spEntries = null)
        {
            var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
                      "<UserData xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\r\n" +
                      "  <GameType>Singleplayer</GameType>\r\n" +
                      "  <SingleplayerData>\r\n    <ModDatas>" + (spEntries ?? Entry("Native", true) + Entry("SandBoxCore", true) + Entry("LotRMPCore", false)) + "</ModDatas>\r\n  </SingleplayerData>\r\n" +
                      "  <MultiplayerData>\r\n    <ModDatas>\r\n" + mpEntries + "\r\n    </ModDatas>\r\n  </MultiplayerData>\r\n" +
                      "  <DLLCheckData>\r\n    <DLLData>\r\n      <DLLCheckData>\r\n        <DLLName>OrderMiddleEarthEvent.Common.dll</DLLName>\r\n        <DLLVerifyInformation />\r\n        <LatestSizeInBytes>12345</LatestSizeInBytes>\r\n        <IsDangerous>true</IsDangerous>\r\n      </DLLCheckData>\r\n    </DLLData>\r\n  </DLLCheckData>\r\n" +
                      "</UserData>\r\n";
            File.WriteAllText(_file, xml, new UTF8Encoding(true));
        }

        private UserData Load()
        {
            using (var r = XmlReader.Create(_file)) return (UserData)new XmlSerializer(typeof(UserData)).Deserialize(r);
        }

        private static List<string> Ids(UserGameTypeData d) => d.ModDatas.Select(m => m.Id).ToList();

        [Fact]
        public void Fresh_file_is_created_with_our_modules_ticked_and_loads_with_the_launchers_serializer()
        {
            var r = Sel().Apply(Mods);
            Assert.Equal(LauncherSelectionOutcome.Changed, r.Outcome);
            Assert.True(r.CreatedFile);
            Assert.Null(r.BackupPath);
            var u = Load();
            Assert.Equal(GameType.Multiplayer, u.GameType);
            Assert.Equal(new[] { "LotRMPCore", "OrderMiddleEarthEvent" }, Ids(u.MultiplayerData));
            Assert.All(u.MultiplayerData.ModDatas, m => Assert.True(m.IsSelected));
            Assert.Equal("v1.0.2.3", u.MultiplayerData.ModDatas[1].LastKnownVersion);
            Assert.Empty(u.SingleplayerData.ModDatas);
            Assert.Equal(LauncherSelectionOutcome.AlreadyTicked, Sel().Inspect(Mods).Outcome);
        }

        [Fact]
        public void Missing_mods_are_added_after_the_official_ones_and_everything_else_is_kept()
        {
            WriteLauncherFile(Entry("Native", true) + Entry("Multiplayer", true) + Entry("SomeOtherMod", false, "v2.0"));
            var r = Sel().Apply(Mods);
            Assert.Equal(LauncherSelectionOutcome.Changed, r.Outcome);
            Assert.NotNull(r.BackupPath);
            Assert.True(File.Exists(r.BackupPath));
            var u = Load();
            Assert.Equal(new[] { "Native", "Multiplayer", "SomeOtherMod", "LotRMPCore", "OrderMiddleEarthEvent" }, Ids(u.MultiplayerData)); // appended at the end
            Assert.False(u.MultiplayerData.ModDatas.Single(m => m.Id == "SomeOtherMod").IsSelected);
            Assert.True(u.MultiplayerData.ModDatas.Single(m => m.Id == "Native").IsSelected);
            // Single player list, game type and DLL data untouched.
            Assert.Equal(GameType.Singleplayer, u.GameType);
            Assert.Equal(new[] { "Native", "SandBoxCore", "LotRMPCore" }, Ids(u.SingleplayerData));
            Assert.False(u.SingleplayerData.ModDatas[2].IsSelected);
            Assert.Equal(12345u, u.DLLCheckData.DLLData.Single().LatestSizeInBytes);
        }

        [Fact]
        public void Present_but_unticked_mods_are_ticked_in_place_keeping_their_version()
        {
            WriteLauncherFile(Entry("Native", true) + Entry("Multiplayer", true) + Entry("LotRMPCore", false, "v1.4.4.1") + Entry("OrderMiddleEarthEvent", false, "v1.0.1.7") + Entry("Other", true));
            Assert.Equal(LauncherSelectionOutcome.Refused, Sel().Inspect(Mods).Outcome);
            Assert.Equal(LauncherSelectionOutcome.Changed, Sel().Apply(Mods).Outcome);
            var u = Load();
            Assert.Equal(new[] { "Native", "Multiplayer", "LotRMPCore", "OrderMiddleEarthEvent", "Other" }, Ids(u.MultiplayerData));
            Assert.All(u.MultiplayerData.ModDatas, m => Assert.True(m.IsSelected));
            Assert.Equal("v1.4.4.1", u.MultiplayerData.ModDatas[2].LastKnownVersion);
        }

        [Fact]
        public void Wrong_order_is_fixed_core_before_event_after_official()
        {
            WriteLauncherFile(Entry("OrderMiddleEarthEvent", true) + Entry("Native", true) + Entry("LotRMPCore", true) + Entry("Multiplayer", true));
            Assert.Equal(LauncherSelectionOutcome.Changed, Sel().Apply(Mods).Outcome);
            Assert.Equal(new[] { "Native", "Multiplayer", "LotRMPCore", "OrderMiddleEarthEvent" }, Ids(Load().MultiplayerData));
        }

        [Fact]
        public void Already_ticked_in_order_writes_nothing()
        {
            WriteLauncherFile(Entry("Native", true) + Entry("Multiplayer", true) + Entry("LotRMPCore", true) + Entry("OrderMiddleEarthEvent", true));
            var before = File.ReadAllBytes(_file);
            var r = Sel().Apply(Mods);
            Assert.Equal(LauncherSelectionOutcome.AlreadyTicked, r.Outcome);
            Assert.Equal(before, File.ReadAllBytes(_file));
            Assert.Empty(Sel().Backups());
        }

        [Fact]
        public void Duplicates_of_our_ids_are_merged_and_legacy_ids_left_untouched()
        {
            WriteLauncherFile(Entry("Native", true) + Entry("Multiplayer", true) + Entry("LotrMpEvent", true, "v1.0.0") + Entry("LotRMPCore", false, "v1.4.4.1") +
                              Entry("LotRMPCore", true, "v1.4.7.5") + Entry("OrderMiddleEarthEvent", true) + Entry("OrderMiddleEarthEvent", false));
            var r = Sel().Apply(Mods);
            Assert.Equal(LauncherSelectionOutcome.Changed, r.Outcome);
            var u = Load();
            Assert.Equal(new[] { "Native", "Multiplayer", "LotrMpEvent", "LotRMPCore", "OrderMiddleEarthEvent" }, Ids(u.MultiplayerData));
            Assert.True(u.MultiplayerData.ModDatas.Single(m => m.Id == "LotrMpEvent").IsSelected); // not ours: kept as it was
            Assert.Equal("v1.4.4.1", u.MultiplayerData.ModDatas.Single(m => m.Id == "LotRMPCore").LastKnownVersion); // first entry wins (List.Find)
            Assert.Contains("LotrMpEvent", r.OtherLotREntries);
            Assert.Contains("older LotR module", r.Message);
        }

        [Theory]
        [InlineData("<UserData><MultiplayerData><ModDatas><UserModData><Id>Native</Id>")]           // truncated
        [InlineData("not xml at all")]
        [InlineData("<?xml version=\"1.0\"?><Something><MultiplayerData/></Something>")]           // other root
        [InlineData("<UserData><MultiplayerData><ModDatas><Weird/></ModDatas></MultiplayerData></UserData>")]
        [InlineData("<!DOCTYPE UserData [<!ENTITY x \"y\">]><UserData/>")]                       // DTD refused
        public void Malformed_or_unknown_file_is_left_unchanged_with_a_message(string content)
        {
            File.WriteAllText(_file, content);
            var before = File.ReadAllBytes(_file);
            var r = Sel().Apply(Mods);
            Assert.Equal(LauncherSelectionOutcome.Refused, r.Outcome);
            Assert.Contains("yourself", r.Message);
            Assert.Equal(before, File.ReadAllBytes(_file));
            Assert.Empty(Sel().Backups());
        }

        [Fact]
        public void Backup_then_undo_restores_the_exact_previous_file()
        {
            WriteLauncherFile(Entry("Native", true) + Entry("Multiplayer", true));
            var before = File.ReadAllBytes(_file);
            var r = Sel().Apply(Mods);
            Assert.Equal(before, File.ReadAllBytes(r.BackupPath));
            Assert.StartsWith(LauncherSelection.FileName + LauncherSelection.BackupMarker + "20261021-200000", Path.GetFileName(r.BackupPath));
            var u = Sel().Undo(r.Undo);
            Assert.Equal(LauncherSelectionOutcome.Changed, u.Outcome);
            Assert.Equal(before, File.ReadAllBytes(_file));
            Assert.True(File.Exists(r.BackupPath)); // the backup stays
            Assert.False(File.Exists(_file + ".lotr-tmp"));
        }

        [Fact]
        public void Undo_of_a_created_file_removes_it()
        {
            var r = Sel().Apply(Mods);
            Assert.True(r.CreatedFile);
            Assert.Equal(LauncherSelectionOutcome.Changed, Sel().Undo(r.Undo).Outcome);
            Assert.False(File.Exists(_file));
        }

        [Fact]
        public void Undo_is_refused_when_the_launcher_saved_since()
        {
            WriteLauncherFile(Entry("Native", true));
            var r = Sel().Apply(Mods);
            File.AppendAllText(_file, "\r\n<!-- the launcher rewrote it -->");
            var changed = File.ReadAllBytes(_file);
            var u = Sel().Undo(r.Undo);
            Assert.Equal(LauncherSelectionOutcome.Refused, u.Outcome);
            Assert.Contains(r.BackupPath, u.Message);
            Assert.Equal(changed, File.ReadAllBytes(_file));
        }

        [Fact]
        public void Launcher_or_game_running_is_refused_for_apply_and_undo()
        {
            WriteLauncherFile(Entry("Native", true));
            var before = File.ReadAllBytes(_file);
            _procs.Running.Add(new RunningProcessInfo { Name = "TaleWorlds.MountAndBlade.Launcher.exe", Pid = 1 });
            var r = Sel().Apply(Mods);
            Assert.Equal(LauncherSelectionOutcome.Refused, r.Outcome);
            Assert.Contains("Close the Bannerlord launcher", r.Message);
            Assert.Equal(before, File.ReadAllBytes(_file));

            _procs.Running.Clear();
            var ok = Sel().Apply(Mods);
            _procs.Running.Add(new RunningProcessInfo { Name = "Bannerlord.exe", Pid = 2 });
            Assert.Equal(LauncherSelectionOutcome.Refused, Sel().Undo(ok.Undo).Outcome);
        }

        [Fact]
        public void Only_the_last_backups_are_kept()
        {
            WriteLauncherFile(Entry("Native", true));
            for (int i = 0; i < LauncherSelection.BackupsKept + 3; i++)
            {
                _clock = _clock.AddMinutes(1);
                WriteLauncherFile(Entry("Native", true)); // the player unticked everything again
                Assert.Equal(LauncherSelectionOutcome.Changed, Sel().Apply(Mods).Outcome);
            }
            var b = Sel().Backups();
            Assert.Equal(LauncherSelection.BackupsKept, b.Count);
            Assert.Contains("20261021-200800", Path.GetFileName(b[0]));
        }

        [Fact]
        public void Empty_file_and_missing_multiplayer_section_are_handled()
        {
            File.WriteAllBytes(_file, new byte[0]);
            Assert.Equal(LauncherSelectionOutcome.Changed, Sel().Apply(Mods).Outcome);
            Assert.Equal(new[] { "LotRMPCore", "OrderMiddleEarthEvent" }, Ids(Load().MultiplayerData));

            File.WriteAllText(_file, "<?xml version=\"1.0\" encoding=\"utf-8\"?><UserData><GameType>Singleplayer</GameType><SingleplayerData><ModDatas /></SingleplayerData></UserData>");
            Assert.Equal(LauncherSelectionOutcome.Changed, Sel().Apply(Mods).Outcome);
            var u = Load();
            Assert.Equal(GameType.Singleplayer, u.GameType);
            Assert.Equal(new[] { "LotRMPCore", "OrderMiddleEarthEvent" }, Ids(u.MultiplayerData));
        }

        [Fact]
        public void Modules_for_a_release_put_core_first_and_skip_admin_tools()
        {
            var m = new ReleaseManifest();
            m.Modules.Add(new ManifestModule { Id = "OrderMiddleEarthEvent", Name = "LotRMPEvent", Version = "v1.0.2.3", Role = ModuleRoles.Event });
            m.Modules.Add(new ManifestModule { Id = "LotRMPAdminClient", Version = "v1", Role = ModuleRoles.AdminClient });
            m.Modules.Add(new ManifestModule { Id = "LotRMPCore", Name = "LotRMPCore", Version = "v1.4.7.5", Role = ModuleRoles.Core });
            var list = LauncherSelection.ModulesFor(m);
            Assert.Equal(new[] { "LotRMPCore", "OrderMiddleEarthEvent" }, list.Select(x => x.Id));
            Assert.Equal("LotRMPEvent", list[1].DisplayName);
        }

        [Fact]
        public void Default_path_is_documents_configs()
        {
            var p = LauncherSelection.DefaultPath();
            Assert.EndsWith(Path.Combine("Mount and Blade II Bannerlord", "Configs", "LauncherData.xml"), p);
            Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Personal), p);
        }
    }
}
