using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using LotR.Releases.Transport;

namespace LotR.Releases.Model
{
    public sealed class ValidationResult
    {
        public List<string> Errors { get; } = new List<string>();

        public List<string> Warnings { get; } = new List<string>();

        public bool IsValid => Errors.Count == 0;

        public override string ToString() => string.Join(Environment.NewLine, Errors.Select(e => "error: " + e).Concat(Warnings.Select(w => "warning: " + w)));
    }

    /// <summary>
    /// Semantic checks on a manifest (after <see cref="ManifestCodec"/> succeeded). The updater must refuse a manifest
    /// with errors even when its signature is valid: path rules protect the player's disk from a mistaken publish.
    /// </summary>
    public static class ManifestValidator
    {
        public static readonly Regex ReleaseIdPattern = new Regex(@"^\d{8}-\d{6}-[0-9a-f]{8}$", RegexOptions.CultureInvariant);
        public static readonly Regex ChannelPattern = new Regex(@"^[a-z][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant);
        public static readonly Regex Sha256Pattern = new Regex(@"^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
        private static readonly Regex ExtPattern = new Regex(@"^[a-z0-9]{1,16}$", RegexOptions.CultureInvariant);
        private static readonly Regex WorkshopPattern = new Regex(@"^\d{1,20}$", RegexOptions.CultureInvariant);

        public static ValidationResult Validate(ReleaseManifest m)
        {
            var r = new ValidationResult();
            if (m.Schema != ReleaseManifest.CurrentSchema)
            {
                r.Errors.Add("schema must be " + ReleaseManifest.CurrentSchema);
            }

            if (m.ReleaseId == null || !ReleaseIdPattern.IsMatch(m.ReleaseId))
            {
                r.Errors.Add("releaseId '" + m.ReleaseId + "' does not match yyyyMMdd-HHmmss-<8 hex>");
            }

            if (m.Channel == null || !ChannelPattern.IsMatch(m.Channel))
            {
                r.Errors.Add("channel '" + m.Channel + "' is not a valid channel name (lowercase letters, digits, '-')");
            }

            if (m.PublishedAt == null || !DateTime.TryParseExact(m.PublishedAt, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out _))
            {
                r.Errors.Add("publishedAt '" + m.PublishedAt + "' must be UTC yyyy-MM-ddTHH:mm:ssZ");
            }

            if (string.IsNullOrWhiteSpace(m.PublishedBy))
            {
                r.Warnings.Add("publishedBy is empty");
            }

            if (m.Notes.Count > 0 && !m.Notes.ContainsKey("en"))
            {
                r.Warnings.Add("notes has no 'en' entry (the team language)");
            }

            if (m.Endpoints == null || string.IsNullOrWhiteSpace(m.Endpoints.Base))
            {
                r.Warnings.Add("endpoints.base is missing: the updater falls back to its built-in base URL");
            }
            else if (!Uri.TryCreate(m.Endpoints.Base, UriKind.Absolute, out var baseUri) || (baseUri.Scheme != "https" && !(baseUri.Scheme == "http" && baseUri.IsLoopback)))
            {
                r.Errors.Add("endpoints.base '" + m.Endpoints.Base + "' must be an absolute https URL (http allowed for loopback only)");
            }

            if (m.Extra.ContainsKey("security"))
            {
                r.Warnings.Add("manifest carries a 'security' block: ignored, scan results belong in the unsigned security.json sidecar (CONTRACTS §8)");
            }

            if (m.Modules.Count == 0)
            {
                r.Errors.Add("modules is empty");
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < m.Modules.Count; i++)
            {
                var mod = m.Modules[i];
                string w = "modules[" + i + "] (" + (mod.Id ?? "?") + ")";
                if (string.IsNullOrWhiteSpace(mod.Id))
                {
                    r.Errors.Add(w + ": id is missing");
                }
                else if (!ids.Add(mod.Id))
                {
                    r.Errors.Add(w + ": duplicate module id");
                }

                if (string.IsNullOrWhiteSpace(mod.Version))
                {
                    r.Errors.Add(w + ": version is missing");
                }
                else if (ModuleVersion.TryParse(mod.Version) == null)
                {
                    r.Warnings.Add(w + ": version '" + mod.Version + "' is not a Bannerlord version (kept verbatim)");
                }

                if (!RelativePath.IsSafeSegment(mod.Folder))
                {
                    r.Errors.Add(w + ": folder '" + mod.Folder + "' must be a single safe folder name");
                }
                else if (!folders.Add(mod.Folder))
                {
                    r.Errors.Add(w + ": two modules share folder '" + mod.Folder + "'");
                }

                if (!ModuleRoles.IsKnown(mod.Role))
                {
                    r.Errors.Add(w + ": role '" + mod.Role + "' must be one of " + string.Join(", ", ModuleRoles.All));
                }

                if (mod.WorkshopItem != null && !WorkshopPattern.IsMatch(mod.WorkshopItem))
                {
                    r.Errors.Add(w + ": workshopItem '" + mod.WorkshopItem + "' must be digits");
                }

                foreach (var d in mod.DependsOn)
                {
                    if (string.IsNullOrWhiteSpace(d.Id))
                    {
                        r.Errors.Add(w + ": a dependency has no id");
                    }
                }

                if (mod.Files.Count == 0)
                {
                    r.Errors.Add(w + ": no files");
                }

                if (mod.FindFile("SubModule.xml") == null)
                {
                    r.Errors.Add(w + ": SubModule.xml is not listed (the game would not see the module)");
                }

                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in mod.Files)
                {
                    string fw = w + " file '" + f.Path + "'";
                    if (!RelativePath.IsSafe(f.Path, out string why))
                    {
                        r.Errors.Add(fw + ": unsafe path (" + why + ")");
                    }
                    else if (!paths.Add(f.Path))
                    {
                        r.Errors.Add(fw + ": listed twice (paths are case-insensitive on Windows)");
                    }

                    if (f.Size < 0 || f.ObjectSize < 0)
                    {
                        r.Errors.Add(fw + ": negative size");
                    }

                    if (f.Sha256 == null || !Sha256Pattern.IsMatch(f.Sha256))
                    {
                        r.Errors.Add(fw + ": sha256 must be 64 lowercase hex characters");
                        continue;
                    }

                    string ext = f.ObjectExtension;
                    if (f.Chunked)
                    {
                        if (f.Object != null)
                        {
                            r.Errors.Add(fw + ": a chunked file has no whole-file object");
                        }

                        if (f.ChunkIndex == null || ext == null || !ExtPattern.IsMatch(ext) || !Sha256Pattern.IsMatch(ObjectNames.Sha(f.ChunkIndex)))
                        {
                            r.Errors.Add(fw + ": chunkIndex must be '<sha256 of the index>.<ext>'");
                        }
                    }
                    else
                    {
                        if (f.ChunkIndex != null)
                        {
                            r.Errors.Add(fw + ": chunkIndex without chunked: true");
                        }

                        if (f.Object == null || ext == null || !ExtPattern.IsMatch(ext) || !string.Equals(f.Object, f.Sha256 + "." + ext, StringComparison.Ordinal))
                        {
                            r.Errors.Add(fw + ": object must be '<sha256>.<ext>'");
                            continue;
                        }

                        if (ext == RawCodec.Ext && f.ObjectSize != f.Size)
                        {
                            r.Errors.Add(fw + ": raw object size differs from file size");
                        }
                    }

                    if (ext != null && TransportCodecs.Find(ext) == null)
                    {
                        r.Errors.Add(fw + ": transport '" + ext + "' is not supported by this build (known: " + string.Join(", ", TransportCodecs.Known) + ")");
                    }

                    bool shouldExec = RelativePath.IsExecutable(f.Path);
                    if (shouldExec != f.Exec)
                    {
                        r.Warnings.Add(fw + ": exec flag " + f.Exec + " but extension says " + shouldExec);
                    }
                }

                foreach (var s in mod.Scenes)
                {
                    if (string.IsNullOrWhiteSpace(s.Name) || string.IsNullOrWhiteSpace(s.UniqueToken) || string.IsNullOrWhiteSpace(s.Revision))
                    {
                        r.Warnings.Add(w + ": scene '" + s.Name + "' lacks name, uniqueToken or revision");
                    }
                }
            }

            return r;
        }
    }
}
