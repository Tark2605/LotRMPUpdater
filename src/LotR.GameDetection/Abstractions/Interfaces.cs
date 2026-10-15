using System.Collections.Generic;

namespace LotR.GameDetection.Abstractions
{
    /// <summary>Registry hive the detector is allowed to read.</summary>
    public enum RegistryHiveKind
    {
        CurrentUser,
        LocalMachine,
    }

    /// <summary>Registry view. Registry32 is the WOW6432Node view used by 32-bit launchers (Steam, GOG).</summary>
    public enum RegistryViewKind
    {
        Default,
        Registry32,
        Registry64,
    }

    /// <summary>Read-only registry access.</summary>
    public interface IRegistryReader
    {
        /// <summary>Returns the value (string, int, byte[]...) or null when the key or value does not exist.</summary>
        object GetValue(RegistryHiveKind hive, RegistryViewKind view, string keyPath, string valueName);

        /// <summary>Returns the sub key names, or an empty list when the key does not exist.</summary>
        IReadOnlyList<string> GetSubKeyNames(RegistryHiveKind hive, RegistryViewKind view, string keyPath);
    }

    /// <summary>File system access. Every method must swallow access errors (return false / empty) rather than throw.</summary>
    public interface IFileSystem
    {
        bool FileExists(string path);
        bool DirectoryExists(string path);

        /// <summary>Full paths of the sub directories; empty when missing or denied.</summary>
        IEnumerable<string> EnumerateDirectories(string path);

        /// <summary>Full paths of the files matching a simple pattern ("*.item", "*"); empty when missing or denied.</summary>
        IEnumerable<string> EnumerateFiles(string path, string searchPattern);

        /// <summary>Reads a text file (UTF-8 with BOM detection). Throws when missing.</summary>
        string ReadAllText(string path);

        /// <summary>Reads a binary file. Throws when missing.</summary>
        byte[] ReadAllBytes(string path);

        /// <summary>
        /// Final path of an existing file or directory: junctions and symbolic links followed,
        /// 8.3 short names expanded, real case restored. Returns the normalised input when it cannot be resolved.
        /// </summary>
        string GetCanonicalPath(string path);

        /// <summary>
        /// Read-only check: can the current user create files and folders in <paramref name="directory"/>?
        /// Evaluated by Windows (AccessCheck on the folder's security descriptor with the user's token, integrity label
        /// included); nothing is written. Null when it cannot be computed.
        /// </summary>
        bool? CheckWriteAccess(string directory, out string detail);

        /// <summary>
        /// Write probe: creates a uniquely named 0-byte file in <paramref name="directory"/> with delete-on-close semantics and
        /// closes it immediately, so nothing is left behind. True = created, false = denied, null = undetermined.
        /// The only method of this interface allowed to touch the disk.
        /// </summary>
        bool? ProbeWriteWithTempFile(string directory, out string detail);

        /// <summary>Roots of the fixed (non-removable, non-network) drives, e.g. "C:\".</summary>
        IReadOnlyList<string> GetFixedDriveRoots();
    }

    /// <summary>A running process and the full path of its main module.</summary>
    public sealed class RunningProcess
    {
        public RunningProcess(string name, int id, string executablePath, string error = null)
        {
            Name = name;
            Id = id;
            ExecutablePath = executablePath;
            Error = error;
        }

        public string Name { get; }
        public int Id { get; }

        /// <summary>Null when the path could not be read (see <see cref="Error"/>).</summary>
        public string ExecutablePath { get; }
        public string Error { get; }
    }

    public interface IProcessList
    {
        /// <summary>Processes whose image name (without ".exe") matches one of <paramref name="processNames"/>, case-insensitive.</summary>
        IReadOnlyList<RunningProcess> GetProcesses(IEnumerable<string> processNames);
    }

    /// <summary>Folders the detector needs. Values are null when the folder does not exist on this machine.</summary>
    public enum KnownFolderKind
    {
        UserDesktop,
        CommonDesktop,
        UserStartMenu,
        CommonStartMenu,
        ProgramData,
        ProgramFiles,
        ProgramFilesX86,
        LocalAppData,
        Documents,
    }

    public interface ISystemEnvironment
    {
        string GetFolder(KnownFolderKind kind);
        string ExpandEnvironmentVariables(string value);
    }

    /// <summary>Target of a Windows shell link (.lnk).</summary>
    public sealed class ShortcutTarget
    {
        public string TargetPath { get; set; }
        public string Arguments { get; set; }
        public string WorkingDirectory { get; set; }

        /// <summary>Target relative to the .lnk location (used when no absolute target is stored).</summary>
        public string RelativePath { get; set; }

        /// <summary>Set when the link could not be parsed.</summary>
        public string Error { get; set; }
    }

    public interface IShortcutResolver
    {
        ShortcutTarget Resolve(string shortcutPath);
    }

    /// <summary>Bundle of every system access the detector uses. Tests build one from fakes.</summary>
    public sealed class SystemAccess
    {
        public SystemAccess(IFileSystem fileSystem, IRegistryReader registry, IProcessList processes, ISystemEnvironment environment, IShortcutResolver shortcuts)
        {
            FileSystem = fileSystem;
            Registry = registry;
            Processes = processes;
            Environment = environment;
            Shortcuts = shortcuts;
        }

        public IFileSystem FileSystem { get; }
        public IRegistryReader Registry { get; }
        public IProcessList Processes { get; }
        public ISystemEnvironment Environment { get; }
        public IShortcutResolver Shortcuts { get; }

        /// <summary>The real Windows machine. Read-only except for the optional write probe.</summary>
        public static SystemAccess CreateReal()
        {
            var fs = new Windows.WindowsFileSystem();
            var env = new Windows.WindowsEnvironment();
            return new SystemAccess(fs, new Windows.WindowsRegistryReader(), new Windows.WindowsProcessList(), env, new Parsing.LnkShortcutResolver(fs, env));
        }
    }
}
