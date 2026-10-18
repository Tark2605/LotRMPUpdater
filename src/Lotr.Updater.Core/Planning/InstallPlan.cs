using System;
using System.Collections.Generic;
using System.Linq;
using LotR.Releases.Chunking;
using LotR.Releases.Install;
using LotR.Releases.Model;
using Lotr.Updater.Core.Game;

namespace Lotr.Updater.Core.Planning
{
    public enum FileActionKind { Add, Replace, Remove }

    /// <summary>One line of the Plan screen, and one step of the install (built from a LotR.Releases <see cref="PlannedFile"/>).</summary>
    public sealed class PlanAction
    {
        public FileActionKind Kind { get; set; }
        /// <summary>'/'-separated, relative to the module folder.</summary>
        public string RelPath { get; set; }
        public long Size { get; set; }
        public long OldSize { get; set; }
        /// <summary>Target file (null for Remove).</summary>
        public ManifestFile Entry { get; set; }
        /// <summary>How LotR.Releases decided to obtain it: CopyLocal / Download / Rebuild.</summary>
        public FileAction Source { get; set; }
        /// <summary>CopyLocal: an existing local file with the same content.</summary>
        public string LocalSource { get; set; }
        /// <summary>CopyLocal from (or Rebuild with parts of) the player's Steam Workshop copy of the module, read-only.</summary>
        public bool FromWorkshop { get; set; }
        /// <summary>Rebuild: chunks in file order, each local (reused) or to download (Local == null).</summary>
        public List<(ChunkRef Chunk, LocalChunk Local)> Chunks { get; set; }
        public override string ToString() => Kind + " " + RelPath;
    }

    public sealed class ModulePlan
    {
        public ManifestModule Module { get; set; }
        public string TargetFolder { get; set; }
        public bool IsNewFolder { get; set; }
        public string LocalId { get; set; }
        public List<PlanAction> Actions { get; } = new List<PlanAction>();
        public int UnchangedFiles { get; set; }
        public long UnchangedBytes { get; set; }
        /// <summary>Files in the folder that no release lists: reported, never deleted (LotR.Releases rule).</summary>
        public List<string> Extra { get; } = new List<string>();
        public bool HasChanges => Actions.Count > 0;
        public int Count(FileActionKind k) => Actions.Count(a => a.Kind == k);
    }

    public sealed class DownloadItem
    {
        public string ObjectName { get; set; }
        public long ObjectSize { get; set; }
        /// <summary>Decoded size (strict decode check), -1 when unknown.</summary>
        public long Size { get; set; } = -1;
        public bool IsCore { get; set; }
        /// <summary>Bytes already in a .part file (resume).</summary>
        public long AlreadyHave { get; set; }
    }

    public enum IssueSeverity { Info, Warning, Blocker }

    public sealed class PlanIssue
    {
        public PlanIssue(IssueSeverity severity, string text) { Severity = severity; Text = text; }
        public IssueSeverity Severity { get; }
        public string Text { get; }
        public override string ToString() => Severity + ": " + Text;
    }

    /// <summary>Everything the player sees on the Plan screen before anything is changed.</summary>
    public sealed class InstallPlan
    {
        public ReleaseManifest Manifest { get; set; }
        public byte[] ManifestBytes { get; set; }
        public string SignatureStatus { get; set; }
        public GameTarget Game { get; set; }
        public List<ModulePlan> Modules { get; } = new List<ModulePlan>();
        public List<DownloadItem> Downloads { get; } = new List<DownloadItem>();
        public List<PlanIssue> Issues { get; } = new List<PlanIssue>();
        public string BackupPath { get; set; }
        public string InstalledReleaseId { get; set; }
        /// <summary>Chunk index objects of this release (kept in the cache after install, for the next update's chunk reuse).</summary>
        public List<string> ChunkIndexObjects { get; } = new List<string>();

        public long DownloadBytes => Downloads.Sum(d => d.ObjectSize - d.AlreadyHave);
        public long DownloadTotalBytes => Downloads.Sum(d => d.ObjectSize);
        /// <summary>Space needed next to the modules while staging (new and changed files).</summary>
        public long StagingBytes => Modules.SelectMany(m => m.Actions).Where(a => a.Kind != FileActionKind.Remove).Sum(a => a.Size);
        /// <summary>Old files moved to the backup folder.</summary>
        public long BackupBytes => Modules.SelectMany(m => m.Actions).Where(a => a.Kind != FileActionKind.Add).Sum(a => a.OldSize);
        public bool HasChanges => Modules.Any(m => m.HasChanges);
        public bool IsBlocked => Issues.Any(i => i.Severity == IssueSeverity.Blocker);
        public long ReusedLocalBytes { get; set; }
        public int ReusedLocalChunks { get; set; }
        /// <summary>Bytes copied from the Steam Workshop copy of a module instead of downloaded (whole files and reused parts).</summary>
        public long WorkshopReusedBytes { get; set; }
        public int WorkshopCopiedFiles { get; set; }
        public bool NeedsCoreSlot => Downloads.Any(d => d.IsCore);
    }
}
