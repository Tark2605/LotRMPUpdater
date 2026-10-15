using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lotr.Updater.Core.Install;
using Lotr.Updater.Core.Planning;
using Lotr.Updater.Core.SelfUpdate;

namespace Lotr.Updater.Core.Headless
{
    public sealed class HeadlessOptions
    {
        /// <summary>Check and plan only: never touches the game folder.</summary>
        public bool CheckOnly { get; set; }
        /// <summary>Read files/updater/latest.json and stage a newer updater (swapped at the next start).</summary>
        public bool SelfUpdate { get; set; }
        /// <summary>Upload the support log (the same text the Support page shows) and report the code.</summary>
        public bool SendSupport { get; set; }
    }

    /// <summary>
    /// Dev / rehearsal only (the exe accepts --headless together with --dev, i.e. a loopback server, a fake game and a
    /// temporary home): the same steps a player clicks through, without a window, written as key=value lines.
    /// </summary>
    public static class HeadlessRunner
    {
        public static async Task<int> RunAsync(UpdaterService svc, HeadlessOptions o, TextWriter report, CancellationToken ct)
        {
            bool ok = true;
            void R(string k, object v) => report.WriteLine(k + "=" + Convert.ToString(v, CultureInfo.InvariantCulture));
            R("appVersion", svc.Config.AppVersion);
            R("pendingSelfUpdate", svc.SelfUpdate.Pending?.Version ?? "none");
            if (o.SelfUpdate)
            {
                try
                {
                    var offer = await svc.SelfUpdate.CheckAsync(svc.State.Channel, svc.State.SkippedUpdaterVersion, ct).ConfigureAwait(false);
                    if (offer == null) R("selfUpdate", "none");
                    else
                    {
                        var staged = await svc.SelfUpdate.DownloadAsync(offer, null, ct).ConfigureAwait(false);
                        R("selfUpdate", "staged " + staged.Version);
                        R("selfUpdateSha256", staged.Sha256);
                        R("selfUpdateReleaseKeySigned", offer.ReleaseKeySigned);
                    }
                }
                catch (Exception ex) when (ex is SelfUpdateException || ex is Net.HttpFetchException || ex is IOException || ex is System.Net.Http.HttpRequestException)
                {
                    ok = false;
                    R("selfUpdate", "error " + ex.Message);
                }
            }

            svc.LocateGame();
            R("game", svc.Game?.Root ?? "not found");
            if (svc.Game == null) { R("result", "FAIL"); return 1; }

            var check = await svc.CheckAsync(null, ct).ConfigureAwait(false);
            if (check.Error != null)
            {
                R("check", "error " + check.Error.Replace('\n', ' '));
                ok = false;
            }
            else
            {
                var actions = check.Plan.Modules.SelectMany(m => m.Actions).ToList();
                R("check", "ok");
                R("release", check.Manifest.ReleaseId);
                R("signature", check.Signature?.Message);
                R("planAdd", actions.Count(a => a.Kind == FileActionKind.Add));
                R("planReplace", actions.Count(a => a.Kind == FileActionKind.Replace));
                R("planRemove", actions.Count(a => a.Kind == FileActionKind.Remove));
                R("planDownloadBytes", check.Plan.DownloadBytes);
                R("planDownloadObjects", check.Plan.Downloads.Count);
                R("planReusedLocalBytes", check.Plan.ReusedLocalBytes);
                R("planCoreSlot", check.Plan.NeedsCoreSlot);
                foreach (var i in check.Plan.Issues) R("issue", i.Severity + " " + i.Text);
                if (!o.CheckOnly && check.Plan.HasChanges)
                {
                    if (check.Plan.IsBlocked) { R("install", "blocked"); ok = false; }
                    else
                    {
                        try
                        {
                            await svc.InstallAsync(check.Plan, null, ct).ConfigureAwait(false);
                            R("install", "ok");
                        }
                        catch (Exception ex) when (ex is InstallException || ex is IOException || ex is UnauthorizedAccessException || ex is Net.HttpFetchException)
                        {
                            R("install", "error " + ex.Message);
                            ok = false;
                        }
                    }
                }
                else R("install", check.Plan.HasChanges ? "skipped (check only)" : "nothing to do");
                R("installed", svc.State.InstalledReleaseId ?? "none");
                if (!o.CheckOnly)
                {
                    var v = await svc.VerifyAsync(null, ct).ConfigureAwait(false);
                    R("verifyHealthy", v.Healthy);
                    R("verifyChecked", v.Checked);
                    R("verifyExtra", v.Extra.Count);
                    if (!v.Healthy) ok = false;
                }
            }

            if (o.SendSupport)
            {
                try
                {
                    var log = svc.BuildSupportLog();
                    var code = await svc.SendSupportAsync(log, ct).ConfigureAwait(false);
                    R("supportCode", code.Code);
                    R("supportExpiresAt", code.ExpiresAt);
                    R("supportLogBytes", System.Text.Encoding.UTF8.GetByteCount(log));
                }
                catch (Exception ex) when (ex is Net.HttpFetchException || ex is System.Net.Http.HttpRequestException || ex is FormatException)
                {
                    R("supportCode", "error " + ex.Message);
                    ok = false;
                }
            }
            R("result", ok ? "PASS" : "FAIL");
            return ok ? 0 : 1;
        }
    }
}
