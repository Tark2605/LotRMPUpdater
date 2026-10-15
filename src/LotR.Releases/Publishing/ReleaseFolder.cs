using System;
using System.Collections.Generic;
using System.IO;
using LotR.Releases.Chunking;
using LotR.Releases.Hashing;
using LotR.Releases.Model;
using LotR.Releases.Transport;

namespace LotR.Releases.Publishing
{
    /// <summary>A published release folder: &lt;releases&gt;/&lt;releaseId&gt;/{manifest.json, manifest.json.sig, objects/}.</summary>
    public sealed class ReleaseFolder
    {
        public const string ManifestFile = "manifest.json";
        public const string SignatureFile = "manifest.json.sig";
        public const string ChannelsFile = "channels.json";

        /// <summary>Chunk indexes are small; anything claiming more is refused before allocating.</summary>
        public const long MaxIndexBytes = 64L * 1024 * 1024;

        private ReleaseFolder(string path)
        {
            Path = System.IO.Path.GetFullPath(path);
            Id = System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
        }

        public string Path { get; }

        public string Id { get; }

        public byte[] ManifestBytes { get; private set; }

        /// <summary>Null when the .sig file is missing.</summary>
        public byte[] SignatureBytes { get; private set; }

        public ReleaseManifest Manifest { get; private set; }

        public static ReleaseFolder Open(string path)
        {
            var r = new ReleaseFolder(path);
            string m = System.IO.Path.Combine(r.Path, ManifestFile);
            if (!File.Exists(m))
            {
                throw new FileNotFoundException("No manifest.json in " + r.Path, m);
            }

            r.ManifestBytes = File.ReadAllBytes(m);
            string s = System.IO.Path.Combine(r.Path, SignatureFile);
            r.SignatureBytes = File.Exists(s) ? File.ReadAllBytes(s) : null;
            r.Manifest = ManifestCodec.Parse(r.ManifestBytes);
            return r;
        }

        public string ObjectPath(string objectName) =>
            System.IO.Path.Combine(Path, ObjectNames.RelativePath(objectName).Replace('/', System.IO.Path.DirectorySeparatorChar));

        public bool HasObject(string objectName) => File.Exists(ObjectPath(objectName));

        /// <summary>Reads and decodes an object; checks the SHA-256 of the decoded bytes against its name.</summary>
        public byte[] ReadObject(string objectName, long expectedSize)
        {
            byte[] raw = File.ReadAllBytes(ObjectPath(objectName));
            byte[] decoded = ObjectIO.Decode(raw, ObjectNames.Extension(objectName), expectedSize);
            string sha = Sha256Util.Of(decoded);
            if (!string.Equals(sha, ObjectNames.Sha(objectName), StringComparison.Ordinal))
            {
                throw new InvalidDataException("object " + objectName + " decodes to content with SHA-256 " + sha);
            }

            return decoded;
        }

        public ChunkIndex ReadChunkIndex(string indexObject)
        {
            var idx = ChunkIndex.Parse(ReadObject(indexObject, -1));
            return idx;
        }

        /// <summary>Every object the release references (file objects, chunk indexes and their chunks), by sha.</summary>
        public Dictionary<string, ObjectRef> CollectObjects(ManifestModule only = null)
        {
            var result = new Dictionary<string, ObjectRef>(StringComparer.Ordinal);
            foreach (var f in only != null ? only.Files : Manifest.AllFiles)
            {
                if (!f.Chunked)
                {
                    result[f.Sha256] = new ObjectRef { Sha256 = f.Sha256, Ext = ObjectNames.Extension(f.Object), ObjectSize = f.ObjectSize, Kind = "file" };
                    continue;
                }

                string indexSha = ObjectNames.Sha(f.ChunkIndex);
                long indexSize = new FileInfo(ObjectPath(f.ChunkIndex)).Length;
                result[indexSha] = new ObjectRef { Sha256 = indexSha, Ext = ObjectNames.Extension(f.ChunkIndex), ObjectSize = indexSize, Kind = "index" };
                foreach (var c in ReadChunkIndex(f.ChunkIndex).Chunks)
                {
                    result[c.Sha256] = new ObjectRef { Sha256 = c.Sha256, Ext = c.Ext, ObjectSize = c.ObjectSize, Kind = "chunk" };
                }
            }

            return result;
        }
    }

    public static class ObjectIO
    {
        /// <summary>Decodes object bytes. <paramref name="expectedSize"/> -1 = trust the zstd frame (bounded).</summary>
        public static byte[] Decode(byte[] objectBytes, string ext, long expectedSize)
        {
            var codec = TransportCodecs.Get(ext);
            if (expectedSize < 0)
            {
                expectedSize = codec is ZstdCodec ? ZstdFrame.Inspect(objectBytes).ContentSize : objectBytes.LongLength;
                if (expectedSize > ReleaseFolder.MaxIndexBytes)
                {
                    throw new InvalidDataException("object declares " + expectedSize + " bytes, more than allowed here");
                }
            }

            return codec.Decode(objectBytes, expectedSize);
        }
    }
}
