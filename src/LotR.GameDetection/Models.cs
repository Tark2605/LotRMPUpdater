using System;
using System.Collections.Generic;
using System.Linq;

namespace LotR.GameDetection
{
    public enum GameStore
    {
        Unknown,
        Steam,
        Epic,
        Gog,
        Xbox,
    }

    /// <summary>Where a candidate came from. Declared from most to least reliable.</summary>
    public enum DiscoverySource
    {
        RunningProcess,
        Steam,
        Epic,
        Gog,
        Xbox,
        Shortcut,
        UninstallRegistry,
        DeepScan,
        Manual,
    }

    /// <summary>
    /// How the Modules write access is checked. The detection library is strictly read-only, so there is
    /// no "create a temp file" mode here: the installer (phase 3) does that right before it writes anyway.
    /// </summary>
    public enum WriteProbeMode
    {
        /// <summary>Windows AccessCheck of the folder security descriptor against the user's token. Nothing is written.</summary>
        AccessCheck,

        /// <summary>Do not check (IsWritable stays null).</summary>
        None,
    }

    public enum RejectionReason
    {
        PathNotFound,
        ShortcutBroken,
        ShortcutUnreadable,
        NoGameExecutable,
        NativeModuleMissing,
        NativeSubModuleXmlMissing,
        DedicatedServer,
        ModdingKitOnly,
    }

    /// <summary>A path reported by one discovery source, before validation.</summary>
    public sealed class Candidate
    {
        public Candidate(string path, DiscoverySource source, string detail, GameStore storeHint = GameStore.Unknown, bool claimed = true)
        {
            Path = path;
            Source = source;
            Detail = detail;
            StoreHint = storeHint;
            Claimed = claimed;
        }

        /// <summary>Any path: the exe, a bin folder, the game root, the Modules folder or an Xbox outer folder.</summary>
        public string Path { get; }
        public DiscoverySource Source { get; }

        /// <summary>Human readable origin, e.g. "HKCU\Software\Valve\Steam\SteamPath -> library D:\SteamLibrary -> appmanifest_261550.acf".</summary>
        public string Detail { get; }

        /// <summary>Store the source vouches for (Unknown when the source is store-agnostic).</summary>
        public GameStore StoreHint { get; }

        /// <summary>
        /// True when the source explicitly says "Bannerlord is here" (a rejection is then worth reporting).
        /// False for speculative probes (every Xbox folder, every Epic entry...), dropped silently when invalid.
        /// </summary>
        public bool Claimed { get; }

        public override string ToString() => $"{Source}: {Path} ({Detail})";
    }

    /// <summary>One way an installation was found.</summary>
    public sealed class DiscoveryRecord
    {
        public DiscoveryRecord(DiscoverySource source, string detail, string path, GameStore storeHint)
        {
            Source = source;
            Detail = detail;
            Path = path;
            StoreHint = storeHint;
        }

        public DiscoverySource Source { get; }
        public string Detail { get; }
        public string Path { get; }
        public GameStore StoreHint { get; }
    }

    /// <summary>A validated Bannerlord client installation.</summary>
    public sealed class GameInstallation
    {
        /// <summary>Game root as first discovered (what the player recognises), e.g. "...\steamapps\common\Mount &amp; Blade II Bannerlord".</summary>
        public string RootPath { get; set; }

        /// <summary>Root with junctions/symlinks followed and 8.3 names expanded; used for de-duplication.</summary>
        public string CanonicalRootPath { get; set; }

        /// <summary>Folder where mods are installed: &lt;root&gt;\Modules.</summary>
        public string ModulesPath { get; set; }

        public string ExecutablePath { get; set; }

        /// <summary>"Win64_Shipping_Client" (Steam/Epic/GOG) or "Gaming.Desktop.x64_Shipping_Client" (Xbox PC).</summary>
        public string BinFolderName { get; set; }

        public GameStore Store { get; set; }
        public string StoreEvidence { get; set; }

        /// <summary>E.g. "v1.4.8"; null when no version file could be read.</summary>
        public string GameVersion { get; set; }
        public string GameVersionSource { get; set; }

        /// <summary>Null when not checked.</summary>
        public bool? IsWritable { get; set; }
        public WriteProbeMode WriteCheckMode { get; set; }
        public string WriteCheckDetail { get; set; }

        /// <summary>True for Xbox installs inside the protected WindowsApps folder.</summary>
        public bool IsProtectedLocation { get; set; }

        public List<string> Warnings { get; } = new List<string>();
        public List<DiscoveryRecord> Discoveries { get; } = new List<DiscoveryRecord>();

        /// <summary>Most reliable source that found this installation.</summary>
        public DiscoverySource PrimarySource => Discoveries.Count == 0 ? DiscoverySource.Manual : Discoveries.Min(d => d.Source);

        public bool FoundBy(DiscoverySource source) => Discoveries.Any(d => d.Source == source);

        public override string ToString() => $"{Store} {GameVersion} {ModulesPath} (writable={IsWritable}, via {PrimarySource})";
    }

    /// <summary>A path a source claimed to be Bannerlord, but which failed validation.</summary>
    public sealed class RejectedCandidate
    {
        public RejectedCandidate(string path, DiscoverySource source, string detail, RejectionReason reason, string message)
        {
            Path = path;
            Source = source;
            Detail = detail;
            Reason = reason;
            Message = message;
        }

        public string Path { get; }
        public DiscoverySource Source { get; }
        public string Detail { get; }
        public RejectionReason Reason { get; }

        /// <summary>Plain-English explanation suitable for the player.</summary>
        public string Message { get; }

        public override string ToString() => $"{Source}: {Path} rejected ({Reason}) {Message}";
    }

    public sealed class SourceError
    {
        public SourceError(DiscoverySource source, string message)
        {
            Source = source;
            Message = message;
        }

        public DiscoverySource Source { get; }
        public string Message { get; }
    }

    public sealed class DetectionOptions
    {
        public WriteProbeMode WriteProbe { get; set; } = WriteProbeMode.AccessCheck;

        /// <summary>Opt-in slow scan of fixed drives (common game roots only, depth limited).</summary>
        public bool DeepScan { get; set; }

        public int DeepScanMaxDepth { get; set; } = 4;

        /// <summary>Safety cap on the number of directories the deep scan may visit.</summary>
        public int DeepScanMaxDirectories { get; set; } = 200000;

        /// <summary>Sources to skip (e.g. RunningProcess in tests).</summary>
        public HashSet<DiscoverySource> DisabledSources { get; } = new HashSet<DiscoverySource>();
    }

    public sealed class DetectionReport
    {
        public List<GameInstallation> Installations { get; } = new List<GameInstallation>();
        public List<RejectedCandidate> Rejected { get; } = new List<RejectedCandidate>();

        /// <summary>Informational findings (store shortcuts, Steam lists the app but no folder...).</summary>
        public List<string> Hints { get; } = new List<string>();
        public List<SourceError> SourceErrors { get; } = new List<SourceError>();
        public List<DiscoverySource> SourcesRun { get; } = new List<DiscoverySource>();
        public TimeSpan Duration { get; set; }

        public bool Found => Installations.Count > 0;

        /// <summary>One paragraph for the player.</summary>
        public string Summary
        {
            get
            {
                if (!Found)
                {
                    var msg = "Bannerlord was not found on this PC. Start the game once from its launcher, or choose its folder manually (the folder that contains 'bin' and 'Modules').";
                    if (Rejected.Count > 0)
                        msg += " Some locations looked like Bannerlord but were rejected: " + string.Join("; ", Rejected.Select(r => r.Path + " (" + r.Message + ")"));
                    return msg;
                }
                if (Installations.Count == 1)
                {
                    var i = Installations[0];
                    return $"Found Bannerlord {i.GameVersion ?? "(unknown version)"} ({i.Store}) - mods go to {i.ModulesPath}.";
                }
                return $"Found {Installations.Count} Bannerlord installations: " + string.Join("; ", Installations.Select(i => $"{i.Store} {i.GameVersion} at {i.RootPath}"));
            }
        }
    }
}
