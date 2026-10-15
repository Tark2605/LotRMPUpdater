using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Lotr.Updater.Core.IO
{
    /// <summary>Every disk operation of the install engine goes through this, so tests can inject failures
    /// (locked file, full disk, read-only folder, crash in the middle of a swap).</summary>
    public interface IFileSystem
    {
        bool FileExists(string path);
        bool DirectoryExists(string path);
        long GetFileLength(string path);
        DateTime GetLastWriteTimeUtc(string path);
        Stream OpenRead(string path);
        /// <summary>Opens for writing; append keeps existing bytes (resumable downloads).</summary>
        Stream OpenWrite(string path, bool append);
        void CreateDirectory(string path);
        void MoveFile(string source, string destination);
        void CopyFile(string source, string destination, bool overwrite);
        void DeleteFile(string path);
        void DeleteDirectory(string path, bool recursive);
        IEnumerable<string> EnumerateFiles(string directory, bool recursive);
        IEnumerable<string> EnumerateDirectories(string directory);
        long GetAvailableFreeSpace(string path);
        /// <summary>Creates and deletes a delete-on-close temp file in <paramref name="directory"/>. Never elevates.</summary>
        WriteProbeResult ProbeWritable(string directory);
        bool HasMarkOfTheWeb(string path);
        /// <summary>Removes the Zone.Identifier alternate data stream. Returns true when one was removed.</summary>
        bool ClearMarkOfTheWeb(string path);
        bool SameVolume(string a, string b);
    }

    public sealed class WriteProbeResult
    {
        public WriteProbeResult(bool writable, string detail) { Writable = writable; Detail = detail; }
        public bool Writable { get; }
        public string Detail { get; }
    }

    public class RealFileSystem : IFileSystem
    {
        public virtual bool FileExists(string path) => File.Exists(path);
        public virtual bool DirectoryExists(string path) => Directory.Exists(path);
        public virtual long GetFileLength(string path) => new FileInfo(path).Length;
        public virtual DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);
        public virtual Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);

        public virtual Stream OpenWrite(string path, bool append)
        {
            var fs = new FileStream(path, append ? FileMode.OpenOrCreate : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            if (append) fs.Seek(0, SeekOrigin.End);
            return fs;
        }

        public virtual void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public virtual void MoveFile(string source, string destination)
        {
            // MoveFileEx without REPLACE_EXISTING: the engine always moves into an empty slot (journal guarantees it),
            // a rename on one volume, copy+delete across volumes.
            File.Move(source, destination);
        }

        public virtual void CopyFile(string source, string destination, bool overwrite) => File.Copy(source, destination, overwrite);

        public virtual void DeleteFile(string path)
        {
            if (!File.Exists(path)) return;
            var attr = File.GetAttributes(path);
            if ((attr & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);
            File.Delete(path);
        }

        public virtual void DeleteDirectory(string path, bool recursive)
        {
            if (!Directory.Exists(path)) return;
            if (recursive)
                foreach (var f in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    var attr = File.GetAttributes(f);
                    if ((attr & FileAttributes.ReadOnly) != 0) File.SetAttributes(f, attr & ~FileAttributes.ReadOnly);
                }
            Directory.Delete(path, recursive);
        }

        public virtual IEnumerable<string> EnumerateFiles(string directory, bool recursive) =>
            Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly) : Array.Empty<string>();

        public virtual IEnumerable<string> EnumerateDirectories(string directory) =>
            Directory.Exists(directory) ? Directory.EnumerateDirectories(directory) : Array.Empty<string>();

        public virtual long GetAvailableFreeSpace(string path)
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (GetDiskFreeSpaceEx(ExistingAncestor(path) ?? root, out var free, out _, out _)) return (long)free;
            return new DriveInfo(root).AvailableFreeSpace;
        }

        public virtual WriteProbeResult ProbeWritable(string directory)
        {
            var dir = ExistingAncestor(directory);
            if (dir == null) return new WriteProbeResult(false, "Folder does not exist: " + directory);
            var probe = Path.Combine(dir, ".lotr-write-probe-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
            try
            {
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                return new WriteProbeResult(true, "Write test passed in " + dir);
            }
            catch (UnauthorizedAccessException ex) { return new WriteProbeResult(false, "Windows refused to let this app write in " + dir + " (" + ex.Message + ")"); }
            catch (IOException ex) { return new WriteProbeResult(false, "Could not write in " + dir + " (" + ex.Message + ")"); }
            finally { try { if (File.Exists(probe)) File.Delete(probe); } catch { } }
        }

        public virtual bool HasMarkOfTheWeb(string path)
        {
            var h = CreateFileW(@"\\?\" + Path.GetFullPath(path) + ":Zone.Identifier", 0x80000000 /*GENERIC_READ*/, 7, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero);
            if (h == new IntPtr(-1)) return false;
            CloseHandle(h);
            return true;
        }

        public virtual bool ClearMarkOfTheWeb(string path)
        {
            // .NET Framework rejects ':' in paths, so the stream is deleted through Win32.
            if (DeleteFileW(@"\\?\" + Path.GetFullPath(path) + ":Zone.Identifier")) return true;
            int err = Marshal.GetLastWin32Error();
            if (err == 2 || err == 3) return false; // no such stream
            throw new IOException("Could not remove the 'downloaded from the internet' mark from " + path + ": " + new Win32Exception(err).Message);
        }

        public virtual bool SameVolume(string a, string b)
        {
            var ra = Path.GetPathRoot(Path.GetFullPath(a));
            var rb = Path.GetPathRoot(Path.GetFullPath(b));
            return string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase);
        }

        private static string ExistingAncestor(string path)
        {
            var d = Path.GetFullPath(path);
            while (d != null && !Directory.Exists(d)) d = Path.GetDirectoryName(d);
            return d;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteFileW(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeForCaller, out ulong total, out ulong totalFree);
    }
}
