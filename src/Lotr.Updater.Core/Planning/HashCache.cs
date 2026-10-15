using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Lotr.Updater.Core.IO;
using Lotr.Updater.Core.Json;
using Lotr.Updater.Core.Objects;

namespace Lotr.Updater.Core.Planning
{
    /// <summary>
    /// Remembers the SHA-256 of local files by (path, size, last write time) so that checking for an update does not re-hash
    /// 10 GB of Core every time. "Verify my install" bypasses it. Also remembers which chunk index describes a file hash, so
    /// unchanged chunks of a big file can be reused locally on the next update.
    /// </summary>
    public sealed class HashCache
    {
        private readonly string _file;
        private readonly IFileSystem _fs;
        private readonly Dictionary<string, string> _hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _indexes = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly object _gate = new object();

        public HashCache(string file, IFileSystem fs)
        {
            _file = file;
            _fs = fs;
            Load();
        }

        private void Load()
        {
            try
            {
                if (_file == null || !File.Exists(_file)) return;
                var o = Json.Json.ParseObject(File.ReadAllText(_file, Encoding.UTF8));
                var h = o.Obj("hashes");
                if (h != null) foreach (var kv in h) _hashes[kv.Key] = Convert.ToString(kv.Value);
                var i = o.Obj("chunkIndexes");
                if (i != null) foreach (var kv in i) _indexes[kv.Key] = Convert.ToString(kv.Value);
            }
            catch (Exception) { _hashes.Clear(); _indexes.Clear(); }
        }

        public void Save()
        {
            if (_file == null) return;
            JsonObject o;
            lock (_gate)
            {
                var h = new JsonObject();
                foreach (var kv in _hashes) h[kv.Key] = kv.Value;
                var i = new JsonObject();
                foreach (var kv in _indexes) i[kv.Key] = kv.Value;
                o = new JsonObject { ["hashes"] = h, ["chunkIndexes"] = i };
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file));
                File.WriteAllText(_file + ".tmp", Json.Json.Write(o, indent: false), new UTF8Encoding(false));
                if (File.Exists(_file)) File.Delete(_file);
                File.Move(_file + ".tmp", _file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private string Key(string path, long size, DateTime mtime) =>
            Path.GetFullPath(path) + "|" + size.ToString(CultureInfo.InvariantCulture) + "|" + mtime.Ticks.ToString(CultureInfo.InvariantCulture);

        /// <summary>SHA-256 of a local file; from the cache unless <paramref name="force"/>.</summary>
        public string Hash(string path, bool force, CancellationToken ct, Action<long> progress = null)
        {
            long size = _fs.GetFileLength(path);
            var mtime = _fs.GetLastWriteTimeUtc(path);
            var key = Key(path, size, mtime);
            if (!force)
                lock (_gate)
                    if (_hashes.TryGetValue(key, out var cached)) { progress?.Invoke(size); return cached; }
            string sha;
            using (var s = _fs.OpenRead(path)) sha = ObjectCodec.Sha256Hex(s, ct, progress);
            lock (_gate)
            {
                // drop stale entries for this path
                var prefix = Path.GetFullPath(path) + "|";
                foreach (var k in _hashes.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList()) _hashes.Remove(k);
                _hashes[key] = sha;
            }
            return sha;
        }

        public void Remember(string path, string sha)
        {
            if (!_fs.FileExists(path)) return;
            var key = Key(path, _fs.GetFileLength(path), _fs.GetLastWriteTimeUtc(path));
            lock (_gate) _hashes[key] = sha;
        }

        public void RememberChunkIndex(string fileSha, string indexObject)
        {
            lock (_gate) _indexes[fileSha] = indexObject;
        }

        public string ChunkIndexFor(string fileSha)
        {
            lock (_gate) return _indexes.TryGetValue(fileSha, out var o) ? o : null;
        }

        public IReadOnlyCollection<string> KnownIndexObjects
        {
            get { lock (_gate) return _indexes.Values.ToList(); }
        }
    }
}
