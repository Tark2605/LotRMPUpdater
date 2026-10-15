using System;
using System.Collections.Generic;
using System.Linq;
using LotR.Releases.Json;

namespace LotR.Releases.Model
{
    /// <summary>Module roles of CONTRACTS §2.</summary>
    public static class ModuleRoles
    {
        public const string Event = "event";
        public const string Core = "core";
        public const string AdminClient = "admin-client";

        public static readonly IReadOnlyList<string> All = new[] { Event, Core, AdminClient };

        public static bool IsKnown(string role) => All.Contains(role, StringComparer.Ordinal);
    }

    /// <summary>manifest.json, schema 1 (CONTRACTS §2). Serialized by <see cref="ManifestCodec"/>.</summary>
    public sealed class ReleaseManifest
    {
        public const int CurrentSchema = 1;

        public int Schema { get; set; } = CurrentSchema;

        public string ReleaseId { get; set; }

        /// <summary>
        /// Signed channel name. REVIEW-AFTER(anti-rollback): the updater must check it equals the channel it asked
        /// channels.json for, and refuse a publishedAt older than the installed one unless the user picked a rollback.
        /// </summary>
        public string Channel { get; set; }

        /// <summary>UTC, ISO 8601 with seconds and 'Z' (yyyy-MM-ddTHH:mm:ssZ).</summary>
        public string PublishedAt { get; set; }

        public string PublishedBy { get; set; }

        /// <summary>Player-facing summary per language code ("en" required when present).</summary>
        public SortedDictionary<string, string> Notes { get; set; } = new SortedDictionary<string, string>(StringComparer.Ordinal);

        public GameRange Game { get; set; } = new GameRange();

        public List<ManifestModule> Modules { get; set; } = new List<ManifestModule>();

        public ReleaseEndpoints Endpoints { get; set; }

        /// <summary>Unknown top-level members, kept so a newer manifest round-trips through an older tool.</summary>
        public JsonObject Extra { get; set; } = new JsonObject();

        public ManifestModule FindModule(string id) => Modules.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));

        public IEnumerable<ManifestFile> AllFiles => Modules.SelectMany(m => m.Files);

        public long TotalSize => Modules.Sum(m => m.TotalSize);
    }

    public sealed class GameRange
    {
        public string MinVersion { get; set; }

        public string MaxVersion { get; set; }
    }

    public sealed class ReleaseEndpoints
    {
        public string Base { get; set; }

        public string Files { get; set; } = "/files/releases/";

        public string Queue { get; set; } = "/api/queue";

        public string Support { get; set; } = "/api/support";
    }

    public sealed class ManifestModule
    {
        /// <summary>SubModule.xml Id (what the game uses to match client and server).</summary>
        public string Id { get; set; }

        /// <summary>SubModule.xml Name.</summary>
        public string Name { get; set; }

        /// <summary>SubModule.xml Version, verbatim (trimmed).</summary>
        public string Version { get; set; }

        /// <summary>Folder name under Modules\ (the only place the updater writes for this module).</summary>
        public string Folder { get; set; }

        public string Role { get; set; }

        public List<ModuleDependency> DependsOn { get; set; } = new List<ModuleDependency>();

        public string WorkshopItem { get; set; }

        public List<ManifestFile> Files { get; set; } = new List<ManifestFile>();

        public List<SceneInfo> Scenes { get; set; } = new List<SceneInfo>();

        public JsonObject Extra { get; set; } = new JsonObject();

        public long TotalSize => Files.Sum(f => f.Size);

        public ManifestFile FindFile(string path) => Files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    public sealed class ModuleDependency
    {
        public string Id { get; set; }

        /// <summary>DependentVersion from SubModule.xml, null when the module does not pin one.</summary>
        public string Version { get; set; }

        public bool Optional { get; set; }
    }

    public sealed class ManifestFile
    {
        /// <summary>Relative to the module folder, '/' separators.</summary>
        public string Path { get; set; }

        /// <summary>Uncompressed size in bytes.</summary>
        public long Size { get; set; }

        /// <summary>Lowercase hex SHA-256 of the uncompressed file. Also the object identity.</summary>
        public string Sha256 { get; set; }

        /// <summary>
        /// Whole-file object name "&lt;sha256&gt;.&lt;ext&gt;" (ext "zst" or "raw"). Null for a chunked file: its bytes
        /// come from the chunks listed in <see cref="ChunkIndex"/>.
        /// </summary>
        public string Object { get; set; }

        /// <summary>
        /// Bytes to download for a fresh install of this file: the stored size of <see cref="Object"/>, or for a
        /// chunked file the sum of the stored sizes of its DISTINCT chunks (the index object itself not included).
        /// </summary>
        public long ObjectSize { get; set; }

        /// <summary>True for .dll/.exe (scanned, flagged in the UI).</summary>
        public bool Exec { get; set; }

        /// <summary>True for files &gt;= 16 MiB split by FastCDC (CONTRACTS §1).</summary>
        public bool Chunked { get; set; }

        /// <summary>Chunk index object name "&lt;sha256 of the index bytes&gt;.&lt;ext&gt;" when <see cref="Chunked"/>.</summary>
        public string ChunkIndex { get; set; }

        /// <summary>The object name that the manifest points at directly (the whole file or the chunk index).</summary>
        public string PrimaryObject => Chunked ? ChunkIndex : Object;

        public string ObjectExtension => ObjectNames.Extension(PrimaryObject);
    }

    public static class ObjectNames
    {
        public static string Make(string sha256, string ext) => sha256 + "." + ext;

        public static string Extension(string objectName)
        {
            int dot = objectName?.LastIndexOf('.') ?? -1;
            return dot < 0 ? null : objectName.Substring(dot + 1);
        }

        public static string Sha(string objectName)
        {
            int dot = objectName?.LastIndexOf('.') ?? -1;
            return dot < 0 ? objectName : objectName.Substring(0, dot);
        }

        /// <summary>Path of an object inside a release folder: "objects/ab/abcd....zst" ('/' separators, also the URL suffix).</summary>
        public static string RelativePath(string objectName) => "objects/" + objectName.Substring(0, 2) + "/" + objectName;
    }

    public sealed class SceneInfo
    {
        public string Name { get; set; }

        public string UniqueToken { get; set; }

        public string Revision { get; set; }
    }
}
