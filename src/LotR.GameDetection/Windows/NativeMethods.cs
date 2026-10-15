using System;
using System.Runtime.InteropServices;
using System.Text;

namespace LotR.GameDetection.Windows
{
    /// <summary>Win32 imports. Everything here is read-only (queries), except the delete-on-close write probe flags.</summary>
    internal static class NativeMethods
    {
        internal const uint FILE_READ_ATTRIBUTES = 0x0080;
        internal const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4;
        internal const uint OPEN_EXISTING = 3;
        internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        internal static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        internal const uint OWNER_SECURITY_INFORMATION = 0x1;
        internal const uint GROUP_SECURITY_INFORMATION = 0x2;
        internal const uint DACL_SECURITY_INFORMATION = 0x4;
        internal const uint LABEL_SECURITY_INFORMATION = 0x10;

        internal const uint TOKEN_DUPLICATE = 0x0002;
        internal const uint TOKEN_QUERY = 0x0008;
        internal const int SecurityImpersonation = 2;

        internal const uint FILE_ADD_FILE = 0x0002;
        internal const uint FILE_ADD_SUBDIRECTORY = 0x0004;

        internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        // write probe only
        internal const uint GENERIC_WRITE = 0x40000000;
        internal const uint DELETE = 0x00010000;
        internal const uint CREATE_NEW = 1;
        internal const uint FILE_ATTRIBUTE_HIDDEN = 0x2;
        internal const uint FILE_ATTRIBUTE_TEMPORARY = 0x100;
        internal const uint FILE_FLAG_DELETE_ON_CLOSE = 0x04000000;
        internal const int ERROR_ACCESS_DENIED = 5;
        internal const int ERROR_WRITE_PROTECT = 19;
        internal const int ERROR_FILE_EXISTS = 80;

        [StructLayout(LayoutKind.Sequential)]
        internal struct GENERIC_MAPPING
        {
            public uint GenericRead;
            public uint GenericWrite;
            public uint GenericExecute;
            public uint GenericAll;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetFinalPathNameByHandleW(IntPtr hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool GetFileSecurityW(string lpFileName, uint requestedInformation, byte[] pSecurityDescriptor, uint nLength, out uint lpnLengthNeeded);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool DuplicateToken(IntPtr existingTokenHandle, int impersonationLevel, out IntPtr duplicateTokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool AccessCheck(byte[] pSecurityDescriptor, IntPtr clientToken, uint desiredAccess, ref GENERIC_MAPPING genericMapping, byte[] privilegeSet, ref uint privilegeSetLength, out uint grantedAccess, out bool accessStatus);
    }
}
