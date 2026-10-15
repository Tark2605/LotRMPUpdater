using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LotR.Releases.Model;

namespace LotR.Releases.Modules
{
    public sealed class ScannedFile
    {
        public string FullPath { get; set; }

        /// <summary>Manifest path ('/' separators, relative to the module folder).</summary>
        public string Path { get; set; }

        public long Size { get; set; }
    }

    /// <summary>
    /// Lists the files of a module folder in a stable order (ordinal by manifest path), skipping OS/VCS litter.
    /// Refuses links/junctions (a module folder must be plain files) and unsafe names.
    /// </summary>
    public static class ModuleScanner
    {
        /// <summary>Default exclusions (glob on the manifest path, case-insensitive; '*' does not cross '/').</summary>
        public static readonly IReadOnlyList<string> DefaultExcludes = new[]
        {
            ".git/**", "**/.git/**", ".gitattributes", ".gitignore", "**/Thumbs.db", "Thumbs.db", "**/desktop.ini", "desktop.ini",
            "**/.DS_Store", ".DS_Store", "**/*.tmp", "*.tmp", "**/~$*", "~$*",
        };

        public static List<ScannedFile> Scan(string moduleFolder, IEnumerable<string> extraExcludes = null, List<string> warnings = null)
        {
            if (!Directory.Exists(moduleFolder))
            {
                throw new DirectoryNotFoundException("Module folder not found: " + moduleFolder);
            }

            var excludes = DefaultExcludes.Concat(extraExcludes ?? Enumerable.Empty<string>()).Select(GlobToRegex).ToList();
            var result = new List<ScannedFile>();
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(moduleFolder));
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                foreach (var entry in dir.EnumerateFileSystemInfos())
                {
                    string rel = RelativePath.FromFileSystem(moduleFolder, entry.FullName);
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidDataException("'" + rel + "' is a link or junction; a module folder must contain plain files only");
                    }

                    bool isDir = (entry.Attributes & FileAttributes.Directory) != 0;
                    if (excludes.Any(x => x.IsMatch(rel) || (isDir && x.IsMatch(rel + "/"))))
                    {
                        warnings?.Add("excluded: " + rel + (isDir ? "/" : string.Empty));
                        continue;
                    }

                    if (isDir)
                    {
                        stack.Push((DirectoryInfo)entry);
                        continue;
                    }

                    if (!RelativePath.IsSafe(rel, out string why))
                    {
                        throw new InvalidDataException("'" + rel + "' cannot be published: " + why);
                    }

                    result.Add(new ScannedFile { FullPath = entry.FullName, Path = rel, Size = ((FileInfo)entry).Length });
                }
            }

            result.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in result)
            {
                if (!seen.Add(f.Path))
                {
                    throw new InvalidDataException("Two files differ only by case: " + f.Path);
                }
            }

            return result;
        }

        internal static Regex GlobToRegex(string glob)
        {
            var sb = new System.Text.StringBuilder("^");
            for (int i = 0; i < glob.Length; i++)
            {
                char c = glob[i];
                if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    // "**/" matches zero or more directories; "**" at the end matches everything.
                    if (i + 2 < glob.Length && glob[i + 2] == '/')
                    {
                        sb.Append("(?:.*/)?");
                        i += 2;
                    }
                    else
                    {
                        sb.Append(".*");
                        i++;
                    }
                }
                else if (c == '*')
                {
                    sb.Append("[^/]*");
                }
                else if (c == '?')
                {
                    sb.Append("[^/]");
                }
                else
                {
                    sb.Append(Regex.Escape(c.ToString()));
                }
            }

            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }
}
