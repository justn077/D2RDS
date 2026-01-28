using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Diagnostics;

namespace MultiboxLauncher;

public sealed record UpdateInfo(string Version, string DownloadUrl, string ApiDownloadUrl);

// Handles update checking and self-update flow.
public static class UpdateService
{
    private const string ReleasesUrl = "https://api.github.com/repos/justn077/D2RDS/releases/latest";

    public static string CurrentVersion
    {
        get
        {
            var version = typeof(UpdateService).Assembly.GetName().Version;
            return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    // Checks the latest GitHub release; supports private repo access via token.
    public static async Task<UpdateInfo?> CheckLatestAsync(string? token)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("D2RDS-Updater");
        if (!string.IsNullOrWhiteSpace(token))
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var json = await client.GetStringAsync(ReleasesUrl);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = tag.TrimStart('v', 'V');

        string downloadUrl = "";
        string apiDownloadUrl = "";
        string fallbackDownloadUrl = "";
        string fallbackApiUrl = "";
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(fallbackDownloadUrl))
                {
                    fallbackDownloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                    fallbackApiUrl = asset.GetProperty("url").GetString() ?? "";
                }
            }

            if (name.Contains("selfcontained", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                apiDownloadUrl = asset.GetProperty("url").GetString() ?? "";
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            downloadUrl = fallbackDownloadUrl;
            apiDownloadUrl = fallbackApiUrl;
        }

        if (string.IsNullOrWhiteSpace(downloadUrl) || string.IsNullOrWhiteSpace(version))
            return null;

        return new UpdateInfo(version, downloadUrl, apiDownloadUrl);
    }

    public static bool IsNewer(string current, string latest)
    {
        if (!Version.TryParse(current, out var cur))
            return true;
        if (!Version.TryParse(latest, out var lat))
            return true;
        return lat > cur;
    }

    // Downloads the latest zip and applies it after the app exits, then restarts the app.
    public static async Task DownloadAndInstallAsync(UpdateInfo update, string? token)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"D2RDS_Update_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, "update.zip");
        using (var client = new HttpClient())
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("D2RDS-Updater");
            if (!string.IsNullOrWhiteSpace(token))
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                if (!string.IsNullOrWhiteSpace(update.ApiDownloadUrl))
                    client.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream");
            }

            var url = !string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(update.ApiDownloadUrl)
                ? update.ApiDownloadUrl
                : update.DownloadUrl;
            var data = await client.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(zipPath, data);
        }

        var extractDir = Path.Combine(tempDir, "extract");
        ZipFile.ExtractToDirectory(zipPath, extractDir, true);

        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
        var updater = Path.Combine(tempDir, "apply-update.ps1");
        var script = $@"
$pid = {Process.GetCurrentProcess().Id}
while (Get-Process -Id $pid -ErrorAction SilentlyContinue) {{ Start-Sleep -Milliseconds 300 }}
Copy-Item -Path '{extractDir}\\*' -Destination '{appDir}' -Recurse -Force
Start-Process -FilePath '{exePath}'
";
        await File.WriteAllTextAsync(updater, script);

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{updater}\"",
            UseShellExecute = true
        });
    }
}
