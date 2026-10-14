using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using LotR.GameDetection.Abstractions;

namespace LotR.GameDetection.Windows
{
    /// <summary>
    /// Real file system. Strictly read-only: no method creates, modifies or deletes anything.
    /// Paths are passed to Win32 with the \\?\ prefix so long paths (&gt; 260 chars) work on .NET Framework too.
    /// </summary>
    public sealed class WindowsFileSystem : IFileSystem
    {
        public bool FileExists(string path)
        {
            try { return path != null && File.Exists(PathUtil.ToExtended(path)); }
            catch { return false; }
        }

        public bool DirectoryExists(string path)
        {
            try { return path != null && Directory.Exists(PathUtil.ToExtended(path)); }
            catch { return false; }
        }

        public IEnumerable<string> EnumerateDirectories(string path)
        {
            try
            {
                return Directory.EnumerateDirectories(PathUtil.ToExtended(path)).Select(PathUtil.Normalize).ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        public IEnumerable<string> EnumerateFiles(string path, string searchPattern)
        {
            try
            {
                return Directory.EnumerateFiles(PathUtil.ToExtended(path), searchPattern ?? "*").Select(PathUtil.Normalize).ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        public string ReadAllText(string path) => File.ReadAllText(PathUtil.ToExtended(path));

        public byte[] ReadAllBytes(string path) => File.ReadAllBytes(PathUtil.ToExtended(path));

        public string GetCanonicalPath(string path)
        {
            var normalized = PathUtil.Normalize(path);
            if (normalized == null)
                return null;

            IntPtr h = NativeMethods.CreateFileW(PathUtil.ToExtended(normalized), NativeMethods.FILE_READ_ATTRIBUTES, NativeMethods.FILE_SHARE_ALL, IntPtr.Zero,
                NativeMethods.OPEN_EXISTING, NativeMethods.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (h == NativeMethods.INVALID_HANDLE_VALUE)
                return normalized;
            try
            {
                var sb = new StringBuilder(1024);
                uint len = NativeMethods.GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
                if (len > sb.Capacity)
                {
                    sb = new StringBuilder((int)len + 1);
                    len = NativeMethods.GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
                }
                return len == 0 ? normalized : PathUtil.Normalize(sb.ToString(0, (int)len));
            }
            finally
            {
                NativeMethods.CloseHandle(h);
            }
        }

        public bool? CheckWriteAccess(string directory, out string detail)
        {
            var target = PathUtil.ToExtended(directory);
            const uint info = NativeMethods.OWNER_SECURITY_INFORMATION | NativeMethods.GROUP_SECURITY_INFORMATION
                              | NativeMethods.DACL_SECURITY_INFORMATION | NativeMethods.LABEL_SECURITY_INFORMATION;

            NativeMethods.GetFileSecurityW(target, info, null, 0, out uint needed);
            if (needed == 0)
            {
                detail = "Could not read the folder permissions: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return null;
            }

            var sd = new byte[needed];
            if (!NativeMethods.GetFileSecurityW(target, info, sd, needed, out needed))
            {
                detail = "Could not read the folder permissions: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return null;
            }

            IntPtr processToken = IntPtr.Zero, impersonation = IntPtr.Zero;
            try
            {
                if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), NativeMethods.TOKEN_QUERY | NativeMethods.TOKEN_DUPLICATE, out processToken)
                    || !NativeMethods.DuplicateToken(processToken, NativeMethods.SecurityImpersonation, out impersonation))
                {
                    detail = "Could not read the user token: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    return null;
                }

                var mapping = new NativeMethods.GENERIC_MAPPING
                {
                    GenericRead = 0x120089,
                    GenericWrite = 0x120116,
                    GenericExecute = 0x1200A0,
                    GenericAll = 0x1F01FF,
                };
                var privileges = new byte[256];
                uint privilegesLength = (uint)privileges.Length;
                const uint desired = NativeMethods.FILE_ADD_FILE | NativeMethods.FILE_ADD_SUBDIRECTORY;

                if (!NativeMethods.AccessCheck(sd, impersonation, desired, ref mapping, privileges, ref privilegesLength, out _, out bool allowed))
                {
                    detail = "Windows could not evaluate the folder permissions: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    return null;
                }

                detail = allowed
                    ? "Windows AccessCheck: the current user may create files and folders here (nothing was written)."
                    : "Windows AccessCheck: the current user may NOT create files here without administrator rights (nothing was written).";
                return allowed;
            }
            finally
            {
                if (impersonation != IntPtr.Zero) NativeMethods.CloseHandle(impersonation);
                if (processToken != IntPtr.Zero) NativeMethods.CloseHandle(processToken);
            }
        }

        public IReadOnlyList<string> GetFixedDriveRoots()
        {
            var roots = new List<string>();
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.DriveType == DriveType.Fixed && d.IsReady)
                            roots.Add(PathUtil.Normalize(d.RootDirectory.FullName));
                    }
                    catch
                    {
                        // drive vanished or not ready: skip
                    }
                }
            }
            catch
            {
                // ignore
            }
            return roots;
        }
    }
}
