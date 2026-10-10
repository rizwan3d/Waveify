using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Waveify;

/// <summary>
/// Optional Python dependency for Spotify discovery. Installs ddgs and its
/// dependencies into Waveify's own tools folder, never into system Python.
/// Installation is called only after the user accepts the Setup tools prompt.
/// </summary>
internal static class DdgsDependency
{
    internal readonly record struct PythonRuntime(string Executable, bool UseLauncher);

    public static string PackageFolder => Path.Combine(DependencyInstaller.ToolsFolder, "python-packages");

    public static PythonRuntime? FindPython()
    {
        foreach (var runtime in new[] { new PythonRuntime("py", true), new PythonRuntime("python", false) })
        {
            var check = BuildStartInfo(runtime);
            check.ArgumentList.Add("-c");
            check.ArgumentList.Add("import sys; sys.exit(0 if sys.version_info >= (3, 10) else 1)");
            if (TryRunCheck(check, 3000)) return runtime;
        }
        return null;
    }

    public static ProcessStartInfo BuildStartInfo(PythonRuntime runtime, string? packageFolder = null)
    {
        var start = new ProcessStartInfo(runtime.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (runtime.UseLauncher) start.ArgumentList.Add("-3");
        string installed = packageFolder ?? PackageFolder;
        var existing = Environment.GetEnvironmentVariable("PYTHONPATH");
        start.Environment["PYTHONPATH"] = string.IsNullOrEmpty(existing)
            ? installed : installed + Path.PathSeparator + existing;
        return start;
    }

    private static bool TryRunCheck(ProcessStartInfo start, int timeoutMilliseconds)
    {
        // Version/import checks have no output. Don't block the UI indefinitely
        // on a broken Python installation or the Microsoft Store Python alias.
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
        catch (Win32Exception) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public static bool IsAvailable()
    {
        var python = FindPython();
        if (python == null) return false;
        var check = BuildStartInfo(python.Value);
        check.ArgumentList.Add("-c");
        check.ArgumentList.Add("from ddgs import DDGS");
        return TryRunCheck(check, 5000);
    }

    public static async Task<string> InstallAsync(
        IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var runtime = FindPython() ?? throw new InvalidOperationException(
            "Python 3.10+ is required for ddgs. Install Python from python.org, then choose Setup tools again.");

        Directory.CreateDirectory(DependencyInstaller.ToolsFolder);
        string staging = Path.Combine(DependencyInstaller.ToolsFolder, "ddgs-stage-" + Guid.NewGuid().ToString("N"));
        string backup = Path.Combine(DependencyInstaller.ToolsFolder, "ddgs-old-" + Guid.NewGuid().ToString("N"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));

        try
        {
            progress?.Report(new("ddgs", "Downloading ddgs and dependencies from PyPI"));
            var start = BuildStartInfo(runtime);
            foreach (var part in new[]
            {
                "-m", "pip", "install", "--disable-pip-version-check",
                "--no-input", "--only-binary=:all:", "--target", staging, "ddgs"
            })
                start.ArgumentList.Add(part);

            using (var process = Process.Start(start) ??
                throw new InvalidOperationException("Python could not launch pip."))
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync();
                    }
                    if (cancellationToken.IsCancellationRequested) throw;
                    throw new TimeoutException("ddgs installation timed out after 5 minutes.");
                }
                string log = await output;
                string errorLog = await errors;
                if (process.ExitCode != 0)
                {
                    string details = string.IsNullOrWhiteSpace(errorLog) ? log : errorLog;
                    details = details.Trim();
                    throw new InvalidOperationException("ddgs installation failed: " +
                        (details.Length > 500 ? details[^500..] : details));
                }
            }

            progress?.Report(new("ddgs", "Checking installed Python package"));
            var test = BuildStartInfo(runtime, staging);
            test.ArgumentList.Add("-c");
            test.ArgumentList.Add("from ddgs import DDGS");
            if (!TryRunCheck(test, 10000))
                throw new InvalidOperationException("Python could not import ddgs after installation.");

            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(PackageFolder)) Directory.Move(PackageFolder, backup);
            try { Directory.Move(staging, PackageFolder); }
            catch
            {
                if (Directory.Exists(backup) && !Directory.Exists(PackageFolder))
                    Directory.Move(backup, PackageFolder);
                throw;
            }
            progress?.Report(new("ddgs", "Installed in Waveify tools folder"));
            return PackageFolder;
        }
        finally
        {
            // Cleanup is best effort: antivirus scans can briefly hold wheel files
            // open on Windows and should not report a successful install as failed.
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try
            {
                if (Directory.Exists(backup) && Directory.Exists(PackageFolder))
                    Directory.Delete(backup, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
