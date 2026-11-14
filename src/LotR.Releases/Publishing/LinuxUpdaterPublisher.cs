using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LotR.Releases.Hashing;
using LotR.Releases.Json;
using LotR.Releases.Model;
using LotR.Releases.Signing;

namespace LotR.Releases.Publishing
{
    /// <summary>
    /// The Linux / Steam Deck updater download (docs/LINUX.md, "Publishing"), files\updater\linux\ on the server and
    /// updater/linux/ in R2, versioned on its own (the Windows files in files\updater\ are not touched):
    /// <code>
    /// linux/lotr-updater-&lt;version&gt;-linux-x64.tar.gz           what tools/Build-LinuxUpdater.ps1 writes
    /// linux/lotr-updater-&lt;version&gt;-linux-x64.tar.gz.sha256    "&lt;hex&gt;  &lt;name&gt;" LF, like the .exe.sha256
    /// linux/latest.json + latest.json.sig                    same schema and signature as updater/latest.json (signed = false)
    /// </code>
    /// The archive holds one folder lotr-updater-&lt;version&gt;/ with the program lotr-updater (a 64-bit x86-64 ELF file,
    /// executable) and a README. <see cref="Inspect"/> refuses anything else, so a wrong file (the Windows exe, a
    /// renamed zip, an archive with links or "../" paths) is never published or installed.
    /// </summary>
    public static class LinuxUpdaterArchive
    {
        /// <summary>Sub-folder of files\updater (and of the R2 prefix updater/).</summary>
        public const string Folder = "linux";

        public const string ProgramName = "lotr-updater";

        /// <summary>Site-relative folder of the Linux files.</summary>
        public const string UrlFolder = "/files/updater/linux/";

        /// <summary>The program is ~12 MB compressed; anything near these limits is not ours.</summary>
        public const long MaxArchiveBytes = 256L << 20;

        public const long MaxUnpackedBytes = 512L << 20;

        // Plain x.y.z only, like every other link of the chain (Team-Publish.ps1, r2-sync.mjs, check-edge.mjs, the
        // Build-DeployPackage.ps1 allow-list): a 4-part or pre-release version would be signed and shown on the Install
        // page, then refused by the upload to R2 (a dead download link).
        private const string VersionRegex = @"\d+\.\d+\.\d+";
        private static readonly Regex VersionPattern = new Regex("^" + VersionRegex + "$", RegexOptions.CultureInvariant);
        private static readonly Regex NamePattern = new Regex(@"^lotr-updater-(?<v>" + VersionRegex + @")-linux-x64\.tar\.gz$", RegexOptions.CultureInvariant);
        private static readonly Regex HostPattern = new Regex(@"^[a-z0-9]([a-z0-9.-]{0,251}[a-z0-9])?(:\d{1,5})?$", RegexOptions.CultureInvariant);

        public static string FileName(string version) => "lotr-updater-" + version + "-linux-x64.tar.gz";

        /// <summary>The one top-level folder inside the archive.</summary>
        public static string TopFolder(string version) => "lotr-updater-" + version;

        /// <summary>/files/updater/linux/lotr-updater-&lt;version&gt;-linux-x64.tar.gz</summary>
        public static string UrlPath(string version) => UrlFolder + FileName(version);

        public static bool IsVersion(string version) => version != null && VersionPattern.IsMatch(version);

        /// <summary>"lotr-updater-0.1.0-linux-x64.tar.gz" → "0.1.0"; null for any other name.</summary>
        public static string VersionOfFileName(string fileName)
        {
            var m = NamePattern.Match(fileName ?? string.Empty);
            return m.Success ? m.Groups["v"].Value : null;
        }

        /// <summary>
        /// The url of a Linux latest.json is ours: exactly <see cref="UrlPath"/> for its version, or https://&lt;host&gt;
        /// followed by it (a plain host name, no user, query or fragment).
        /// </summary>
        public static bool IsOwnUrl(string url, string version)
        {
            if (url == null || !IsVersion(version))
            {
                return false;
            }

            string path = UrlPath(version);
            if (url == path)
            {
                return true;
            }

            const string https = "https://";
            if (!url.StartsWith(https, StringComparison.Ordinal) || !url.EndsWith(path, StringComparison.Ordinal))
            {
                return false;
            }

            string host = url.Substring(https.Length, url.Length - https.Length - path.Length);
            return HostPattern.IsMatch(host);
        }

        /// <summary>
        /// Reads the whole archive and checks it is the Linux updater of <paramref name="version"/>: gzip, a tar with valid
        /// header checksums, only regular files and folders under lotr-updater-&lt;version&gt;/ (no links, devices, absolute
        /// or "../" paths), the program lotr-updater there, executable, a 64-bit little-endian x86-64 ELF file.
        /// Throws <see cref="InvalidDataException"/> with the reason.
        /// </summary>
        public static LinuxArchiveInfo Inspect(byte[] archive, string version)
        {
            if (!IsVersion(version))
            {
                throw new ArgumentException("version '" + version + "' is not like 1.2.3");
            }

            if (archive == null || archive.Length < 18 || archive[0] != 0x1F || archive[1] != 0x8B)
            {
                throw new InvalidDataException("not a .tar.gz file (no gzip header)");
            }

            if (archive.LongLength > MaxArchiveBytes)
            {
                throw new InvalidDataException("the archive is larger than " + (MaxArchiveBytes >> 20) + " MB: not the Linux updater");
            }

            string top = TopFolder(version);
            string program = top + "/" + ProgramName;
            var info = new LinuxArchiveInfo();
            try
            {
                using (var gz = new GZipStream(new MemoryStream(archive, false), CompressionMode.Decompress))
                {
                    var header = new byte[512];
                    long unpacked = 0;
                    string paxPath = null;
                    bool end = false;
                    while (!end)
                    {
                        if (ReadFull(gz, header) != 512)
                        {
                            throw new InvalidDataException("the tar archive is truncated (no end-of-archive marker)");
                        }

                        if (header.All(b => b == 0))
                        {
                            end = true;
                            break;
                        }

                        CheckHeaderSum(header);
                        char type = (char)header[156];
                        long size = Octal(header, 124, 12, "size");
                        long mode = Octal(header, 100, 8, "mode");
                        unpacked += size;
                        if (size < 0 || unpacked > MaxUnpackedBytes)
                        {
                            throw new InvalidDataException("the archive unpacks to more than " + (MaxUnpackedBytes >> 20) + " MB: not the Linux updater");
                        }

                        if (type == 'x')
                        {
                            // POSIX pax extended header: its "path" replaces the name of the NEXT entry.
                            paxPath = PaxPath(ReadData(gz, size), paxPath);
                            continue;
                        }

                        if (type == 'g')
                        {
                            Skip(gz, Padded(size));
                            continue;
                        }

                        string name = paxPath ?? HeaderName(header);
                        paxPath = null;
                        if (type != '0' && type != '\0' && type != '5')
                        {
                            throw new InvalidDataException("the archive holds '" + name + "' of tar type '" + Printable(type) + "' (only plain files and folders are allowed)");
                        }

                        bool dir = type == '5';
                        string clean = CheckName(name, top, dir);
                        info.Entries.Add(clean + (dir ? "/" : string.Empty));
                        if (dir)
                        {
                            if (size != 0)
                            {
                                throw new InvalidDataException("folder '" + name + "' has a size in the archive");
                            }

                            continue;
                        }

                        if (clean == program)
                        {
                            if (info.ProgramSize > 0)
                            {
                                throw new InvalidDataException(program + " is twice in the archive");
                            }

                            if ((mode & 0x40) == 0)
                            {
                                throw new InvalidDataException(program + " is not executable in the archive (mode " + Convert.ToString(mode, 8) + ")");
                            }

                            byte[] head = ReadData(gz, size, 64);
                            CheckElf(head, program);
                            info.ProgramSize = size;
                            info.ProgramMode = mode;
                            continue;
                        }

                        Skip(gz, Padded(size));
                    }
                }
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception e) when (e is IOException || e is EndOfStreamException)
            {
                throw new InvalidDataException("the archive cannot be read: " + e.Message);
            }

            if (info.ProgramSize <= 0)
            {
                throw new InvalidDataException("the archive has no " + program + " (expected the folder " + top + "/ with the program inside)");
            }

            if (info.Entries.Distinct(StringComparer.Ordinal).Count() != info.Entries.Count)
            {
                throw new InvalidDataException("the archive lists the same path twice");
            }

            return info;
        }

        private static void CheckElf(byte[] head, string program)
        {
            bool elf = head.Length >= 20 && head[0] == 0x7F && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F';
            if (!elf)
            {
                throw new InvalidDataException(program + " is not a Linux program (no ELF header)");
            }

            // e_ident[EI_CLASS] = 2 (64-bit), e_ident[EI_DATA] = 1 (little-endian), e_machine = 0x3E (x86-64).
            if (head[4] != 2 || head[5] != 1 || head[18] != 0x3E || head[19] != 0)
            {
                throw new InvalidDataException(program + " is not a 64-bit x86-64 Linux program (linux-x64 expected)");
            }
        }

        private static string CheckName(string name, string top, bool dir)
        {
            string n = dir ? name.TrimEnd('/') : name;
            if (n.StartsWith("./", StringComparison.Ordinal))
            {
                n = n.Substring(2);
            }

            if (n.Length == 0 || n.StartsWith("/", StringComparison.Ordinal) || n.IndexOf('\\') >= 0 || n.IndexOf(':') >= 0 || n.IndexOf('\0') >= 0
                || n.Split('/').Any(s => s.Length == 0 || s == "." || s == ".."))
            {
                throw new InvalidDataException("the archive holds an unsafe path '" + name + "'");
            }

            if (n != top && !n.StartsWith(top + "/", StringComparison.Ordinal))
            {
                throw new InvalidDataException("'" + name + "' is outside the folder " + top + "/ (wrong version, or not the Linux updater)");
            }

            if (n == top && !dir)
            {
                throw new InvalidDataException(top + " is a file in the archive, not a folder");
            }

            return n;
        }

        private static string HeaderName(byte[] h)
        {
            string name = Field(h, 0, 100);
            bool ustar = h[257] == (byte)'u' && h[258] == (byte)'s' && h[259] == (byte)'t' && h[260] == (byte)'a' && h[261] == (byte)'r';
            if (ustar)
            {
                string prefix = Field(h, 345, 155);
                if (prefix.Length > 0)
                {
                    name = prefix + "/" + name;
                }
            }

            return name;
        }

        private static string PaxPath(byte[] data, string current)
        {
            // Records: "<length> <key>=<value>\n", length in decimal and counting the whole record.
            string path = current;
            int i = 0;
            while (i < data.Length)
            {
                int sp = Array.IndexOf(data, (byte)' ', i);
                if (sp < 0 || !int.TryParse(Encoding.ASCII.GetString(data, i, sp - i), NumberStyles.None, CultureInfo.InvariantCulture, out int len) || len <= sp - i + 1 || i + len > data.Length || data[i + len - 1] != (byte)'\n')
                {
                    throw new InvalidDataException("the archive has a damaged pax header");
                }

                string record = Encoding.UTF8.GetString(data, sp + 1, i + len - sp - 2);
                int eq = record.IndexOf('=');
                if (eq <= 0)
                {
                    throw new InvalidDataException("the archive has a damaged pax header");
                }

                string key = record.Substring(0, eq);
                if (key == "path")
                {
                    path = record.Substring(eq + 1);
                }
                else if (key == "linkpath")
                {
                    throw new InvalidDataException("the archive holds a link (pax linkpath)");
                }

                i += len;
            }

            return path;
        }

        private static void CheckHeaderSum(byte[] h)
        {
            long stored = Octal(h, 148, 8, "checksum");
            long sum = 0;
            for (int i = 0; i < 512; i++)
            {
                sum += i >= 148 && i < 156 ? (byte)' ' : h[i];
            }

            if (sum != stored)
            {
                throw new InvalidDataException("the tar archive is damaged (header checksum)");
            }
        }

        private static long Octal(byte[] h, int at, int length, string what)
        {
            long v = 0;
            bool digits = false;
            for (int i = at; i < at + length; i++)
            {
                byte b = h[i];
                if (b == 0 || (b == (byte)' ' && digits))
                {
                    break;
                }

                if (b == (byte)' ')
                {
                    continue;
                }

                if (b < (byte)'0' || b > (byte)'7')
                {
                    throw new InvalidDataException("the tar archive is damaged (" + what + " field)");
                }

                v = (v * 8) + (b - '0');
                digits = true;
            }

            return v;
        }

        private static string Field(byte[] h, int at, int length)
        {
            int end = Array.IndexOf(h, (byte)0, at, length);
            return Encoding.UTF8.GetString(h, at, (end < 0 ? at + length : end) - at);
        }

        private static string Printable(char c) => c >= ' ' && c < 127 ? c.ToString() : "\\x" + ((int)c).ToString("x2", CultureInfo.InvariantCulture);

        private static long Padded(long size) => (size + 511) / 512 * 512;

        private static int ReadFull(Stream s, byte[] buf) => ReadFull(s, buf, buf.Length);

        private static int ReadFull(Stream s, byte[] buf, int count)
        {
            int done = 0;
            while (done < count)
            {
                int n = s.Read(buf, done, count - done);
                if (n <= 0)
                {
                    break;
                }

                done += n;
            }

            return done;
        }

        /// <summary>Reads an entry's data (and its padding); keeps at most <paramref name="keep"/> bytes.</summary>
        private static byte[] ReadData(Stream s, long size, int keep = int.MaxValue)
        {
            if (size > 1 << 20 && keep == int.MaxValue)
            {
                throw new InvalidDataException("the archive has an oversized pax header");
            }

            var kept = new byte[(int)Math.Min(size, keep)];
            if (ReadFull(s, kept) != kept.Length)
            {
                throw new InvalidDataException("the tar archive is truncated");
            }

            Skip(s, Padded(size) - kept.Length);
            return kept;
        }

        private static void Skip(Stream s, long count)
        {
            var buf = new byte[81920];
            while (count > 0)
            {
                int n = ReadFull(s, buf, (int)Math.Min(buf.Length, count));
                if (n == 0)
                {
                    throw new InvalidDataException("the tar archive is truncated");
                }

                count -= n;
            }
        }
    }

    /// <summary>What <see cref="LinuxUpdaterArchive.Inspect"/> found.</summary>
    public sealed class LinuxArchiveInfo
    {
        /// <summary>Every path in the archive ("lotr-updater-0.1.0/", "lotr-updater-0.1.0/lotr-updater"...).</summary>
        public List<string> Entries { get; } = new List<string>();

        public long ProgramSize { get; set; }

        public long ProgramMode { get; set; }
    }

    public sealed class LinuxUpdaterPublishRequest
    {
        /// <summary>The lotr-updater-&lt;version&gt;-linux-x64.tar.gz written by tools/Build-LinuxUpdater.ps1.</summary>
        public string ArchivePath { get; set; }

        /// <summary>The files\updater folder (the SAME one as for the Windows exe): the files go to its linux\ sub-folder.</summary>
        public string UpdaterDir { get; set; }

        /// <summary>Default: the version in the archive's file name. When both are there they must agree.</summary>
        public string Version { get; set; }

        /// <summary>When set, latest.json's url is https://domain/files/updater/linux/...; else site-relative.</summary>
        public string BaseUrl { get; set; }

        /// <summary>Release keys: linux\latest.json.sig is written with them (same format as updater\latest.json.sig).</summary>
        public List<ReleaseSigner> Signers { get; } = new List<ReleaseSigner>();

        public DateTime? NowUtc { get; set; }

        /// <summary>Replace an already published archive of the same version with different bytes.</summary>
        public bool Force { get; set; }
    }

    public sealed class LinuxUpdaterPublishResult
    {
        public UpdaterLatest Latest { get; set; }

        public string ArchiveFile { get; set; }

        public string Sha256File { get; set; }

        public string LatestFile { get; set; }

        public string SignatureFile { get; set; }

        public LinuxArchiveInfo Archive { get; set; }

        public bool AlreadyPublished { get; set; }
    }

    /// <summary>
    /// Publishes the Linux updater (see <see cref="LinuxUpdaterArchive"/>) into &lt;UpdaterDir&gt;\linux\: the archive, its
    /// .sha256, latest.json.sig, then latest.json LAST, each written to a temporary name and renamed, exactly like
    /// <see cref="UpdaterPublisher"/> does for the Windows exe. There is no channel: one latest.json.
    /// </summary>
    public static class LinuxUpdaterPublisher
    {
        public static LinuxUpdaterPublishResult Publish(LinuxUpdaterPublishRequest req)
        {
            if (req.ArchivePath == null || !File.Exists(req.ArchivePath))
            {
                throw new FileNotFoundException("Linux updater archive not found", req.ArchivePath);
            }

            if (string.IsNullOrEmpty(req.UpdaterDir))
            {
                throw new ArgumentException("UpdaterDir is required");
            }

            string fromName = LinuxUpdaterArchive.VersionOfFileName(Path.GetFileName(req.ArchivePath));
            string version = req.Version ?? fromName;
            if (version == null)
            {
                throw new ArgumentException(Path.GetFileName(req.ArchivePath) + " is not named lotr-updater-<version>-linux-x64.tar.gz; pass --version");
            }

            if (!LinuxUpdaterArchive.IsVersion(version))
            {
                throw new ArgumentException("version '" + version + "' is not like 1.2.3");
            }

            if (fromName != null && fromName != version)
            {
                throw new ArgumentException("--version " + version + " but the archive is named for " + fromName);
            }

            string url = LinuxUpdaterArchive.UrlPath(version);
            if (!string.IsNullOrEmpty(req.BaseUrl))
            {
                if (!req.BaseUrl.StartsWith("https://", StringComparison.Ordinal))
                {
                    throw new ArgumentException("--base-url must be https://");
                }

                url = req.BaseUrl.TrimEnd('/') + url;
                if (!LinuxUpdaterArchive.IsOwnUrl(url, version))
                {
                    throw new ArgumentException("--base-url must be https://<host> only (no path): " + req.BaseUrl);
                }
            }

            if (new FileInfo(req.ArchivePath).Length > LinuxUpdaterArchive.MaxArchiveBytes)
            {
                throw new InvalidDataException(Path.GetFileName(req.ArchivePath) + " is larger than " + (LinuxUpdaterArchive.MaxArchiveBytes >> 20) + " MB: not the Linux updater");
            }

            byte[] archive = File.ReadAllBytes(req.ArchivePath);
            string sha = Sha256Util.Of(archive);

            // The .sha256 the build wrote next to it, when there: a copy damaged on the way is caught here.
            string buildSum = req.ArchivePath + ".sha256";
            if (File.Exists(buildSum))
            {
                (string Sha256, string FileName) sum;
                try
                {
                    sum = Sha256SumFile.Parse(File.ReadAllText(buildSum));
                }
                catch (FormatException e)
                {
                    throw new InvalidDataException(Path.GetFileName(buildSum) + ": " + e.Message);
                }

                if (sum.Sha256 != sha)
                {
                    throw new InvalidDataException(Path.GetFileName(req.ArchivePath) + " does not match the " + Path.GetFileName(buildSum) + " written by the build (damaged copy?)");
                }
            }

            var info = LinuxUpdaterArchive.Inspect(archive, version);

            string dir = Path.Combine(req.UpdaterDir, LinuxUpdaterArchive.Folder);
            Directory.CreateDirectory(dir);
            string name = LinuxUpdaterArchive.FileName(version);
            string archiveOut = Path.Combine(dir, name);
            var result = new LinuxUpdaterPublishResult
            {
                ArchiveFile = archiveOut,
                Sha256File = archiveOut + ".sha256",
                LatestFile = Path.Combine(dir, UpdaterPublisher.LatestFileName("stable")),
                Archive = info,
            };

            if (File.Exists(archiveOut))
            {
                if (Sha256Util.OfFile(archiveOut) == sha)
                {
                    result.AlreadyPublished = true;
                }
                else if (!req.Force)
                {
                    throw new InvalidOperationException("linux/" + name + " is already published with different bytes; bump the version (or --force)");
                }
            }

            if (!result.AlreadyPublished || req.Force)
            {
                AtomicFile.Write(archiveOut, archive);
            }

            AtomicFile.Write(result.Sha256File, Encoding.ASCII.GetBytes(Sha256SumFile.Format(sha, name)));

            var latest = new UpdaterLatest
            {
                Version = version,
                Url = url,
                Sha256 = sha,
                Size = archive.LongLength,
                ReleasedAt = (req.NowUtc ?? DateTime.UtcNow).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                Signed = false, // no Authenticode on Linux; the release-key signature is latest.json.sig
            };
            var val = latest.Validate();
            if (!val.IsValid)
            {
                throw new InvalidOperationException("linux/latest.json would be invalid: " + string.Join("; ", val.Errors));
            }

            byte[] latestBytes = latest.ToJson().ToCanonicalBytes();
            if (req.Signers.Count > 0)
            {
                // Signature first, then latest.json: whoever sees the new latest.json also finds its signature.
                result.SignatureFile = result.LatestFile + ".sig";
                AtomicFile.Write(result.SignatureFile, ManifestSignature.Create(latestBytes, req.Signers));
            }
            else if (File.Exists(result.LatestFile + ".sig"))
            {
                // A signature of the PREVIOUS latest.json would make the site export and the updaters refuse the new one.
                File.Delete(result.LatestFile + ".sig");
            }

            AtomicFile.Write(result.LatestFile, latestBytes);
            result.Latest = latest;
            return result;
        }
    }
}
