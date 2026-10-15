using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LotR.GameDetection.Abstractions;

namespace LotR.GameDetection.Tests.Bench
{
    /// <summary>
    /// In-memory Windows-like file system: case-insensitive, case-preserving, with junctions/symlinks,
    /// 8.3 short-name aliases and per-folder write denial. No length limit, so &gt; 260 char paths work.
    /// </summary>
    public sealed class FakeFileSystem : IFileSystem
    {
        private sealed class Node
        {
            public string Name;
            public bool IsDir;
            public byte[] Data;
            public readonly Dictionary<string, Node> Children = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        }

        private readonly Dictionary<string, Node> _drives = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _links = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<string> _deniedWrite = new List<string>();
        private readonly List<string> _unreadableAcl = new List<string>();
        private readonly HashSet<string> _nonFixedDrives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public int ReadCount { get; private set; }

        // ---------- building ----------

        public FakeFileSystem AddDirectory(string path)
        {
            GetOrCreate(PathUtil.Normalize(path), dir: true);
            return this;
        }

        public FakeFileSystem AddFile(string path, string text) => AddFile(path, Encoding.UTF8.GetBytes(text ?? string.Empty));

        public FakeFileSystem AddFile(string path, byte[] data)
        {
            var n = PathUtil.Normalize(path);
            GetOrCreate(PathUtil.GetParent(n), dir: true);
            var node = GetOrCreate(n, dir: false);
            node.Data = data;
            return this;
        }

        /// <summary>Directory junction or symbolic link: listed in its parent, resolved to <paramref name="target"/>.</summary>
        public FakeFileSystem AddJunction(string linkPath, string target)
        {
            var link = PathUtil.Normalize(linkPath);
            GetOrCreate(link, dir: true);
            _links[PathUtil.Key(link)] = PathUtil.Normalize(target);
            return this;
        }

        /// <summary>8.3 alias such as C:\PROGRA~2 for C:\Program Files (x86): resolvable, never listed.</summary>
        public FakeFileSystem AddShortName(string shortPath, string longPath)
        {
            _links[PathUtil.Key(shortPath)] = PathUtil.Normalize(longPath);
            return this;
        }

        public FakeFileSystem Delete(string path)
        {
            var n = PathUtil.Normalize(path);
            var parent = Find(PathUtil.GetParent(n));
            parent?.Children.Remove(PathUtil.GetFileName(n));
            return this;
        }

        public FakeFileSystem Rename(string path, string newName)
        {
            var n = PathUtil.Normalize(path);
            var parent = Find(PathUtil.GetParent(n));
            if (parent != null && parent.Children.TryGetValue(PathUtil.GetFileName(n), out var node))
            {
                parent.Children.Remove(node.Name);
                node.Name = newName;
                parent.Children[newName] = node;
            }
            return this;
        }

        public FakeFileSystem DenyWrite(string path)
        {
            _deniedWrite.Add(Resolve(path));
            return this;
        }

        public FakeFileSystem MakeAclUnreadable(string path)
        {
            _unreadableAcl.Add(Resolve(path));
            return this;
        }

        public FakeFileSystem MarkDriveNotFixed(string root)
        {
            _nonFixedDrives.Add(PathUtil.Normalize(root));
            return this;
        }

        // ---------- IFileSystem ----------

        public bool FileExists(string path)
        {
            var n = Find(path);
            return n != null && !n.IsDir;
        }

        public bool DirectoryExists(string path)
        {
            var n = Find(path);
            return n != null && n.IsDir;
        }

        public IEnumerable<string> EnumerateDirectories(string path)
        {
            var n = Find(path);
            if (n == null || !n.IsDir)
                return Array.Empty<string>();
            var basePath = PathUtil.Normalize(path);
            return n.Children.Values.Where(c => c.IsDir).Select(c => PathUtil.Combine(basePath, c.Name)).ToList();
        }

        public IEnumerable<string> EnumerateFiles(string path, string searchPattern)
        {
            var n = Find(path);
            if (n == null || !n.IsDir)
                return Array.Empty<string>();
            var rx = new Regex("^" + Regex.Escape(searchPattern ?? "*").Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);
            var basePath = PathUtil.Normalize(path);
            return n.Children.Values.Where(c => !c.IsDir && rx.IsMatch(c.Name)).Select(c => PathUtil.Combine(basePath, c.Name)).ToList();
        }

        public string ReadAllText(string path)
        {
            var bytes = ReadAllBytes(path);
            var text = Encoding.UTF8.GetString(bytes);
            return text.TrimStart('\uFEFF');
        }

        public byte[] ReadAllBytes(string path)
        {
            ReadCount++;
            var n = Find(path);
            if (n == null || n.IsDir)
                throw new System.IO.FileNotFoundException("Could not find file '" + path + "'.");
            return n.Data ?? Array.Empty<byte>();
        }

        public string GetCanonicalPath(string path)
        {
            var resolved = Resolve(path);
            if (resolved == null)
                return null;
            // rebuild with the real case of every segment
            var parts = resolved.Split('\\');
            if (!_drives.TryGetValue(parts[0], out var node))
                return resolved;
            var sb = new StringBuilder(parts[0].ToUpperInvariant());
            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) continue;
                if (!node.Children.TryGetValue(parts[i], out var child))
                    return resolved;
                sb.Append('\\').Append(child.Name);
                node = child;
            }
            return PathUtil.Normalize(sb.ToString());
        }

        public bool? CheckWriteAccess(string directory, out string detail)
        {
            if (!DirectoryExists(directory))
            {
                detail = "folder does not exist";
                return null;
            }
            var resolved = Resolve(directory);
            if (_unreadableAcl.Any(a => PathUtil.IsUnder(resolved, a)))
            {
                detail = "Could not read the folder permissions (fake)";
                return null;
            }
            if (_deniedWrite.Any(d => PathUtil.IsUnder(resolved, d)))
            {
                detail = "fake ACL: write denied";
                return false;
            }
            detail = "fake ACL: write allowed";
            return true;
        }

        /// <summary>Folders the temp-file probe ran in (the probe file is created and removed, like FILE_FLAG_DELETE_ON_CLOSE).</summary>
        public List<string> ProbedDirectories { get; } = new List<string>();

        public bool? ProbeWriteWithTempFile(string directory, out string detail)
        {
            if (!DirectoryExists(directory))
            {
                detail = "The folder does not exist.";
                return null;
            }
            ProbedDirectories.Add(PathUtil.Normalize(directory));
            var resolved = Resolve(directory);
            if (_unreadableAcl.Any(a => PathUtil.IsUnder(resolved, a)) || _deniedWrite.Any(d => PathUtil.IsUnder(resolved, d)))
            {
                detail = "fake temp-file probe: access denied";
                return false;
            }
            // create then delete-on-close: the tree is unchanged afterwards
            var probe = PathUtil.Combine(directory, ".lotr-write-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
            AddFile(probe, Array.Empty<byte>());
            Delete(probe);
            detail = "fake temp-file probe: created and removed";
            return true;
        }

        /// <summary>Every path in the tree (directories end with '\'), for "nothing changed" assertions.</summary>
        public List<string> Snapshot()
        {
            var result = new List<string>();
            void Walk(Node n, string path)
            {
                foreach (var c in n.Children.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var p = path + "\\" + c.Name;
                    result.Add(c.IsDir ? p + "\\" : p + " (" + (c.Data?.Length ?? 0) + ")");
                    if (c.IsDir) Walk(c, p);
                }
            }
            foreach (var d in _drives.OrderBy(d => d.Key))
                Walk(d.Value, d.Value.Name);
            return result;
        }

        public IReadOnlyList<string> GetFixedDriveRoots() =>
            _drives.Keys.Select(d => PathUtil.Normalize(d)).Where(d => !_nonFixedDrives.Contains(d)).OrderBy(d => d).ToList();

        // ---------- internals ----------

        /// <summary>Applies junctions and short-name aliases, segment by segment.</summary>
        public string Resolve(string path)
        {
            var current = PathUtil.Normalize(path);
            if (current == null)
                return null;
            for (int guard = 0; guard < 40; guard++)
            {
                var parts = current.Split('\\');
                bool replaced = false;
                for (int i = 1; i <= parts.Length && !replaced; i++)
                {
                    var prefix = string.Join("\\", parts.Take(i));
                    if (_links.TryGetValue(PathUtil.Key(prefix), out var target))
                    {
                        var rest = string.Join("\\", parts.Skip(i));
                        current = rest.Length == 0 ? target : PathUtil.Combine(target, rest);
                        replaced = true;
                    }
                }
                if (!replaced)
                    return current;
            }
            return current;
        }

        private Node Find(string path)
        {
            var resolved = Resolve(path);
            if (resolved == null)
                return null;
            var parts = resolved.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (!_drives.TryGetValue(parts[0], out var node))
                return null;
            for (int i = 1; i < parts.Length; i++)
            {
                if (!node.IsDir || !node.Children.TryGetValue(parts[i], out node))
                    return null;
            }
            return node;
        }

        private Node GetOrCreate(string normalized, bool dir)
        {
            normalized = Resolve(normalized); // writing below a junction writes into its target
            var parts = normalized.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (!_drives.TryGetValue(parts[0], out var node))
            {
                node = new Node { Name = parts[0].ToUpperInvariant(), IsDir = true };
                _drives[parts[0]] = node;
            }
            for (int i = 1; i < parts.Length; i++)
            {
                if (!node.Children.TryGetValue(parts[i], out var child))
                {
                    child = new Node { Name = parts[i], IsDir = dir || i < parts.Length - 1 };
                    node.Children[parts[i]] = child;
                }
                node = child;
            }
            return node;
        }
    }
}
