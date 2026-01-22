using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Diagnostics;

namespace MultiboxLauncher;

public sealed record UpdateInfo(string Version, string DownloadUrl);

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
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (name.Contains("selfcontained", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(downloadUrl) || string.IsNullOrWhiteSpace(version))
            return null;

        return new UpdateInfo(version, downloadUrl);
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
    public static async Task DownloadAndInstallAsync(UpdateInfo update)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"D2RDS_Update_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, "update.zip");
        using (var client = new HttpClient())
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("D2RDS-Updater");
            var data = await client.GetByteArrayAsync(update.DownloadUrl);
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
