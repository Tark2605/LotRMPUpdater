using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using LotR.GameDetection.Abstractions;
using Microsoft.Win32;

namespace LotR.GameDetection.Windows
{
    /// <summary>Read-only registry access (keys are opened with read rights only).</summary>
    public sealed class WindowsRegistryReader : IRegistryReader
    {
        public object GetValue(RegistryHiveKind hive, RegistryViewKind view, string keyPath, string valueName)
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(Map(hive), Map(view)))
                using (var key = baseKey.OpenSubKey(keyPath, false))
                {
                    return key?.GetValue(valueName);
                }
            }
            catch
            {
                return null;
            }
        }

        public IReadOnlyList<string> GetSubKeyNames(RegistryHiveKind hive, RegistryViewKind view, string keyPath)
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(Map(hive), Map(view)))
                using (var key = baseKey.OpenSubKey(keyPath, false))
                {
                    return key?.GetSubKeyNames() ?? Array.Empty<string>();
                }
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static RegistryHive Map(RegistryHiveKind hive) => hive == RegistryHiveKind.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;

        private static RegistryView Map(RegistryViewKind view)
        {
            switch (view)
            {
                case RegistryViewKind.Registry32: return RegistryView.Registry32;
                case RegistryViewKind.Registry64: return RegistryView.Registry64;
                default: return RegistryView.Default;
            }
        }
    }

    /// <summary>
    /// Running processes. The image path is read with QueryFullProcessImageName and the limited query right,
    /// which works across 32/64-bit and for most processes of the same user without elevation.
    /// </summary>
    public sealed class WindowsProcessList : IProcessList
    {
        public IReadOnlyList<RunningProcess> GetProcesses(IEnumerable<string> processNames)
        {
            var result = new List<RunningProcess>();
            foreach (var name in processNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Process[] processes;
                try { processes = Process.GetProcessesByName(name); }
                catch { continue; }

                foreach (var p in processes)
                {
                    using (p)
                    {
                        string path = null, error = null;
                        IntPtr h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                        if (h == IntPtr.Zero)
                        {
                            error = "cannot open process " + p.Id + " (access denied?)";
                        }
                        else
                        {
                            try
                            {
                                var sb = new StringBuilder(32768);
                                uint size = (uint)sb.Capacity;
                                if (NativeMethods.QueryFullProcessImageNameW(h, 0, sb, ref size))
                                    path = sb.ToString(0, (int)size);
                                else
                                    error = "cannot read the image path of process " + p.Id;
                            }
                            finally
                            {
                                NativeMethods.CloseHandle(h);
                            }
                        }
                        result.Add(new RunningProcess(name, p.Id, path, error));
                    }
                }
            }
            return result;
        }
    }

    public sealed class WindowsEnvironment : ISystemEnvironment
    {
        public string GetFolder(KnownFolderKind kind)
        {
            string p;
            switch (kind)
            {
                case KnownFolderKind.UserDesktop: p = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); break;
                case KnownFolderKind.CommonDesktop: p = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory); break;
                case KnownFolderKind.UserStartMenu: p = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu); break;
                case KnownFolderKind.CommonStartMenu: p = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu); break;
                case KnownFolderKind.ProgramData: p = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData); break;
                case KnownFolderKind.ProgramFiles:
                    p = Environment.GetEnvironmentVariable("ProgramW6432");
                    if (string.IsNullOrEmpty(p)) p = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                    break;
                case KnownFolderKind.ProgramFilesX86: p = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86); break;
                case KnownFolderKind.LocalAppData: p = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); break;
                case KnownFolderKind.Documents: p = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); break;
                default: p = null; break;
            }
            return string.IsNullOrEmpty(p) ? null : PathUtil.Normalize(p);
        }

        public string ExpandEnvironmentVariables(string value) => value == null ? null : Environment.ExpandEnvironmentVariables(value);
    }
}
