using System;
using System.Collections.Generic;
using System.Linq;
using LotR.Releases.Json;
using LotR.Releases.Model;

namespace LotR.Releases.Chunking
{
    /// <summary>
    /// The chunk index of one chunked file, stored as an object (canonical JSON, then zst/raw by the 3 % rule) and
    /// named by the SHA-256 of its canonical bytes, so the signed manifest covers it.
    /// <code>
    /// {"chunker":"fastcdc2020;avg=262144;min=65536;max=1048576;nc=1","chunks":[["&lt;sha256&gt;",size,objectSize,"zst"],...],
    ///  "schema":1,"sha256":"&lt;whole file&gt;","size":N}
    /// </code>
    /// Rows are in file order; the object of a row is "&lt;sha256&gt;.&lt;ext&gt;".
    /// Rows are compact arrays [sha256, size, objectSize, ext] (CONTRACTS §8, accepted 15/10).
    /// </summary>
    public sealed class ChunkIndex
    {
        public const int CurrentSchema = 1;

        public static readonly string ContractChunker = "fastcdc2020;avg=" + FastCdc.ContractAverage + ";min=" + FastCdc.ContractMin + ";max=" + FastCdc.ContractMax + ";nc=" + FastCdc.NormalizationLevel;

        public string Chunker { get; set; } = ContractChunker;

        public string Sha256 { get; set; }

        public long Size { get; set; }

        public List<ChunkRef> Chunks { get; set; } = new List<ChunkRef>();

        /// <summary>Stored bytes of the distinct chunks (what a fresh install downloads for this file).</summary>
        public long DistinctObjectBytes => Distinct().Sum(c => c.ObjectSize);

        public IEnumerable<ChunkRef> Distinct()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in Chunks)
            {
                if (seen.Add(c.Sha256))
                {
                    yield return c;
                }
            }
        }

        public byte[] ToCanonicalBytes()
        {
            var rows = new JsonArray();
            foreach (var c in Chunks)
            {
                rows.Add(new JsonArray { c.Sha256, c.Size, c.ObjectSize, c.Ext });
            }

            return new JsonObject
            {
                ["schema"] = CurrentSchema,
                ["chunker"] = Chunker,
                ["sha256"] = Sha256,
                ["size"] = Size,
                ["chunks"] = rows,
            }.ToCanonicalBytes();
        }

        public static ChunkIndex Parse(byte[] utf8)
        {
            var o = JsonParser.Parse(utf8) as JsonObject ?? throw new ManifestFormatException("chunk index: not an object");
            if (o["schema"]?.AsLong() != CurrentSchema)
            {
                throw new ManifestFormatException("chunk index: unsupported schema");
            }

            var idx = new ChunkIndex
            {
                Chunker = o["chunker"]?.AsString() ?? throw new ManifestFormatException("chunk index: chunker missing"),
                Sha256 = o["sha256"]?.AsString() ?? throw new ManifestFormatException("chunk index: sha256 missing"),
                Size = o["size"]?.AsLong() ?? throw new ManifestFormatException("chunk index: size missing"),
            };
            var rows = o["chunks"] as JsonArray ?? throw new ManifestFormatException("chunk index: chunks missing");
            long total = 0;
            foreach (var r in rows)
            {
                var a = r as JsonArray;
                if (a == null || a.Count != 4)
                {
                    throw new ManifestFormatException("chunk index: a row is not [sha256,size,objectSize,ext]");
                }

                var c = new ChunkRef
                {
                    Sha256 = a[0].AsString(),
                    Size = a[1].AsLong() ?? -1,
                    ObjectSize = a[2].AsLong() ?? -1,
                    Ext = a[3].AsString(),
                };
                if (c.Sha256 == null || !ManifestValidator.Sha256Pattern.IsMatch(c.Sha256) || c.Size <= 0 || c.ObjectSize < 0 || c.Ext == null)
                {
                    throw new ManifestFormatException("chunk index: invalid row " + a.ToCanonicalString());
                }

                total += c.Size;
                idx.Chunks.Add(c);
            }

            if (total != idx.Size)
            {
                throw new ManifestFormatException("chunk index: chunk sizes add up to " + total + ", file size is " + idx.Size);
            }

            return idx;
        }
    }

    public sealed class ChunkRef
    {
        public string Sha256 { get; set; }

        public long Size { get; set; }

        public long ObjectSize { get; set; }

        public string Ext { get; set; }

        public string Object => ObjectNames.Make(Sha256, Ext);
    }
}
