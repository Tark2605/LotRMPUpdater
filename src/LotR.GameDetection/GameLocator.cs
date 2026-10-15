using System;
using System.Collections.Generic;
using System.Linq;

namespace LotR.GameDetection
{
    /// <summary>
    /// One Bannerlord client installation, as the updater needs it. Stable public contract (docs/CONTRACTS.md consumers):
    /// add members, never rename or remove them. Full diagnostics are in <see cref="Details"/>.
    /// </summary>
    public sealed class GameInstall
    {
        internal GameInstall(GameInstallation details)
        {
            Details = details ?? throw new ArgumentNullException(nameof(details));
        }

        /// <summary>Game root (contains bin\ and Modules\), as the player knows it (junctions not expanded).</summary>
        public string Root => Details.RootPath;

        /// <summary>&lt;Root&gt;\Modules: the only folder the updater installs into (&lt;ModulesPath&gt;\&lt;module folder&gt;).</summary>
        public string ModulesPath => Details.ModulesPath;

        public GameStore Store => Details.Store;

        /// <summary>E.g. "v1.4.8" from bin\&lt;platform&gt;\Version.xml (fallback Modules\Native\SubModule.xml); null if unreadable.</summary>
        public string GameVersion => Details.GameVersion;

        /// <summary>True / false = result of the write probe on ModulesPath; null = not checked or undetermined.</summary>
        public bool? Writable => Details.IsWritable;

        /// <summary>Most reliable discovery source (RunningProcess, Steam, Epic, Gog, Xbox, Shortcut, UninstallRegistry, DeepScan, Manual).</summary>
        public DiscoverySource Source => Details.PrimarySource;

        /// <summary>Every finding, warning and evidence string (for the live log and the support log).</summary>
        public GameInstallation Details { get; }

        public override string ToString() => Details.ToString();
    }

    /// <summary>Result of <see cref="GameLocator.FindAll"/> or <see cref="GameLocator.Validate"/>.</summary>
    public sealed class GameLocateResult
    {
        internal GameLocateResult(DetectionReport report)
        {
            Report = report;
            Installs = report.Installations.Select(i => new GameInstall(i)).ToList();
        }

        /// <summary>Most reliable first. Empty = not found (see <see cref="Message"/> and <see cref="Report"/>.Rejected).</summary>
        public IReadOnlyList<GameInstall> Installs { get; }

        public bool Found => Installs.Count > 0;

        /// <summary>One plain-English paragraph for the player (found / several copies / not found + rejection reasons).</summary>
        public string Message => Report.Summary;

        /// <summary>Full report: rejections with reasons, hints, source errors, timing. <see cref="ReportJson"/> serialises it.</summary>
        public DetectionReport Report { get; }
    }

    /// <summary>
    /// Entry point for the updater. Read-only except the Modules write probe (see <see cref="WriteProbeMode"/>).
    /// <code>
    /// var result = GameLocator.FindAll();                       // automatic search
    /// var picked = GameLocator.Validate(@"D:\Games\Bannerlord"); // folder chosen by the player
    /// </code>
    /// </summary>
    public static class GameLocator
    {
        /// <summary>Searches this PC (all sources; deep scan only when <paramref name="options"/>.DeepScan is set).</summary>
        public static GameLocateResult FindAll(DetectionOptions options = null) =>
            new GameLocateResult(GameDetector.CreateForThisPc().Detect(options));

        /// <summary>Validates a folder or file picked by the player; rejections carry a reason and a message.</summary>
        public static GameLocateResult Validate(string path, DetectionOptions options = null) =>
            new GameLocateResult(GameDetector.CreateForThisPc().ValidateManualSelection(path, options));

        /// <summary>Same as <see cref="FindAll"/> on a simulated machine (tests).</summary>
        public static GameLocateResult FindAll(Abstractions.SystemAccess system, DetectionOptions options = null) =>
            new GameLocateResult(new GameDetector(system).Detect(options));

        public static GameLocateResult Validate(Abstractions.SystemAccess system, string path, DetectionOptions options = null) =>
            new GameLocateResult(new GameDetector(system).ValidateManualSelection(path, options));
    }
}
