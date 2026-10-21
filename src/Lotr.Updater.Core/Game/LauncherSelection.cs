using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using LotR.Releases.Model;
using Lotr.Updater.Core.Model;

namespace Lotr.Updater.Core.Game
{
    /// <summary>One module to tick in the launcher's Multiplayer tab (SubModule.xml Id + Version).</summary>
    public sealed class LauncherModule
    {
        public LauncherModule(string id, string version, string displayName = null)
        {
            Id = id;
            Version = version;
            DisplayName = string.IsNullOrEmpty(displayName) ? id : displayName;
        }

        public string Id { get; }
        public string Version { get; }
        public string DisplayName { get; }
    }

    public enum LauncherSelectionOutcome
    {
        /// <summary>The file was written (a backup was made first, or the file did not exist).</summary>
        Changed,
        /// <summary>Everything was already ticked in a valid order: nothing written.</summary>
        AlreadyTicked,
        /// <summary>Refused before touching anything (launcher/game running, unreadable file, nothing to do).</summary>
        Refused,
        /// <summary>Writing failed; the original file is unchanged (or restored).</summary>
        Failed,
    }

    /// <summary>Lets the player put the launcher's list back the way it was before one "Tick the mods" click.</summary>
    public sealed class LauncherSelectionUndo
    {
        public string FilePath { get; set; }
        /// <summary>Copy of the file taken before writing; null when the file did not exist (undo then removes the file we created).</summary>
        public string BackupPath { get; set; }
        /// <summary>SHA-256 of what we wrote: undo refuses when the launcher has saved its list since (it would lose those changes).</summary>
        public string WrittenSha256 { get; set; }
    }

    public sealed class LauncherSelectionResult
    {
        public LauncherSelectionOutcome Outcome { get; set; }
        /// <summary>One sentence for the player.</summary>
        public string Message { get; set; }
        public string FilePath { get; set; }
        public string BackupPath { get; set; }
        public bool CreatedFile { get; set; }
        /// <summary>Set when <see cref="Outcome"/> is Changed.</summary>
        public LauncherSelectionUndo Undo { get; set; }
        /// <summary>Other entries that look like an older LotR module (left untouched).</summary>
        public List<string> OtherLotREntries { get; } = new List<string>();
        public bool Ok => Outcome == LauncherSelectionOutcome.Changed || Outcome == LauncherSelectionOutcome.AlreadyTicked;
    }

    /// <summary>
    /// Opt-in edit of the official launcher's mod list (Documents\Mount and Blade II Bannerlord\Configs\LauncherData.xml).
    ///
    /// Format (decompiled TaleWorlds.MountAndBlade.Launcher.Library, UserDatas\UserDataManager.cs / UserData.cs): the launcher
    /// XmlSerializer-serializes UserData: &lt;UserData&gt;&lt;GameType/&gt;&lt;SingleplayerData&gt;&lt;ModDatas&gt;&lt;UserModData&gt;&lt;Id/&gt;
    /// &lt;LastKnownVersion/&gt;&lt;IsSelected&gt;true|false&lt;/IsSelected&gt;…&lt;/ModDatas&gt;&lt;/SingleplayerData&gt;&lt;MultiplayerData&gt;…&lt;/MultiplayerData&gt;
    /// &lt;DLLCheckData/&gt;&lt;/UserData&gt;. LauncherModsVM.LoadSubModules lists the ModDatas entries in file order (unknown Ids and
    /// duplicates skipped), then the modules not listed, then MBMath.TopologySort (order-preserving depth-first) pulls each
    /// module's DependedModules ahead of it. An entry's IsSelected is honoured (official required modules are always ticked).
    /// The launcher rewrites the whole file when the player switches tab or presses Play, so editing it while the launcher is
    /// open would be overwritten: refused.
    ///
    /// Only MultiplayerData/ModDatas is changed, only our own Ids are added/ticked/moved, everything else in the file (single
    /// player list, DLL check data, other mods, older LotR Ids) is kept byte-for-byte equivalent through XDocument.
    /// </summary>
    public sealed class LauncherSelection
    {
        public const string FileName = "LauncherData.xml";
        public const string BackupMarker = ".lotr-backup-";
        public const int BackupsKept = 5;

        /// <summary>Official module Ids that can appear in the Multiplayer list; our modules go after the last of them.</summary>
        public static readonly string[] OfficialModuleIds =
        {
            "Native", "SandBoxCore", "Sandbox", "SandBox", "CustomBattle", "StoryMode", "BirthAndDeath", "Multiplayer",
            "DedicatedCustomServerHelper", "NavalDLC",
        };

        /// <summary>Older Ids of the LotR multiplayer mod: never touched, only reported when ticked.</summary>
        public static readonly string[] LegacyLotRIds = { "LotrMpEvent", "LotRMPEvent", "LOTRMPEvent", "LotrMPEvent" };

        private readonly IProcessProbe _probe;
        private readonly Func<DateTime> _now;

        public LauncherSelection(string filePath, IProcessProbe probe, Func<DateTime> now = null)
        {
            FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
            _probe = probe;
            _now = now ?? (() => DateTime.Now);
        }

        public string FilePath { get; }

        /// <summary>
        /// Where the launcher keeps its list: Environment.SpecialFolder.Personal + \Mount and Blade II Bannerlord\Configs\ (exactly
        /// what UserDataManager's constructor does, for every store). On .NET Framework SpecialFolder.Personal is
        /// SHGetFolderPath(CSIDL_PERSONAL), i.e. the shell's Documents known folder, so a Documents folder moved to OneDrive or
        /// another drive is followed the same way the launcher follows it.
        /// </summary>
        public static string DefaultPath()
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            if (string.IsNullOrEmpty(docs)) return null;
            return Path.Combine(docs, "Mount and Blade II Bannerlord", "Configs", FileName);
        }

        /// <summary>The modules a player ticks for a release: core first, then the rest, admin tools left out.</summary>
        public static List<LauncherModule> ModulesFor(ReleaseManifest m)
        {
            var list = new List<LauncherModule>();
            if (m == null) return list;
            foreach (var x in m.Modules
                         .Where(x => !string.Equals(x.Role, ModuleRoles.AdminClient, StringComparison.Ordinal) && !string.IsNullOrEmpty(x.Id))
                         .OrderBy(x => string.Equals(x.Role, ModuleRoles.Core, StringComparison.Ordinal) ? 0 : 1))
                list.Add(new LauncherModule(x.Id, x.Version, x.DisplayName()));
            return list;
        }

        // ------------------------------------------------------------------ inspect

        /// <summary>Read-only: are our modules ticked in a valid order? Never writes.</summary>
        public LauncherSelectionResult Inspect(IReadOnlyList<LauncherModule> mods)
        {
            var r = new LauncherSelectionResult { FilePath = FilePath };
            if (mods == null || mods.Count == 0) return Refuse(r, "There is nothing to tick yet: install the mod first.");
            if (!File.Exists(FilePath))
            {
                r.Outcome = LauncherSelectionOutcome.Refused;
                r.Message = "The launcher has no saved mod list yet, so " + Names(mods) + " are not ticked.";
                return r;
            }
            if (!TryLoad(r, out var doc)) return r;
            var md = ModDatas(doc, create: false);
            ReportLegacy(md, r);
            if (IsSatisfied(md, mods))
            {
                r.Outcome = LauncherSelectionOutcome.AlreadyTicked;
                r.Message = Names(mods) + " are ticked in the launcher's Multiplayer tab.";
            }
            else
            {
                r.Outcome = LauncherSelectionOutcome.Refused;
                r.Message = Names(mods) + " are not all ticked in the launcher's Multiplayer tab yet.";
            }
            return r;
        }

        // ------------------------------------------------------------------ apply

        /// <summary>Ticks <paramref name="mods"/> (in that order) in the Multiplayer list. Backup first, atomic write, refuses
        /// while Bannerlord or its launcher runs or when the file cannot be read as the launcher's format.</summary>
        public LauncherSelectionResult Apply(IReadOnlyList<LauncherModule> mods)
        {
            var r = new LauncherSelectionResult { FilePath = FilePath };
            if (mods == null || mods.Count == 0) return Refuse(r, "There is nothing to tick yet: install the mod first.");
            var running = Running();
            if (running != null) return Refuse(r, running);

            XDocument doc;
            bool exists = File.Exists(FilePath);
            bool empty = exists && SafeLength(FilePath) == 0;
            if (exists && !empty)
            {
                if (!TryLoad(r, out doc)) return r;
            }
            else
            {
                // Same document the launcher writes for a new UserData; GameType Multiplayer so it opens on the right tab.
                doc = new XDocument(new XDeclaration("1.0", "utf-8", null),
                    new XElement("UserData",
                        new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
                        new XAttribute(XNamespace.Xmlns + "xsd", "http://www.w3.org/2001/XMLSchema"),
                        new XElement("GameType", "Multiplayer"),
                        new XElement("SingleplayerData", new XElement("ModDatas")),
                        new XElement("MultiplayerData", new XElement("ModDatas")),
                        new XElement("DLLCheckData", new XElement("DLLData"))));
            }

            var md = ModDatas(doc, create: true);
            ReportLegacy(md, r);
            if (IsSatisfied(md, mods))
            {
                r.Outcome = LauncherSelectionOutcome.AlreadyTicked;
                r.Message = Names(mods) + " are already ticked in the launcher's Multiplayer tab. Nothing was changed.";
                AppendLegacyAdvice(r);
                return r;
            }

            Fix(md, mods);

            var bytes = Serialize(doc);
            var undo = new LauncherSelectionUndo { FilePath = FilePath, WrittenSha256 = Sha256(bytes) };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                if (exists)
                {
                    undo.BackupPath = NewBackupPath();
                    File.Copy(FilePath, undo.BackupPath, overwrite: false);
                }
                WriteAtomic(FilePath, bytes);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is System.Security.SecurityException)
            {
                r.Outcome = LauncherSelectionOutcome.Failed;
                r.Message = "Could not save the launcher's mod list (" + ex.Message + "). Nothing was changed: tick " + Names(mods) + " yourself in the launcher's Multiplayer tab.";
                return r;
            }
            PruneBackups();
            r.Outcome = LauncherSelectionOutcome.Changed;
            r.CreatedFile = !exists;
            r.BackupPath = undo.BackupPath;
            r.Undo = undo;
            r.Message = "Done: " + Names(mods) + " are ticked in the launcher's Multiplayer tab." +
                        (undo.BackupPath != null ? " The previous list is saved as " + Path.GetFileName(undo.BackupPath) + "." : "");
            AppendLegacyAdvice(r);
            return r;
        }

        // ------------------------------------------------------------------ undo

        /// <summary>Puts the file back as it was before <paramref name="undo"/>'s click. Refused when the launcher runs, or when the
        /// launcher saved its list since (restoring would silently lose the player's later choices).</summary>
        public LauncherSelectionResult Undo(LauncherSelectionUndo undo)
        {
            var r = new LauncherSelectionResult { FilePath = FilePath };
            if (undo == null || !string.Equals(undo.FilePath, FilePath, StringComparison.OrdinalIgnoreCase)) return Refuse(r, "There is nothing to undo.");
            var running = Running();
            if (running != null) return Refuse(r, running);
            try
            {
                if (!File.Exists(FilePath) || !string.Equals(Sha256(File.ReadAllBytes(FilePath)), undo.WrittenSha256, StringComparison.Ordinal))
                    return Refuse(r, "The launcher has saved its mod list since, so it was not put back automatically (that would lose your later changes)." +
                                     (undo.BackupPath != null ? " The old list is still in " + undo.BackupPath + "." : ""));
                if (undo.BackupPath == null)
                {
                    // The file did not exist before: remove the one we created (the launcher creates a fresh one).
                    File.Delete(FilePath);
                    r.Outcome = LauncherSelectionOutcome.Changed;
                    r.Message = "Undone: the launcher's mod list is back to how it was (it had none saved).";
                    return r;
                }
                if (!File.Exists(undo.BackupPath)) return Refuse(r, "The backup " + undo.BackupPath + " is gone, so nothing was changed.");
                WriteAtomic(FilePath, File.ReadAllBytes(undo.BackupPath));
                r.Outcome = LauncherSelectionOutcome.Changed;
                r.BackupPath = undo.BackupPath;
                r.Message = "Undone: the launcher's mod list is back to how it was. The backup stays in " + Path.GetFileName(undo.BackupPath) + ".";
                return r;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
            {
                r.Outcome = LauncherSelectionOutcome.Failed;
                r.Message = "Could not undo (" + ex.Message + ").";
                return r;
            }
        }

        // ------------------------------------------------------------------ internals

        private string Running()
        {
            IReadOnlyList<RunningProcessInfo> ps;
            try { ps = _probe?.RunningGameProcesses() ?? new List<RunningProcessInfo>(); }
            catch (Exception) { ps = new List<RunningProcessInfo>(); }
            if (ps.Count == 0) return null;
            return "Close the Bannerlord launcher and the game first (" + string.Join(", ", ps.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase)) +
                   " is running): the launcher saves its own list when it closes and would undo the change. Nothing was changed.";
        }

        private static LauncherSelectionResult Refuse(LauncherSelectionResult r, string message)
        {
            r.Outcome = LauncherSelectionOutcome.Refused;
            r.Message = message;
            return r;
        }

        private bool TryLoad(LauncherSelectionResult r, out XDocument doc)
        {
            doc = null;
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using (var s = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var x = XmlReader.Create(s, settings))
                    doc = XDocument.Load(x, LoadOptions.PreserveWhitespace);
            }
            catch (Exception ex) when (ex is XmlException || ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
            {
                Refuse(r, "The launcher's settings file could not be read (" + ex.Message + "), so it was left as it is. Tick the mods yourself in the launcher's Multiplayer tab.");
                return false;
            }
            if (doc.Root == null || doc.Root.Name.LocalName != "UserData" || doc.Root.Name.Namespace != XNamespace.None)
            {
                Refuse(r, "The launcher's settings file is not in the format this app knows, so it was left as it is. Tick the mods yourself in the launcher's Multiplayer tab.");
                doc = null;
                return false;
            }
            var mp = doc.Root.Element("MultiplayerData");
            var md = mp?.Element("ModDatas");
            if (md != null && md.Elements().Any(e => e.Name.LocalName != "UserModData"))
            {
                Refuse(r, "The launcher's Multiplayer list contains something this app does not know, so it was left as it is. Tick the mods yourself in the launcher.");
                doc = null;
                return false;
            }
            return true;
        }

        private static long SafeLength(string path)
        {
            try { return new FileInfo(path).Length; } catch (Exception) { return -1; }
        }

        private static XElement ModDatas(XDocument doc, bool create)
        {
            var root = doc.Root;
            var mp = root.Element("MultiplayerData");
            if (mp == null)
            {
                if (!create) return null;
                mp = new XElement("MultiplayerData");
                // XmlSerializer order: GameType, SingleplayerData, MultiplayerData, DLLCheckData.
                var anchor = root.Element("SingleplayerData") ?? root.Element("GameType");
                if (anchor != null) anchor.AddAfterSelf(mp);
                else root.AddFirst(mp);
            }
            var md = mp.Element("ModDatas");
            if (md == null)
            {
                if (!create) return null;
                md = new XElement("ModDatas");
                mp.AddFirst(md);
            }
            return md;
        }

        private static string IdOf(XElement e) => (string)e.Element("Id");

        private static bool IsTicked(XElement e)
        {
            var v = ((string)e.Element("IsSelected"))?.Trim();
            return v == "true" || v == "1";
        }

        /// <summary>The launcher uses the first entry with a given Id (List.Find) and ignores later duplicates.</summary>
        private static List<XElement> Entries(XElement md) => md == null ? new List<XElement>() : md.Elements("UserModData").ToList();

        internal static bool IsSatisfied(XElement md, IReadOnlyList<LauncherModule> mods)
        {
            var entries = Entries(md);
            int last = -1;
            foreach (var m in mods)
            {
                var matches = entries.Select((e, i) => new { e, i }).Where(x => IdOf(x.e) == m.Id).ToList();
                if (matches.Count != 1) return false; // missing, or a duplicate we clean up
                if (!IsTicked(matches[0].e)) return false;
                if (matches[0].i < last) return false;
                last = matches[0].i;
            }
            int lastOfficial = LastOfficialIndex(entries);
            return last < 0 || entries.FindIndex(e => mods.Any(m => m.Id == IdOf(e))) > lastOfficial;
        }

        private static int LastOfficialIndex(List<XElement> entries)
        {
            int last = -1;
            for (int i = 0; i < entries.Count; i++)
                if (OfficialModuleIds.Contains(IdOf(entries[i]), StringComparer.Ordinal)) last = i;
            return last;
        }

        /// <summary>Removes our entries (keeping the first one of each, with its LastKnownVersion), then puts them back ticked, in
        /// order, where the first of them was — but never before an official module.</summary>
        internal static void Fix(XElement md, IReadOnlyList<LauncherModule> mods)
        {
            var entries = Entries(md);
            var ourIds = new HashSet<string>(mods.Select(m => m.Id), StringComparer.Ordinal);
            int firstOurs = entries.FindIndex(e => ourIds.Contains(IdOf(e)));
            var kept = new Dictionary<string, XElement>(StringComparer.Ordinal);
            foreach (var e in entries.Where(e => ourIds.Contains(IdOf(e))))
            {
                if (!kept.ContainsKey(IdOf(e))) kept[IdOf(e)] = e;
                RemoveWithWhitespace(e);
            }
            var rest = Entries(md);
            int insertAt = firstOurs < 0 ? rest.Count : entries.Take(firstOurs).Count(e => !ourIds.Contains(IdOf(e)));
            insertAt = Math.Max(insertAt, LastOfficialIndex(rest) + 1);

            var fresh = new List<XElement>();
            foreach (var m in mods)
            {
                XElement e;
                if (kept.TryGetValue(m.Id, out var old))
                {
                    e = new XElement(old);
                    var sel = e.Element("IsSelected");
                    if (sel != null) sel.Value = "true";
                    else e.Add(new XElement("IsSelected", "true"));
                }
                else
                {
                    e = new XElement("UserModData", new XElement("Id", m.Id));
                    if (!string.IsNullOrEmpty(m.Version)) e.Add(new XElement("LastKnownVersion", m.Version));
                    e.Add(new XElement("IsSelected", "true"));
                }
                fresh.Add(e);
            }
            // Whitespace inside ModDatas was kept as is; strip it so the writer re-indents consistently.
            foreach (var t in md.Nodes().OfType<XText>().ToList()) t.Remove();
            if (insertAt >= rest.Count) md.Add(fresh);
            else rest[insertAt].AddBeforeSelf(fresh);
        }

        private static void RemoveWithWhitespace(XElement e)
        {
            if (e.PreviousNode is XText t && string.IsNullOrWhiteSpace(t.Value)) t.Remove();
            e.Remove();
        }

        private static void ReportLegacy(XElement md, LauncherSelectionResult r)
        {
            foreach (var e in Entries(md))
            {
                var id = IdOf(e);
                if (id != null && LegacyLotRIds.Contains(id, StringComparer.Ordinal) && IsTicked(e) && !r.OtherLotREntries.Contains(id)) r.OtherLotREntries.Add(id);
            }
        }

        private static void AppendLegacyAdvice(LauncherSelectionResult r)
        {
            if (r.OtherLotREntries.Count > 0)
                r.Message += " An older LotR module (" + string.Join(", ", r.OtherLotREntries) + ") is also ticked; it was left as it is. Untick it in the launcher if you no longer play with it.";
        }

        private static string Names(IReadOnlyList<LauncherModule> mods)
        {
            var n = mods.Select(m => m.DisplayName).ToList();
            return n.Count == 1 ? n[0] : string.Join(", ", n.Take(n.Count - 1)) + " and " + n[n.Count - 1];
        }

        /// <summary>Same shape as the launcher's own XmlWriter (UTF-8, indented); everything else in the document is kept.</summary>
        internal static byte[] Serialize(XDocument doc)
        {
            // Re-indent: drop whitespace-only text nodes kept by PreserveWhitespace so the indenting writer can do its job.
            foreach (var t in doc.DescendantNodes().OfType<XText>().Where(t => !(t is XCData) && string.IsNullOrWhiteSpace(t.Value) && t.Parent != null && t.Parent.HasElements).ToList())
                t.Remove();
            using (var ms = new MemoryStream())
            {
                var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(true) };
                using (var w = XmlWriter.Create(ms, settings)) doc.Save(w);
                return ms.ToArray();
            }
        }

        private static void WriteAtomic(string path, byte[] bytes)
        {
            var tmp = path + ".lotr-tmp";
            File.WriteAllBytes(tmp, bytes);
            try
            {
                if (File.Exists(path)) File.Replace(tmp, path, null, ignoreMetadataErrors: true);
                else File.Move(tmp, path);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
            }
        }

        private string NewBackupPath()
        {
            var stamp = _now().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var p = FilePath + BackupMarker + stamp + ".xml";
            for (int i = 2; File.Exists(p); i++) p = FilePath + BackupMarker + stamp + "-" + i + ".xml";
            return p;
        }

        /// <summary>Backups this app made next to the file, newest first.</summary>
        public List<string> Backups()
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!Directory.Exists(dir)) return new List<string>();
                return Directory.GetFiles(dir, Path.GetFileName(FilePath) + BackupMarker + "*.xml")
                    .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return new List<string>(); }
        }

        private void PruneBackups()
        {
            foreach (var old in Backups().Skip(BackupsKept))
                try { File.Delete(old); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        private static string Sha256(byte[] b)
        {
            using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(b)).Replace("-", "").ToLowerInvariant();
        }
    }
}
