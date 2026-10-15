using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lotr.Updater.Core.IO;
using Lotr.Updater.Core.Logging;
using Lotr.Updater.Core.Model;
using Lotr.Updater.Core.Net;

namespace Lotr.Updater.Core.Objects
{
    public sealed class CorruptObjectException : Exception
    {
        public CorruptObjectException(string message) : base(message) { }
    }

    /// <summary>
    /// Local cache of downloaded objects (%LOCALAPPDATA%\LotRMPUpdater\cache\objects\aa\&lt;sha&gt;.&lt;ext&gt;). An object is only
    /// moved from "x.part" to its final name after its DECODED bytes matched the SHA-256 in its name.
    /// </summary>
    public sealed class ObjectStore
    {
        private readonly UpdaterPaths _paths;
        private readonly IFileSystem _fs;
        private readonly HttpFetcher _fetch;
        private readonly IUpdaterLog _log;

        public ObjectStore(UpdaterPaths paths, IFileSystem fs, HttpFetcher fetch, IUpdaterLog log)
        {
            _paths = paths;
            _fs = fs;
            _fetch = fetch;
            _log = log;
        }

        public string PathOf(string objectName) => _paths.CachedObjectPath(objectName);

        public bool Has(string objectName) => _fs.FileExists(PathOf(objectName));

        /// <summary>Bytes already downloaded towards this object (resume), for progress.</summary>
        public long PartialBytes(string objectName)
        {
            var p = PathOf(objectName) + ".part";
            return _fs.FileExists(p) ? _fs.GetFileLength(p) : 0;
        }

        /// <summary>Downloads (or resumes) and verifies one object. A corrupt object is deleted and downloaded once more;
        /// a second corruption stops with a clear error.</summary>
        public async Task EnsureAsync(Uri uri, string objectName, long objectSize, Action<long> progress, CancellationToken ct, long decodedSize = -1)
        {
            var final = PathOf(objectName);
            if (_fs.FileExists(final)) return;
            var ext = ObjectCodec.ExtensionOf(objectName);
            ObjectCodec.CodecFor(ext); // throws the "update the updater" message for an unknown transport
            var expected = ObjectCodec.ShaOf(objectName);
            _fs.CreateDirectory(Path.GetDirectoryName(final));
            var part = final + ".part";
            for (int round = 1; ; round++)
            {
                await _fetch.DownloadResumableAsync(uri, part, objectSize, progress, ct).ConfigureAwait(false);
                DecodeResult r;
                try
                {
                    r = ObjectCodec.DecodeTo(_fs.OpenRead(part), ext, null, null, ct, decodedSize);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException) && !(ex is IOException && IsDiskError(ex)))
                {
                    r = new DecodeResult(-1, "undecodable: " + ex.Message);
                }
                if (r.Sha256 == expected)
                {
                    if (_fs.FileExists(final)) _fs.DeleteFile(part); // another task finished the same object
                    else _fs.MoveFile(part, final);
                    return;
                }
                _fs.DeleteFile(part);
                progress?.Invoke(-objectSize);
                if (round >= 2)
                    throw new CorruptObjectException($"The file {objectName.Substring(0, 12)}… arrived damaged twice (expected SHA-256 {expected.Substring(0, 12)}…, got {Short(r.Sha256)}). Nothing was installed. Try again later; if it keeps happening, send your log to support.");
                _log?.Write(LogLevel.Warn, $"Object {objectName.Substring(0, 12)}… failed its SHA-256 check ({Short(r.Sha256)}); downloading it again.");
            }
        }

        /// <summary>Small object of unknown size (chunk index): plain GET, verified, cached.</summary>
        public async Task EnsureSmallAsync(Uri uri, string objectName, CancellationToken ct)
        {
            var final = PathOf(objectName);
            if (_fs.FileExists(final)) return;
            for (int round = 1; ; round++)
            {
                var bytes = await _fetch.GetBytesAsync(uri, ct).ConfigureAwait(false);
                DecodeResult r;
                try { r = ObjectCodec.DecodeTo(new MemoryStream(bytes), ObjectCodec.ExtensionOf(objectName), null, null, ct); }
                catch (Exception ex) when (!(ex is OperationCanceledException)) { r = new DecodeResult(-1, "undecodable: " + ex.Message); }
                if (r.Sha256 == ObjectCodec.ShaOf(objectName))
                {
                    _fs.CreateDirectory(Path.GetDirectoryName(final));
                    using (var o = _fs.OpenWrite(final + ".part", false)) o.Write(bytes, 0, bytes.Length);
                    if (!_fs.FileExists(final)) _fs.MoveFile(final + ".part", final); else _fs.DeleteFile(final + ".part");
                    return;
                }
                if (round >= 2) throw new CorruptObjectException("The chunk list " + objectName.Substring(0, 12) + "… arrived damaged twice. Nothing was installed.");
                _log?.Write(LogLevel.Warn, "Chunk list " + objectName.Substring(0, 12) + "… failed its check; downloading it again.");
            }
        }

        private static string Short(string s) => s == null ? "?" : (s.Length > 20 ? s.Substring(0, 12) + "…" : s);

        private static bool IsDiskError(Exception ex) => ex.HResult == unchecked((int)0x80070070) || ex.HResult == unchecked((int)0x80070027);

        /// <summary>Decodes a cached object into <paramref name="destination"/> and checks its hash.</summary>
        public void DecodeVerified(string objectName, Stream destination, Action<long> progress, CancellationToken ct, long decodedSize = -1)
        {
            {
                var r = ObjectCodec.DecodeTo(_fs.OpenRead(PathOf(objectName)), ObjectCodec.ExtensionOf(objectName), destination, progress, ct, decodedSize);
                if (r.Sha256 != ObjectCodec.ShaOf(objectName))
                {
                    try { _fs.DeleteFile(PathOf(objectName)); } catch (IOException) { }
                    throw new CorruptObjectException("A downloaded file was damaged on disk after download (" + objectName.Substring(0, 12) + "…). It was removed; run the update again.");
                }
            }
        }

        public byte[] ReadDecoded(string objectName, CancellationToken ct)
        {
            using (var ms = new MemoryStream())
            {
                DecodeVerified(objectName, ms, null, ct);
                return ms.ToArray();
            }
        }

        public void Delete(string objectName)
        {
            try { _fs.DeleteFile(PathOf(objectName)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
