using System;
using System.Collections.Generic;
using System.Linq;
using LotR.Releases.Json;

namespace LotR.Releases.Model
{
    public sealed class ManifestFormatException : FormatException
    {
        public ManifestFormatException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// Manifest &lt;-&gt; JSON. <see cref="ToCanonicalBytes"/> gives the exact bytes that are written to manifest.json and
    /// signed. Reading is tolerant of unknown members (kept in Extra) and strict on the types of known ones.
    /// </summary>
    public static class ManifestCodec
    {
        private static readonly string[] TopKeys = { "schema", "releaseId", "channel", "publishedAt", "publishedBy", "notes", "game", "modules", "endpoints" };
        private static readonly string[] ModuleKeys = { "id", "name", "version", "folder", "role", "dependsOn", "workshopItem", "files", "scenes" };

        public static byte[] ToCanonicalBytes(ReleaseManifest m) => ToJson(m).ToCanonicalBytes();

        public static ReleaseManifest Parse(byte[] utf8) => FromJson(JsonParser.Parse(utf8));

        public static JsonObject ToJson(ReleaseManifest m)
        {
            var o = new JsonObject();
            foreach (var kv in m.Extra ?? new JsonObject())
            {
                o[kv.Key] = kv.Value;
            }

            o["schema"] = m.Schema;
            o["releaseId"] = m.ReleaseId;
            o["channel"] = m.Channel;
            o["publishedAt"] = m.PublishedAt;
            o["publishedBy"] = m.PublishedBy;
            var notes = new JsonObject();
            foreach (var kv in m.Notes ?? new SortedDictionary<string, string>(StringComparer.Ordinal))
            {
                notes[kv.Key] = kv.Value;
            }

            o["notes"] = notes;
            o["game"] = new JsonObject { ["minVersion"] = m.Game?.MinVersion, ["maxVersion"] = m.Game?.MaxVersion };
            o["modules"] = new JsonArray(m.Modules.Select(ModuleToJson));
            // No "security" member: scan results live in the unsigned sidecar security.json (CONTRACTS §8, 15/10).
            if (m.Endpoints != null)
            {
                o["endpoints"] = new JsonObject
                {
                    ["base"] = m.Endpoints.Base,
                    ["files"] = m.Endpoints.Files,
                    ["queue"] = m.Endpoints.Queue,
                    ["support"] = m.Endpoints.Support,
                };
            }

            return o;
        }

        private static JsonNode ModuleToJson(ManifestModule mod)
        {
            var o = new JsonObject();
            foreach (var kv in mod.Extra ?? new JsonObject())
            {
                o[kv.Key] = kv.Value;
            }

            o["id"] = mod.Id;
            o["name"] = mod.Name;
            o["version"] = mod.Version;
            o["folder"] = mod.Folder;
            o["role"] = mod.Role;
            o["dependsOn"] = new JsonArray(mod.DependsOn.Select(d =>
            {
                var dj = new JsonObject { ["id"] = d.Id };
                if (d.Version != null)
                {
                    dj["version"] = d.Version;
                }

                if (d.Optional)
                {
                    dj["optional"] = true;
                }

                return (JsonNode)dj;
            }));
            o["workshopItem"] = mod.WorkshopItem;
            o["files"] = new JsonArray(mod.Files.Select(f =>
            {
                var fj = new JsonObject
                {
                    ["path"] = f.Path,
                    ["size"] = f.Size,
                    ["sha256"] = f.Sha256,
                    ["objectSize"] = f.ObjectSize,
                    ["exec"] = f.Exec,
                };
                if (f.Chunked)
                {
                    // A chunked entry has no whole-file object (CONTRACTS §1).
                    fj["chunked"] = true;
                    fj["chunkIndex"] = f.ChunkIndex;
                }
                else
                {
                    fj["object"] = f.Object;
                }

                return (JsonNode)fj;
            }));
            o["scenes"] = new JsonArray(mod.Scenes.Select(s => (JsonNode)new JsonObject
            {
                ["name"] = s.Name,
                ["uniqueToken"] = s.UniqueToken,
                ["revision"] = s.Revision,
            }));
            return o;
        }

        public static ReleaseManifest FromJson(JsonNode node)
        {
            var o = node as JsonObject ?? throw new ManifestFormatException("manifest: the document is not a JSON object");
            var m = new ReleaseManifest
            {
                Schema = (int)(Long(o, "schema", "manifest") ?? throw new ManifestFormatException("manifest.schema is missing")),
                ReleaseId = Str(o, "releaseId", "manifest"),
                Channel = Str(o, "channel", "manifest"),
                PublishedAt = Str(o, "publishedAt", "manifest"),
                PublishedBy = Str(o, "publishedBy", "manifest"),
            };

            if (m.Schema != ReleaseManifest.CurrentSchema)
            {
                // Refuse early: a newer schema may change meanings, not only add members.
                throw new ManifestFormatException("manifest.schema " + m.Schema + " is not supported (this build reads schema " + ReleaseManifest.CurrentSchema + ")");
            }

            var notes = Obj(o, "notes", "manifest");
            if (notes != null)
            {
                foreach (var kv in notes)
                {
                    m.Notes[kv.Key] = kv.Value.AsString() ?? throw new ManifestFormatException("manifest.notes." + kv.Key + " must be a string");
                }
            }

            var game = Obj(o, "game", "manifest");
            if (game != null)
            {
                m.Game.MinVersion = Str(game, "minVersion", "manifest.game");
                m.Game.MaxVersion = Str(game, "maxVersion", "manifest.game");
            }

            var ep = Obj(o, "endpoints", "manifest");
            if (ep != null)
            {
                m.Endpoints = new ReleaseEndpoints
                {
                    Base = Str(ep, "base", "manifest.endpoints"),
                    Files = Str(ep, "files", "manifest.endpoints"),
                    Queue = Str(ep, "queue", "manifest.endpoints"),
                    Support = Str(ep, "support", "manifest.endpoints"),
                };
            }

            var modules = o["modules"] as JsonArray ?? throw new ManifestFormatException("manifest.modules must be an array");
            int i = 0;
            foreach (var mn in modules)
            {
                m.Modules.Add(ModuleFromJson(mn, "manifest.modules[" + i++ + "]"));
            }

            foreach (var kv in o)
            {
                if (!TopKeys.Contains(kv.Key))
                {
                    m.Extra[kv.Key] = kv.Value;
                }
            }

            return m;
        }

        private static ManifestModule ModuleFromJson(JsonNode node, string where)
        {
            var o = node as JsonObject ?? throw new ManifestFormatException(where + " must be an object");
            var mod = new ManifestModule
            {
                Id = Str(o, "id", where),
                Name = Str(o, "name", where),
                Version = Str(o, "version", where),
                Folder = Str(o, "folder", where),
                Role = Str(o, "role", where),
                WorkshopItem = Str(o, "workshopItem", where),
            };

            if (o["dependsOn"] is JsonArray deps)
            {
                int d = 0;
                foreach (var dn in deps)
                {
                    string w = where + ".dependsOn[" + d++ + "]";
                    var dobj = dn as JsonObject ?? throw new ManifestFormatException(w + " must be an object");
                    mod.DependsOn.Add(new ModuleDependency
                    {
                        Id = Str(dobj, "id", w),
                        Version = Str(dobj, "version", w),
                        Optional = dobj["optional"]?.AsBool() ?? false,
                    });
                }
            }

            var files = o["files"] as JsonArray ?? throw new ManifestFormatException(where + ".files must be an array");
            int f = 0;
            foreach (var fn in files)
            {
                string w = where + ".files[" + f++ + "]";
                var fo = fn as JsonObject ?? throw new ManifestFormatException(w + " must be an object");
                mod.Files.Add(new ManifestFile
                {
                    Path = Str(fo, "path", w),
                    Size = Long(fo, "size", w) ?? throw new ManifestFormatException(w + ".size is missing"),
                    Sha256 = Str(fo, "sha256", w),
                    Object = Str(fo, "object", w),
                    ObjectSize = Long(fo, "objectSize", w) ?? throw new ManifestFormatException(w + ".objectSize is missing"),
                    Exec = fo["exec"]?.AsBool() ?? false,
                    Chunked = fo["chunked"]?.AsBool() ?? false,
                    ChunkIndex = Str(fo, "chunkIndex", w),
                });
            }

            if (o["scenes"] is JsonArray scenes)
            {
                int s = 0;
                foreach (var sn in scenes)
                {
                    string w = where + ".scenes[" + s++ + "]";
                    var so = sn as JsonObject ?? throw new ManifestFormatException(w + " must be an object");
                    mod.Scenes.Add(new SceneInfo { Name = Str(so, "name", w), UniqueToken = Str(so, "uniqueToken", w), Revision = Str(so, "revision", w) });
                }
            }

            foreach (var kv in o)
            {
                if (!ModuleKeys.Contains(kv.Key))
                {
                    mod.Extra[kv.Key] = kv.Value;
                }
            }

            return mod;
        }

        private static string Str(JsonObject o, string key, string where)
        {
            var v = o[key];
            if (v == null || v.IsNull)
            {
                return null;
            }

            return v.AsString() ?? throw new ManifestFormatException(where + "." + key + " must be a string");
        }

        private static long? Long(JsonObject o, string key, string where)
        {
            var v = o[key];
            if (v == null || v.IsNull)
            {
                return null;
            }

            return v.AsLong() ?? throw new ManifestFormatException(where + "." + key + " must be an integer");
        }

        private static JsonObject Obj(JsonObject o, string key, string where)
        {
            var v = o[key];
            if (v == null || v.IsNull)
            {
                return null;
            }

            return v.AsObject() ?? throw new ManifestFormatException(where + "." + key + " must be an object");
        }
    }
}
