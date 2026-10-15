using System;
using System.Linq;
using System.Text;
using LotR.Releases.Json;
using LotR.Releases.Model;
using Xunit;

namespace LotR.Releases.Tests
{
    public class JsonTests
    {
        [Fact]
        public void Canonical_sorts_keys_ordinally_and_has_no_whitespace()
        {
            var o = JsonParser.Parse("{ \"b\": 1, \"a\": [true, null, \"x\"], \"B\": {\"z\":1,\"y\":2}, \"é\": -5 }");
            Assert.Equal("{\"B\":{\"y\":2,\"z\":1},\"a\":[true,null,\"x\"],\"b\":1,\"é\":-5}", o.ToCanonicalString());
        }

        [Fact]
        public void Canonical_escapes_only_what_json_requires_and_keeps_utf8()
        {
            var s = new JsonString("q\"b\\s/ \u0001 \t é 中");
            Assert.Equal("\"q\\\"b\\\\s/ \\u0001 \\t é 中\"", s.ToCanonicalString());
            byte[] bytes = s.ToCanonicalBytes();
            Assert.Equal(Encoding.UTF8.GetBytes("\"q\\\"b\\\\s/ \\u0001 \\t é 中\""), bytes);
        }

        [Fact]
        public void Canonical_form_is_a_fixed_point()
        {
            string text = "{\"z\":[1,2,{\"b\":\"\\u00e9\",\"a\":9007199254740993}],\"a\":\"\\n\"}";
            string once = JsonParser.Parse(text).ToCanonicalString();
            Assert.Equal(once, JsonParser.Parse(once).ToCanonicalString());
            Assert.Contains("9007199254740993", once); // integers are exact (no double rounding)
        }

        [Theory]
        [InlineData("{\"a\":1,\"a\":2}")]
        [InlineData("{\"a\":1,}")]
        [InlineData("[1,]")]
        [InlineData("{'a':1}")]
        [InlineData("// c\n{}")]
        [InlineData("{\"a\":01}")]
        [InlineData("{\"a\":1} x")]
        [InlineData("\"\u0001\"")]
        [InlineData("{\"a\":tru}")]
        public void Parser_is_strict(string text)
        {
            Assert.ThrowsAny<FormatException>(() => JsonParser.Parse(text));
        }

        [Fact]
        public void Parser_accepts_bom_only_from_bytes()
        {
            var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("{\"a\":1}")).ToArray();
            Assert.Equal(1, JsonParser.Parse(bytes).AsObject()["a"].AsLong());
            Assert.ThrowsAny<FormatException>(() => JsonParser.Parse(new byte[] { 0xC3, 0x28 }));
        }

        [Fact]
        public void Pretty_output_parses_back_to_the_same_canonical_bytes()
        {
            var m = ManifestCodec.ToJson(TestData.SmallManifest());
            Assert.Equal(m.ToCanonicalString(), JsonParser.Parse(m.ToPrettyString()).ToCanonicalString());
        }
    }

    public class ManifestTests
    {
        // Pinned canonical bytes: if this changes, every signature produced so far changes meaning. Update only with
        // a schema bump.
        private const string GoldenSmall =
            "{\"channel\":\"stable\",\"endpoints\":{\"base\":\"https://example.invalid\",\"files\":\"/files/releases/\",\"queue\":\"/api/queue\",\"support\":\"/api/support\"}," +
            "\"game\":{\"maxVersion\":null,\"minVersion\":\"v1.2.12\"},\"modules\":[{\"dependsOn\":[{\"id\":\"LotRMPCore\"},{\"id\":\"Native\",\"optional\":true,\"version\":\"v1.2.12\"}]," +
            "\"files\":[{\"exec\":false,\"object\":\"" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" + ".zst\",\"objectSize\":8,\"path\":\"SubModule.xml\",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"size\":10}," +
            "{\"exec\":true,\"object\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.raw\",\"objectSize\":5,\"path\":\"bin/Win64_Shipping_Client/X.dll\",\"sha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"size\":5}," +
            "{\"chunkIndex\":\"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd.zst\",\"chunked\":true,\"exec\":false,\"objectSize\":19000000,\"path\":\"SceneObj/Big/terrain.bin\",\"sha256\":\"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc\",\"size\":20000000}]," +
            "\"folder\":\"LotRMPEvent\",\"id\":\"OrderMiddleEarthEvent\",\"name\":\"LotRMPEvent\",\"role\":\"event\",\"scenes\":[{\"name\":\"Big\",\"revision\":\"1\",\"uniqueToken\":\"{X}\"}],\"version\":\"v1.0.2.2\",\"workshopItem\":\"3774022783\"}]," +
            "\"notes\":{\"en\":\"Héllo \\\"quoted\\\"\\n\"},\"publishedAt\":\"2026-10-15T14:00:00Z\",\"publishedBy\":\"test\",\"releaseId\":\"20261015-140000-a1b2c3d4\",\"schema\":1}";

        [Fact]
        public void Canonical_bytes_are_pinned()
        {
            string actual = Encoding.UTF8.GetString(ManifestCodec.ToCanonicalBytes(TestData.SmallManifest()));
            Assert.Equal(GoldenSmall, actual);
        }

        [Fact]
        public void Round_trip_is_byte_identical_and_keeps_unknown_members()
        {
            var bytes = ManifestCodec.ToCanonicalBytes(TestData.SmallManifest());
            var obj = (JsonObject)JsonParser.Parse(bytes);
            obj["futureField"] = new JsonObject { ["x"] = 1 };
            ((JsonObject)obj["modules"].AsArray()[0])["futureModuleField"] = "y";
            var parsed = ManifestCodec.FromJson(obj);
            Assert.Equal(obj.ToCanonicalString(), ManifestCodec.ToJson(parsed).ToCanonicalString());
            Assert.True(ManifestValidator.Validate(parsed).IsValid, ManifestValidator.Validate(parsed).ToString());
        }

        [Fact]
        public void Other_schema_is_refused()
        {
            var o = ManifestCodec.ToJson(TestData.SmallManifest());
            o["schema"] = 2;
            Assert.Throws<ManifestFormatException>(() => ManifestCodec.FromJson(o));
        }

        [Fact]
        public void Wrong_types_are_refused()
        {
            var o = ManifestCodec.ToJson(TestData.SmallManifest());
            ((JsonObject)((JsonObject)o["modules"].AsArray()[0])["files"].AsArray()[0])["size"] = "10";
            Assert.Throws<ManifestFormatException>(() => ManifestCodec.FromJson(o));
        }

        [Theory]
        [InlineData("../evil.dll")]
        [InlineData("/abs.txt")]
        [InlineData("a\\b.txt")]
        [InlineData("C:/x.txt")]
        [InlineData("a//b")]
        [InlineData("a/./b")]
        [InlineData("file.txt:ads")]
        [InlineData("bin/CON.txt")]
        [InlineData("bin/nul")]
        [InlineData("trailing./x")]
        [InlineData("x/trailing ")]
        [InlineData("")]
        public void Unsafe_paths_are_errors(string path)
        {
            var m = TestData.SmallManifest();
            m.Modules[0].Files[1].Path = path;
            Assert.False(ManifestValidator.Validate(m).IsValid);
        }

        [Fact]
        public void Duplicate_paths_differing_by_case_are_errors()
        {
            var m = TestData.SmallManifest();
            m.Modules[0].Files[1].Path = "submodule.XML";
            var r = ManifestValidator.Validate(m);
            Assert.Contains(r.Errors, e => e.Contains("listed twice"));
        }

        [Fact]
        public void Object_rules_are_enforced()
        {
            var m = TestData.SmallManifest();
            m.Modules[0].Files[0].Object = TestData.Sha('e') + ".zst"; // not the file's own sha
            m.Modules[0].Files[1].ObjectSize = 4; // raw must equal size
            m.Modules[0].Files[2].Object = TestData.Sha('c') + ".zst"; // chunked with whole object
            var r = ManifestValidator.Validate(m);
            Assert.Contains(r.Errors, e => e.Contains("object must be"));
            Assert.Contains(r.Errors, e => e.Contains("raw object size"));
            Assert.Contains(r.Errors, e => e.Contains("chunked file has no whole-file object"));
        }

        [Fact]
        public void Reserved_and_unknown_transports_are_refused()
        {
            var m = TestData.SmallManifest();
            m.Modules[0].Files[0].Object = TestData.Sha('a') + ".zpatch";
            Assert.Contains(ManifestValidator.Validate(m).Errors, e => e.Contains("not supported"));
        }

        [Fact]
        public void Module_level_rules()
        {
            var m = TestData.SmallManifest();
            m.Modules.Add(new ManifestModule { Id = "OrderMiddleEarthEvent", Version = "v1", Folder = "lotrmpevent", Role = "mystery", Files = { new ManifestFile { Path = "x.txt", Sha256 = TestData.Sha('f'), Object = TestData.Sha('f') + ".raw" } } });
            m.ReleaseId = "2026-10-15";
            m.Endpoints.Base = "http://example.com";
            var r = ManifestValidator.Validate(m);
            Assert.Contains(r.Errors, e => e.Contains("duplicate module id"));
            Assert.Contains(r.Errors, e => e.Contains("share folder"));
            Assert.Contains(r.Errors, e => e.Contains("role 'mystery'"));
            Assert.Contains(r.Errors, e => e.Contains("SubModule.xml is not listed"));
            Assert.Contains(r.Errors, e => e.Contains("releaseId"));
            Assert.Contains(r.Errors, e => e.Contains("https"));
        }

        [Fact]
        public void A_security_block_in_the_manifest_is_only_a_warning_and_never_written()
        {
            var o = ManifestCodec.ToJson(TestData.SmallManifest());
            o["security"] = new JsonObject { ["virustotal"] = new JsonArray() };
            var m = ManifestCodec.FromJson(o);
            var r = ManifestValidator.Validate(m);
            Assert.True(r.IsValid);
            Assert.Contains(r.Warnings, w => w.Contains("security"));
            Assert.DoesNotContain("security", Encoding.UTF8.GetString(ManifestCodec.ToCanonicalBytes(TestData.SmallManifest())));
        }

        [Fact]
        public void Channels_and_latest_json()
        {
            var c = ChannelIndex.Parse(Encoding.UTF8.GetBytes("{\"stable\":\"20261015-140000-a1b2c3d4\",\"beta\":null}"));
            Assert.Equal("20261015-140000-a1b2c3d4", c.Get("stable"));
            Assert.Null(c.Get("beta"));
            Assert.Equal("{\"stable\":\"20261015-140000-a1b2c3d4\"}", Encoding.UTF8.GetString(c.ToBytes()));
            Assert.Throws<ManifestFormatException>(() => ChannelIndex.Parse(Encoding.UTF8.GetBytes("{\"stable\":\"../x\"}")));

            var l = UpdaterLatest.Parse(Encoding.UTF8.GetBytes("{\"version\":\"0.1.0\",\"url\":\"/files/updater/LotRMPUpdater-0.1.0.exe\",\"sha256\":\"" + TestData.Sha('a') + "\",\"size\":123,\"releasedAt\":\"2026-10-15T14:00:00Z\",\"signed\":false}"));
            Assert.True(l.Validate().IsValid);
            l.Url = "http://evil";
            Assert.False(l.Validate().IsValid);
        }

        [Fact]
        public void Sha256_sum_file_format()
        {
            string line = Sha256SumFile.Format(TestData.Sha('A'), "LotRMPUpdater-0.1.0.exe");
            Assert.Equal(TestData.Sha('a') + "  LotRMPUpdater-0.1.0.exe\n", line);
            Assert.Equal((TestData.Sha('a'), "LotRMPUpdater-0.1.0.exe"), Sha256SumFile.Parse(line.Replace("\n", "\r\n")));
            Assert.Equal((TestData.Sha('a'), "x.exe"), Sha256SumFile.Parse(TestData.Sha('a') + " *x.exe"));
            Assert.Throws<FormatException>(() => Sha256SumFile.Parse("abc  x.exe"));
        }
    }
}
