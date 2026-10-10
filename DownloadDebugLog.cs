using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace Waveify;

/// <summary>Thread-safe, per-download diagnostic log. Never logs environment variables or process credentials.</summary>
internal sealed class DownloadDebugLog : IDisposable
{
    private static readonly Regex SensitiveQuery = new(@"(?i)([?&](?:token|access_token|auth|authorization|key|api_key|signature|sig|expire|expires|session|cookie|password|secret|oauth)[^=&#\s]*=)[^&#\s]+", RegexOptions.Compiled);
    private static readonly Regex Authorization = new(@"(?i)(Authorization:\s*(?:Bearer|Basic)\s+)[^\s]+", RegexOptions.Compiled);
    private readonly StreamWriter writer;
    private readonly object gate = new();
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Waveify", "logs");
    public string PathName { get; }
    public DownloadDebugLog(string provider, string url, ProcessStartInfo info)
    {
        Directory.CreateDirectory(Folder);
        string safeProvider = Regex.Replace(provider, "[^A-Za-z0-9_-]", "_");
        PathName = Path.Combine(Folder, $"{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{safeProvider}_{Guid.NewGuid():N}.log");
        writer = new StreamWriter(new FileStream(PathName, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
        Write("INFO", "Started UTC " + DateTime.UtcNow.ToString("O"));
        Write("INFO", "URL: " + Redact(url));
        Write("INFO", "Executable: " + info.FileName);
        Write("INFO", "Arguments: " + string.Join(" ", info.ArgumentList.Select(Redact)));
    }
    private static string Redact(string s)
    {
        s = SensitiveQuery.Replace(s, "$1[REDACTED]");
        return Authorization.Replace(s, "$1[REDACTED]");
    }
    public void Write(string category, string message)
    {
        lock (gate) writer.WriteLine($"{DateTime.UtcNow:O} [{category}] {Redact(message)}");
    }
    public void Dispose() { lock (gate) writer.Dispose(); }
}
