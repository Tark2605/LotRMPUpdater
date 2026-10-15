using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Lotr.Security;

/// <summary>
/// API keys. Never stored in the repository. Resolution order, first hit wins:
///   1. environment variables LOTR_VT_APIKEY / LOTR_HA_APIKEY
///   2. the secrets file: --secrets &lt;path&gt;, else %LOTR_SECURITY_SECRETS%, else
///      %ProgramData%\LotRMP\secrets\security-api.json
/// File format (see tools/security/templates/security-api.example.json):
///   { "virustotal": { "apiKey": "..." }, "hybridAnalysis": { "apiKey": "..." } }
/// A missing key is not an error: the service is skipped with a message (unless --require-keys).
/// </summary>
public sealed class Secrets
{
    public const string VtEnv = "LOTR_VT_APIKEY";
    public const string HaEnv = "LOTR_HA_APIKEY";
    public const string FileEnv = "LOTR_SECURITY_SECRETS";

    public string VirusTotalKey { get; private set; }
    public string HybridAnalysisKey { get; private set; }
    public string VirusTotalSource { get; private set; } = "none";
    public string HybridAnalysisSource { get; private set; } = "none";
    public string FilePath { get; private set; }
    public System.Collections.Generic.List<string> Warnings { get; } = new System.Collections.Generic.List<string>();

    public static string DefaultFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LotRMP", "secrets", "security-api.json");

    public static Secrets Load(string explicitPath, Func<string, string> env = null)
    {
        env = env ?? Environment.GetEnvironmentVariable;
        var s = new Secrets();
        s.FilePath = !string.IsNullOrEmpty(explicitPath) ? explicitPath
                   : !string.IsNullOrEmpty(env(FileEnv)) ? env(FileEnv)
                   : DefaultFile;

        string fileVt = null, fileHa = null;
        if (File.Exists(s.FilePath))
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(s.FilePath, Encoding.UTF8)))
                {
                    JsonElement r = doc.RootElement;
                    if (r.TryGet("virustotal", out JsonElement vt)) fileVt = vt.Str("apiKey");
                    if (r.TryGet("hybridAnalysis", out JsonElement ha)) fileHa = ha.Str("apiKey");
                }
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
            {
                s.Warnings.Add($"secrets file {s.FilePath} unreadable: {ex.Message}");
            }
            s.CheckFileSafety();
        }

        s.Pick(env(VtEnv), "env " + VtEnv, fileVt, true);
        s.Pick(env(HaEnv), "env " + HaEnv, fileHa, false);
        return s;
    }

    private void Pick(string envValue, string envName, string fileValue, bool vt)
    {
        string value = null, source = "none";
        if (Usable(envValue)) { value = envValue.Trim(); source = envName; }
        else if (Usable(fileValue)) { value = fileValue.Trim(); source = "file " + FilePath; }
        if (vt) { VirusTotalKey = value; VirusTotalSource = source; }
        else { HybridAnalysisKey = value; HybridAnalysisSource = source; }
    }

    /// <summary>Placeholders from the example file count as "no key".</summary>
    private static bool Usable(string v) =>
        !string.IsNullOrWhiteSpace(v) && !v.Trim().StartsWith("<") && !v.Contains("PUT-YOUR") && !v.Contains("REPLACE");

    public static string Mask(string key) =>
        string.IsNullOrEmpty(key) ? "(none)" : key.Length <= 8 ? "****" : key.Substring(0, 4) + new string('*', 8) + key.Substring(key.Length - 4);

    /// <summary>Warns when the secrets file sits in a git working tree or is readable by ordinary users.</summary>
    private void CheckFileSafety()
    {
        try
        {
            for (var d = new FileInfo(FilePath).Directory; d != null; d = d.Parent)
            {
                if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git")))
                {
                    Warnings.Add($"secrets file {FilePath} is inside a git working tree ({d.FullName}); move it out (default {DefaultFile})");
                    break;
                }
            }
            FileSecurity acl = File.GetAccessControl(FilePath);
            var broad = new[]
            {
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            };
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>())
            {
                if (rule.AccessControlType == AccessControlType.Allow && broad.Contains(rule.IdentityReference) &&
                    (rule.FileSystemRights & FileSystemRights.ReadData) != 0)
                {
                    Warnings.Add($"secrets file {FilePath} is readable by {rule.IdentityReference.Translate(typeof(NTAccount))}; run tools/security/Initialize-SecretsFile.ps1 to lock it down");
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is IdentityNotMappedException || ex is SystemException)
        {
            // Best effort only.
        }
    }
}
