using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Drowsy.Update;

/// <summary>A newer release on GitHub, with everything needed to install it.</summary>
internal sealed record AvailableUpdate(Version Version, string Tag, string Notes, string PageUrl, string ZipUrl, string ShaUrl, long Size);

/// <summary>
/// Self-update from GitHub Releases. The release must carry <see cref="ZipName"/> and
/// <see cref="ZipName"/>.sha256 (CI attaches both on a v* tag).
/// Installing renames the running files to *.old (Windows allows renaming files that are in use,
/// not deleting them), copies the new ones in, and restarts. *.old files are removed on the next start.
/// </summary>
internal static class Updater
{
    public const string Repository = "jamesharding/Drowsy";
    public const string ZipName = "Drowsy-win-x64.zip";
    private const string OldSuffix = ".old";

    public static Version CurrentVersion { get; } =
        typeof(Updater).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    // Created on first use (and after CurrentVersion, which its User-Agent needs). The engine
    // only pays for HttpClient if it actually checks.
    private static readonly Lazy<HttpClient> LazyHttp = new(CreateClient);
    private static HttpClient Http => LazyHttp.Value;

    /// <summary>The folder Drowsy runs from.</summary>
    public static string AppDir => AppPaths.AppDir;

    /// <summary>
    /// Only official builds (made by CI from a version tag, with -p:OfficialBuild=true) update
    /// themselves. Any other build would replace what you built with the latest release.
    /// </summary>
#if OFFICIAL_BUILD
    public static bool Enabled => true;
#else
    public static bool Enabled => false;
#endif

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Drowsy-updater", CurrentVersion.ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>The latest release if it's newer than this build; null if up to date.</summary>
    public static async Task<AvailableUpdate?> CheckAsync(CancellationToken cancel = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using HttpResponseMessage response = await Http.SendAsync(request, timeout.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
        response.EnsureSuccessStatusCode();

        GitHubRelease? release = await JsonSerializer.DeserializeAsync(
            await response.Content.ReadAsStreamAsync(timeout.Token), UpdateJsonContext.Default.GitHubRelease, timeout.Token);
        if (release is null || release.Draft || release.Prerelease) return null;
        if (!Version.TryParse(release.TagName.TrimStart('v', 'V'), out Version? version)) return null;
        version = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        if (version <= CurrentVersion) return null;

        GitHubAsset? zip = release.Assets.FirstOrDefault(a => a.Name.Equals(ZipName, StringComparison.OrdinalIgnoreCase));
        GitHubAsset? sha = release.Assets.FirstOrDefault(a => a.Name.Equals(ZipName + ".sha256", StringComparison.OrdinalIgnoreCase));
        if (zip is null || sha is null)
        {
            Log.Info($"Update {release.TagName} has no {ZipName} + .sha256; ignoring it");
            return null;
        }
        return new AvailableUpdate(version, release.TagName, release.Body ?? "", release.HtmlUrl, zip.BrowserDownloadUrl, sha.BrowserDownloadUrl, zip.Size);
    }

    /// <summary>Checks on a worker thread; calls <paramref name="found"/> (on that thread) only when there's an update.</summary>
    public static void CheckInBackground(Action<AvailableUpdate> found) => Task.Run(async () =>
    {
        try
        {
            if (await CheckAsync() is { } update) found(update);
        }
        catch (Exception ex)
        {
            Log.Info($"Update check failed: {ex.GetType().Name}: {ex.Message}"); // offline etc.; not an error
        }
    });

    /// <summary>Downloads, verifies and unpacks the update; returns the folder holding the new Drowsy.exe.</summary>
    public static async Task<string> DownloadAsync(AvailableUpdate update, IProgress<double>? progress, CancellationToken cancel = default)
    {
        string work = Path.Combine(AppPaths.LogDir, "update");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        string zipPath = Path.Combine(work, ZipName);

        string expected = (await Http.GetStringAsync(update.ShaUrl, cancel)).Trim().Split(' ', '\t')[0].ToLowerInvariant();
        if (expected.Length != 64) throw new InvalidDataException("The update's checksum file is malformed.");

        using (HttpResponseMessage response = await Http.GetAsync(update.ZipUrl, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? update.Size;
            await using Stream source = await response.Content.ReadAsStreamAsync(cancel);
            await using var target = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            byte[] buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        string actual;
        await using (FileStream fs = File.OpenRead(zipPath))
        {
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, cancel));
        }
        if (actual != expected) throw new InvalidDataException("The downloaded update doesn't match its checksum.");

        string staged = Path.Combine(work, "files");
        ZipFile.ExtractToDirectory(zipPath, staged);
        string? exe = Directory.EnumerateFiles(staged, "Drowsy.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (exe is null) throw new InvalidDataException("The update doesn't contain Drowsy.exe.");
        Log.Info($"Downloaded and verified update {update.Tag}");
        return Path.GetDirectoryName(exe)!;
    }

    /// <summary>
    /// Copies every file from <paramref name="sourceDir"/> over <paramref name="targetDir"/>, moving
    /// existing files aside to *.old first (works while they're running). All-or-nothing: on failure the
    /// old files are put back.
    /// </summary>
    public static void ReplaceFiles(string sourceDir, string targetDir)
    {
        var done = new List<(string Target, string? Backup)>();
        try
        {
            foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(targetDir, Path.GetRelativePath(sourceDir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string? backup = null;
                if (File.Exists(target))
                {
                    backup = target + OldSuffix;
                    if (File.Exists(backup) && !TryDelete(backup)) backup = $"{target}.{Guid.NewGuid():N}{OldSuffix}";
                    File.Move(target, backup);
                }
                done.Add((target, backup));
                File.Copy(file, target);
            }
        }
        catch
        {
            for (int i = done.Count - 1; i >= 0; i--)
            {
                (string target, string? backup) = done[i];
                TryDelete(target);
                if (backup is not null)
                {
                    try { File.Move(backup, target); }
                    catch (Exception ex) { Log.Error($"Rollback: could not restore {target}", ex); }
                }
            }
            throw;
        }
    }

    /// <summary>Installs a downloaded update over this copy and restarts Drowsy (engine + this window).</summary>
    public static void InstallAndRestart(string stagedDir, AvailableUpdate update)
    {
        Log.Info($"Installing update {CurrentVersion} -> {update.Version}");
        ReplaceFiles(stagedDir, AppDir);
        Restart(openSettings: true);
    }

    /// <summary>
    /// Starts <c>Drowsy.exe --restart</c> (the new exe, now on disk), asks the engine to quit, and
    /// leaves the caller to exit. The helper waits for both old processes to go, then starts the engine.
    /// </summary>
    public static void Restart(bool openSettings)
    {
        string exe = AppPaths.EngineExe;
        Process.Start(new ProcessStartInfo(exe, openSettings ? "--restart --open-settings" : "--restart") { UseShellExecute = false })?.Dispose();
        Program.RequestEngineExit();
    }

    /// <summary><c>Drowsy.exe --restart</c>: wait for the old processes to exit, then start the engine (and settings).</summary>
    public static void RunRestartHelper(bool openSettings, bool installed)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while ((Program.EngineIsRunning() || Program.SettingsWindowIsOpen()) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(200);
        }

        // The single-instance locks go a moment before the processes do, and until a process has
        // fully exited its exe stays locked, so the new engine couldn't clear away the *.old files.
        // Wait for the old copies from this folder to exit (never touching anything else), and stop
        // any that are stuck.
        int self = Environment.ProcessId;
        foreach (Process p in Process.GetProcessesByName("Drowsy"))
        {
            try
            {
                if (p.Id == self || !(p.MainModule?.FileName ?? "").StartsWith(AppDir, StringComparison.OrdinalIgnoreCase)) continue;
                int remaining = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
                if (!p.WaitForExit(remaining))
                {
                    p.Kill();
                    p.WaitForExit(5000);
                }
            }
            catch { /* already gone */ }
            finally { p.Dispose(); }
        }

        Program.LaunchEngine(installed ? "--installed" : null);
        if (openSettings) Program.LaunchSettings();
    }

    /// <summary>Removes *.old files left by a previous update or install. Best effort.</summary>
    public static void CleanUp()
    {
        try
        {
            foreach (string old in Directory.EnumerateFiles(AppDir, "*" + OldSuffix, SearchOption.AllDirectories)) TryDelete(old);
            string work = Path.Combine(AppPaths.LogDir, "update");
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
        catch
        {
            // Still in use by a process that hasn't exited; next start will get it.
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

internal sealed class GitHubRelease
{
    public string TagName { get; set; } = "";
    public string HtmlUrl { get; set; } = "";
    public string? Body { get; set; }
    public bool Draft { get; set; }
    public bool Prerelease { get; set; }
    public List<GitHubAsset> Assets { get; set; } = [];
}

internal sealed class GitHubAsset
{
    public string Name { get; set; } = "";
    public string BrowserDownloadUrl { get; set; } = "";
    public long Size { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext
{
}
