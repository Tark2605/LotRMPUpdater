using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Lotr.Security;

/// <summary>Time source + sleeping, injectable so tests never really wait.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new SystemClock();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, ct);
}

public interface ILog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

public sealed class ConsoleLog : ILog
{
    public void Info(string message) => Console.WriteLine(message);
    public void Warn(string message) => Console.WriteLine("WARNING: " + message);
    public void Error(string message) => Console.Error.WriteLine("ERROR: " + message);
}

public sealed class MemoryLog : ILog
{
    public readonly List<string> Lines = new List<string>();
    public void Info(string message) { lock (Lines) Lines.Add("I " + message); }
    public void Warn(string message) { lock (Lines) Lines.Add("W " + message); }
    public void Error(string message) { lock (Lines) Lines.Add("E " + message); }
    public override string ToString() { lock (Lines) return string.Join(Environment.NewLine, Lines); }
}

/// <summary>Exit codes of lotr-security (documented in docs/SECURITY-AUTOMATION.md).</summary>
public static class ExitCodes
{
    public const int Ok = 0;              // everything scanned, or service skipped because no key is configured
    public const int Failed = 1;          // at least one file ended in error (others may be done)
    public const int Usage = 2;           // bad arguments / missing key with --require-keys
    public const int Incomplete = 3;      // quota reached or analysis still pending: run the same command again later
    public const int AuthOrConfig = 4;    // API key rejected (401/403) or unusable configuration
}

/// <summary>Thrown when the API rejects the key; aborts the whole run (no point continuing).</summary>
public sealed class ApiAuthException : Exception
{
    public ApiAuthException(string message) : base(message) { }
}

/// <summary>Thrown when the daily quota is exhausted (locally counted or reported by the API). Resumable.</summary>
public sealed class QuotaExhaustedException : Exception
{
    public QuotaExhaustedException(string message) : base(message) { }
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static T Read<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), Options);
    }

    /// <summary>Atomic write (temp file + replace) so a crash never leaves a half-written state file.</summary>
    public static void Write<T>(string path, T value)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        string text = JsonSerializer.Serialize(value, Options).Replace("\r\n", "\n") + "\n";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    public static string Str(this JsonElement e, string name)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
        }
        return null;
    }

    public static long? Long(this JsonElement e, string name)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l)) return l;
            if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out l)) return l;
        }
        return null;
    }

    public static bool TryGet(this JsonElement e, string name, out JsonElement value)
    {
        value = default;
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;
    }
}

internal static class Hashing
{
    public static string Sha256File(string path)
    {
        using (var sha = System.Security.Cryptography.SHA256.Create())
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
        {
            return ToHex(sha.ComputeHash(fs));
        }
    }

    public static string Sha256Text(string text)
    {
        using (var sha = System.Security.Cryptography.SHA256.Create())
            return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
    }

    public static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    public static bool IsSha256(string s)
    {
        if (s == null || s.Length != 64) return false;
        foreach (char c in s) if (!Uri.IsHexDigit(c)) return false;
        return true;
    }
}
