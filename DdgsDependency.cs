using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Runtime.InteropServices;

namespace Waveify;

/// <summary>
/// Installs Python and ddgs exclusively for Waveify. The official CPython
/// NuGet archive contains Python's standard library AND pip, so no system-wide
/// Python installation, PATH change, pip bootstrapper, or separate interpreter
/// installer is required. Everything is isolated with Python's -I switch.
/// </summary>
internal static class DdgsDependency
{
    internal readonly record struct PythonRuntime(string Executable);

    // Pinned stable CPython 64-bit NuGet runtime; this one archive includes pip.
    private const string PythonVersion = "3.13.15";
    private const string PackageUrl =
        "https://api.nuget.org/v3-flatcontainer/python/" + PythonVersion +
        "/python." + PythonVersion + ".nupkg";
    private const long MaxArchiveBytes = 65L * 1024 * 1024;
    private const long MaxUnpackedBytes = 240L * 1024 * 1024;
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(4)
    };
    public static string RuntimeFolder =>
        Path.Combine(DependencyInstaller.ToolsFolder, "spotify-python");
    public static string PythonExecutable => Path.Combine(RuntimeFolder, "python.exe");

    public static PythonRuntime? FindPython()
    {
        // Never fall back to the machine's Python or modify the global PATH.
        if (!File.Exists(PythonExecutable)) return null;
        var runtime = new PythonRuntime(PythonExecutable);
        var check = BuildStartInfo(runtime);
        check.ArgumentList.Add("-c");
        check.ArgumentList.Add("import sys; sys.exit(0 if sys.version_info[:2] == (3, 13) else 1)");
        return TryRunCheck(check, 2500) ? runtime : null;
    }

    public static ProcessStartInfo BuildStartInfo(PythonRuntime runtime)
    {
        var start = new ProcessStartInfo(runtime.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(runtime.Executable)!
        };
        // -I ignores registry/user site, PYTHONPATH and environment; only
        // this private interpreter and its Lib/site-packages are imported.
        start.ArgumentList.Add("-I");
        start.Environment.Remove("PYTHONPATH");
        start.Environment.Remove("PYTHONHOME");
        start.Environment.Remove("PYTHONUSERBASE");
        return start;
    }

    private static bool TryRunCheck(ProcessStartInfo start, int timeoutMilliseconds)
    {
        start.RedirectStandardOutput = false;
        start.RedirectStandardError = false;
        try
        {
            using var process = Process.Start(start);
            if (process == null) return false;
            if (!process.WaitForExit(timeoutMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public static bool IsAvailable()
    {
        var runtime = FindPython();
        if (runtime == null) return false;
        var check = BuildStartInfo(runtime.Value);
        check.ArgumentList.Add("-c");
        check.ArgumentList.Add("from ddgs import DDGS");
        return TryRunCheck(check, 4000);
    }

    public static async Task<string> InstallAsync(
        IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new NotSupportedException(
                "The bundled Spotify Python runtime currently supports Windows x64 only.");

        Directory.CreateDirectory(DependencyInstaller.ToolsFolder);
        if (IsAvailable()) return RuntimeFolder;

        var existingRuntime = FindPython();
        bool installingRuntime = existingRuntime == null;
        string stage = Path.Combine(DependencyInstaller.ToolsFolder,
            "spotify-python-stage-" + Guid.NewGuid().ToString("N"));
        string archive = Path.Combine(DependencyInstaller.ToolsFolder,
            "python-" + Guid.NewGuid().ToString("N") + ".nupkg");
        string backup = Path.Combine(DependencyInstaller.ToolsFolder,
            "spotify-python-old-" + Guid.NewGuid().ToString("N"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(7));

        try
        {
            var runtimeDir = RuntimeFolder;
            if (installingRuntime)
            {
                progress?.Report(new("ddgs", "Downloading private Python " + PythonVersion + " (with pip)"));
                await DownloadVerifiedPythonAsync(archive, progress, timeout.Token);
                progress?.Report(new("ddgs", "Extracting only the Python runtime and pip"));
                ExtractMinimalPython(archive, stage, timeout.Token);
                runtimeDir = stage;
                var pythonCheck = BuildStartInfo(new PythonRuntime(Path.Combine(stage, "python.exe")));
                pythonCheck.ArgumentList.Add("-m");
                pythonCheck.ArgumentList.Add("pip");
                pythonCheck.ArgumentList.Add("--version");
                if (!TryRunCheck(pythonCheck, 12000))
                    throw new InvalidOperationException("Downloaded Python runtime does not contain working pip.");
            }

            var python = new PythonRuntime(Path.Combine(runtimeDir, "python.exe"));
            progress?.Report(new("ddgs", "Installing ddgs and required wheels only"));
            var pip = BuildStartInfo(python);
            var sitePackages = Path.Combine(runtimeDir, "Lib", "site-packages");
            foreach (var argument in new[]
            {
                "-m", "pip", "install", "--disable-pip-version-check",
                "--no-input", "--no-cache-dir", "--only-binary=:all:",
                "--target", sitePackages, "ddgs"
            })
                pip.ArgumentList.Add(argument);
            await RunPipAsync(pip, timeout.Token);

            progress?.Report(new("ddgs", "Verifying the isolated Spotify search environment"));
            var test = BuildStartInfo(python);
            test.ArgumentList.Add("-c");
            test.ArgumentList.Add("from ddgs import DDGS");
            if (!TryRunCheck(test, 12000))
                throw new InvalidOperationException("ddgs failed to import in the private Python runtime.");

            cancellationToken.ThrowIfCancellationRequested();
            if (installingRuntime)
            {
                if (Directory.Exists(RuntimeFolder))
                    Directory.Move(RuntimeFolder, backup);
                try { Directory.Move(stage, RuntimeFolder); }
                catch
                {
                    if (Directory.Exists(backup) && !Directory.Exists(RuntimeFolder))
                        Directory.Move(backup, RuntimeFolder);
                    throw;
                }
            }

            if (!IsAvailable()) throw new InvalidOperationException(
                "The installed Python runtime failed its final ddgs check.");
            progress?.Report(new("ddgs", "Installed: private Python + ddgs"));
            return RuntimeFolder;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Python/ddgs setup timed out. Try Setup tools again.");
        }
        finally
        {
            TryDeleteFile(archive);
            TryDeleteFolder(stage);
            if (Directory.Exists(RuntimeFolder)) TryDeleteFolder(backup);
        }
    }

    private static async Task DownloadVerifiedPythonAsync(
        string destination, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        using var hashResponse = await Http.GetAsync(PackageUrl + ".sha512", token);
        hashResponse.EnsureSuccessStatusCode();
        string encoded = (await hashResponse.Content.ReadAsStringAsync(token)).Trim();
        byte[] expected;
        try { expected = Convert.FromBase64String(encoded); }
        catch (FormatException ex) { throw new InvalidOperationException("Invalid NuGet archive SHA-512 checksum.", ex); }
        if (expected.Length != 64) throw new InvalidOperationException("Invalid NuGet SHA-512 checksum length.");

        using var response = await Http.GetAsync(PackageUrl, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxArchiveBytes)
            throw new InvalidOperationException("Python package exceeds the download limit.");
        await using (var source = await response.Content.ReadAsStreamAsync(token))
        await using (var target = File.Create(destination))
        {
            byte[] buffer = new byte[128 * 1024];
            long downloaded = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, token)) > 0)
            {
                downloaded += count;
                if (downloaded > MaxArchiveBytes)
                    throw new InvalidOperationException("Python package exceeds the download limit.");
                await target.WriteAsync(buffer.AsMemory(0, count), token);
                progress?.Report(new("ddgs", "Downloading Python from NuGet",
                    downloaded, response.Content.Headers.ContentLength));
            }
        }
        progress?.Report(new("ddgs", "Verifying Python archive SHA-512"));
        await using var file = File.OpenRead(destination);
        byte[] actual = await SHA512.HashDataAsync(file, token);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new InvalidOperationException("Python archive failed SHA-512 validation.");
    }

    private static void ExtractMinimalPython(string archivePath, string destination, CancellationToken token)
    {
        Directory.CreateDirectory(destination);
        string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        long totalBytes = 0;
        int extracted = 0;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (!entry.FullName.StartsWith("tools/", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
            string relative = entry.FullName["tools/".Length..].Replace('/', Path.DirectorySeparatorChar);
            if (!IncludeRuntimeFile(relative)) continue;

            totalBytes += entry.Length;
            if (totalBytes > MaxUnpackedBytes || ++extracted > 14000)
                throw new InvalidOperationException("Python archive exceeds safe extraction limits.");
            string output = Path.GetFullPath(Path.Combine(destination, relative));
            if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Python archive contains an unsafe file path.");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            entry.ExtractToFile(output);
        }
        if (!File.Exists(Path.Combine(destination, "python.exe")) ||
            !File.Exists(Path.Combine(destination, "Lib", "site.py")))
            throw new InvalidOperationException("Python NuGet archive does not contain the runtime files.");
    }

    private static bool IncludeRuntimeFile(string relative)
    {
        string path = relative.Replace('\\', '/');
        if (path.StartsWith("Lib/test/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("Lib/idlelib/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("Lib/tkinter/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("Lib/turtledemo/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("Lib/ensurepip/", StringComparison.OrdinalIgnoreCase))
            return false;
        if (path.StartsWith("Lib/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("DLLs/", StringComparison.OrdinalIgnoreCase)) return true;
        string name = Path.GetFileName(path);
        if (path.Contains('/')) return false;
        return name.Equals("python.exe", StringComparison.OrdinalIgnoreCase) ||
            (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                (name.StartsWith("python", StringComparison.OrdinalIgnoreCase) ||
                 name.StartsWith("vcruntime", StringComparison.OrdinalIgnoreCase) ||
                 name.StartsWith("libcrypto", StringComparison.OrdinalIgnoreCase) ||
                 name.StartsWith("libssl", StringComparison.OrdinalIgnoreCase)));
    }

    private static async Task RunPipAsync(ProcessStartInfo start, CancellationToken token)
    {
        using var process = Process.Start(start) ??
            throw new InvalidOperationException("The private Python runtime could not launch pip.");
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            throw;
        }
        string output = await stdout;
        string errors = await stderr;
        if (process.ExitCode != 0)
        {
            string details = string.IsNullOrWhiteSpace(errors) ? output : errors;
            throw new InvalidOperationException("Private ddgs installation failed: " +
                details.Trim()[..Math.Min(500, details.Trim().Length)]);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteFolder(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
