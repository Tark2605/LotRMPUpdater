using System;
using System.Collections.Generic;
using System.IO;
using LotR.Releases.Model;

namespace LotR.Releases.Tests
{
    internal sealed class TempDir : IDisposable
    {
        public TempDir(string prefix = "lotr-rel-")
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N").Substring(0, 10));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Sub(params string[] parts)
        {
            var all = new List<string> { Path };
            all.AddRange(parts);
            return System.IO.Path.Combine(all.ToArray());
        }

        public void Dispose()
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                }

                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    internal static class TestData
    {
        /// <summary>SplitMix64 byte stream, identical to Data/gen-fastcdc-vectors.py.</summary>
        public static byte[] SplitMix(ulong seed, int n)
        {
            var o = new byte[n];
            ulong x = seed;
            int i = 0;
            while (i < n)
            {
                x += 0x9E3779B97F4A7C15UL;
                ulong z = x;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                z ^= z >> 31;
                for (int k = 0; k < 8 && i < n; k++, i++)
                {
                    o[i] = (byte)(z >> (8 * k));
                }
            }

            return o;
        }

        public static string DataFile(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Data", name);

        /// <summary>Read-only source: the team repo clone (never written).</summary>
        public static string RealEventModule
        {
            get
            {
                string dir = AppContext.BaseDirectory;
                for (int i = 0; i < 10 && dir != null; i++)
                {
                    string c = System.IO.Path.Combine(dir, "colleague", "github-LotRMPEvent", "LotRMPEvent");
                    if (File.Exists(System.IO.Path.Combine(c, "SubModule.xml")))
                    {
                        return c;
                    }

                    dir = System.IO.Path.GetDirectoryName(dir);
                }

                return null;
            }
        }

        public static string Sha(char c) => new string(c, 64);

        public static ReleaseManifest SmallManifest()
        {
            return new ReleaseManifest
            {
                ReleaseId = "20261015-140000-a1b2c3d4",
                Channel = "stable",
                PublishedAt = "2026-10-15T14:00:00Z",
                PublishedBy = "test",
                Notes = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["en"] = "Héllo \"quoted\"\n" },
                Game = new GameRange { MinVersion = "v1.2.12" },
                Endpoints = new ReleaseEndpoints { Base = "https://example.invalid" },
                Modules =
                {
                    new ManifestModule
                    {
                        Id = "OrderMiddleEarthEvent",
                        Name = "LotRMPEvent",
                        Version = "v1.0.2.2",
                        Folder = "LotRMPEvent",
                        Role = ModuleRoles.Event,
                        WorkshopItem = "3774022783",
                        DependsOn = { new ModuleDependency { Id = "LotRMPCore" }, new ModuleDependency { Id = "Native", Version = "v1.2.12", Optional = true } },
                        Files =
                        {
                            new ManifestFile { Path = "SubModule.xml", Size = 10, Sha256 = Sha('a'), Object = Sha('a') + ".zst", ObjectSize = 8 },
                            new ManifestFile { Path = "bin/Win64_Shipping_Client/X.dll", Size = 5, Sha256 = Sha('b'), Object = Sha('b') + ".raw", ObjectSize = 5, Exec = true },
                            new ManifestFile { Path = "SceneObj/Big/terrain.bin", Size = 20000000, Sha256 = Sha('c'), Chunked = true, ChunkIndex = Sha('d') + ".zst", ObjectSize = 19000000 },
                        },
                        Scenes = { new SceneInfo { Name = "Big", UniqueToken = "{X}", Revision = "1" } },
                    },
                },
            };
        }
    }
}
