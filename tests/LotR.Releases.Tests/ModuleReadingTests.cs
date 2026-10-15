using System.IO;
using System.Linq;
using System.Text;
using LotR.Releases.Model;
using LotR.Releases.Modules;
using Xunit;

namespace LotR.Releases.Tests
{
    public class ModuleVersionTests
    {
        [Theory]
        [InlineData("v1.0.2.2", 'v', 1, 0, 2, 2)]
        [InlineData("e1.8.0", 'e', 1, 8, 0, 0)]
        [InlineData("v1.2.12.66233", 'v', 1, 2, 12, 66233)]
        [InlineData("1.0", '\0', 1, 0, 0, 0)]
        [InlineData(" v0.2.0 ", 'v', 0, 2, 0, 0)]
        [InlineData("V3", 'v', 3, 0, 0, 0)]
        [InlineData("d0.0.1", 'd', 0, 0, 1, 0)]
        public void Parses_odd_versions(string text, char type, int a, int b, int c, int d)
        {
            var v = ModuleVersion.TryParse(text);
            Assert.NotNull(v);
            Assert.Equal(type, v.Type);
            Assert.Equal(new[] { a, b, c, d }, new[] { v.Major, v.Minor, v.Revision, v.ChangeSet });
        }

        [Theory]
        [InlineData("")]
        [InlineData("latest")]
        [InlineData("v1.0.0-beta")]
        [InlineData("v1..2")]
        [InlineData("x1.0")]
        [InlineData(null)]
        public void Rejects_non_versions_without_throwing(string text) => Assert.Null(ModuleVersion.TryParse(text));

        [Fact]
        public void Compares_numerically()
        {
            Assert.True(ModuleVersion.TryParse("v1.0.10").CompareTo(ModuleVersion.TryParse("v1.0.9")) > 0);
            Assert.Equal(0, ModuleVersion.TryParse("e1.2").CompareTo(ModuleVersion.TryParse("v1.2.0.0")));
            Assert.True(ModuleVersion.TryParse("v1.2.12.1").SameRelease(ModuleVersion.TryParse("v1.2.12.99")));
        }
    }

    public class SubModuleReaderTests
    {
        private static SubModuleInfo Parse(string xml) => SubModuleReader.Parse(Encoding.UTF8.GetBytes(xml));

        [Fact]
        public void Reads_identity_and_dependencies()
        {
            var s = Parse(@"<?xml version=""1.0"" encoding=""utf-8""?>
<Module>
  <Name value=""LotRMPAdminClient""/>
  <Id value=""LotRMPAdminClient""/>
  <Version value=""v0.2.0""/>
  <ModuleCategory value=""MultiplayerOptional""/>
  <DependedModules>
    <DependedModule Id=""Native"" DependentVersion=""v1.2.12""/>
    <DependedModule Id=""Multiplayer"" Optional=""true""/>
    <DependedModule Id=""OrderMiddleEarthEvent""/>
  </DependedModules>
  <SubModules><SubModule><Name value=""Inner""/><Tags><Tag key=""Id"" value=""nope""/></Tags></SubModule></SubModules>
</Module>");
            Assert.Equal("LotRMPAdminClient", s.Id);
            Assert.Equal("v0.2.0", s.Version);
            Assert.Equal("MultiplayerOptional", s.ModuleCategory);
            Assert.Equal(new[] { "Native", "Multiplayer", "OrderMiddleEarthEvent" }, s.DependedModules.Select(d => d.Id));
            Assert.Equal("v1.2.12", s.DependedModules[0].Version);
            Assert.True(s.DependedModules[1].Optional);
            Assert.Null(s.DependedModules[2].Version);
        }

        [Fact]
        public void Tolerates_bom_leading_whitespace_odd_case_and_blse_metadata()
        {
            byte[] bom = { 0xEF, 0xBB, 0xBF };
            string xml = "\r\n  <?xml version=\"1.0\"?><module><id value=\" X \"/><NAME value=\"Y\"/><Version value=\" e1.8.0 \"/>" +
                         "<DependedModuleMetadatas><DependedModuleMetadata id=\"Bannerlord.Harmony\" order=\"LoadBeforeThis\" version=\"v2.2.2\"/>" +
                         "<DependedModuleMetadata id=\"After\" order=\"LoadAfterThis\"/></DependedModuleMetadatas></module>";
            var s = SubModuleReader.Parse(bom.Concat(Encoding.UTF8.GetBytes(xml)).ToArray());
            Assert.Equal("X", s.Id);
            Assert.Equal("e1.8.0", s.Version);
            Assert.Single(s.DependedModules);
            Assert.Equal("v2.2.2", s.DependedModules[0].Version);
        }

        [Fact]
        public void Odd_version_is_kept_verbatim_with_a_warning()
        {
            var s = Parse("<Module><Id value=\"A\"/><Version value=\"1.0.0-hotfix\"/></Module>");
            Assert.Equal("1.0.0-hotfix", s.Version);
            Assert.Contains(s.Warnings, w => w.Contains("not a Bannerlord version"));
            Assert.Equal("A", s.Name); // falls back to the Id
        }

        [Theory]
        [InlineData("<Module><Version value=\"v1\"/></Module>")]
        [InlineData("<Module><Id value=\"A\"/></Module>")]
        [InlineData("<NotModule><Id value=\"A\"/><Version value=\"v1\"/></NotModule>")]
        [InlineData("<Module><Id value=\"A\"/>")]
        [InlineData("<!DOCTYPE x [<!ENTITY e \"boom\">]><Module><Id value=\"&e;\"/><Version value=\"v1\"/></Module>")]
        public void Refuses_broken_files(string xml) => Assert.Throws<InvalidDataException>(() => Parse(xml));

        [Fact]
        public void Reads_the_real_event_module()
        {
            string mod = TestData.RealEventModule;
            if (mod == null)
            {
                return; // repo clone not present on this machine
            }

            var s = SubModuleReader.ReadFile(Path.Combine(mod, "SubModule.xml"));
            Assert.Equal("OrderMiddleEarthEvent", s.Id);
            Assert.Equal("LotRMPEvent", s.Name);
            Assert.NotNull(ModuleVersion.TryParse(s.Version));
            Assert.Contains(s.DependedModules, d => d.Id == "LotRMPCore");
        }
    }

    public class SceneReaderTests
    {
        [Fact]
        public void Reads_only_the_root_tag_of_a_large_scene()
        {
            using (var t = new TempDir())
            {
                string dir = t.Sub("Mod", "SceneObj", "Huge");
                Directory.CreateDirectory(dir);
                using (var w = new StreamWriter(Path.Combine(dir, "scene.xscene"), false, new UTF8Encoding(false)))
                {
                    w.Write("<?xml version=\"1.0\"?>\n<scene name=\"Other\" version=\"2\" unique_token=\"{88BD0B2B-ECEB-4B3F-8792-F952180D7230}\" revision=\"1791917392611\">\n");
                    var line = "<entity name=\"x\"><transform position=\"1.0, 2.0, 3.0\"/></entity>\n";
                    for (int i = 0; i < 200000; i++)
                    {
                        w.Write(line);
                    }

                    w.Write("<!-- never closed: proves the reader stops after the root start tag -->");
                }

                Directory.CreateDirectory(t.Sub("Mod", "SceneObj", "Empty"));
                var warnings = new System.Collections.Generic.List<string>();
                var scenes = SceneReader.ReadModule(t.Sub("Mod"), warnings);
                var s = Assert.Single(scenes);
                Assert.Equal("Huge", s.Name);
                Assert.Equal("{88BD0B2B-ECEB-4B3F-8792-F952180D7230}", s.UniqueToken);
                Assert.Equal("1791917392611", s.Revision);
                Assert.Contains(warnings, w => w.Contains("declares name 'Other'"));
                Assert.Contains(warnings, w => w.Contains("Empty has no scene.xscene"));
            }
        }

        [Fact]
        public void Reads_the_real_event_scenes()
        {
            string mod = TestData.RealEventModule;
            if (mod == null)
            {
                return;
            }

            var scenes = SceneReader.ReadModule(mod);
            Assert.True(scenes.Count >= 7);
            var forest = scenes.Single(s => s.Name == "ForestBattle");
            Assert.StartsWith("{", forest.UniqueToken);
            Assert.All(scenes, s => Assert.False(string.IsNullOrEmpty(s.Revision)));
        }
    }

    public class ModuleScannerTests
    {
        [Fact]
        public void Lists_in_ordinal_order_and_skips_litter()
        {
            using (var t = new TempDir())
            {
                string m = t.Sub("Mod");
                foreach (var p in new[] { "SubModule.xml", "b/Z.txt", "b/a.txt", "B2/x.txt", ".git/HEAD", "GUI/Thumbs.db", "x.tmp", "keep.txtx" })
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(m, p)));
                    File.WriteAllText(Path.Combine(m, p), p);
                }

                var files = ModuleScanner.Scan(m, new[] { "B2/**" }).Select(f => f.Path).ToArray();
                Assert.Equal(new[] { "SubModule.xml", "b/Z.txt", "b/a.txt", "keep.txtx" }, files);
            }
        }
    }
}
