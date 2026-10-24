using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace vUSB_LAB;

internal sealed record AvailableRelease(Version Version, string AssetName, Uri Download, string Sha256);

internal static class ReleaseUpdater
{
    private const string Repository = "brucemurphy/vUSB-Lab";
    private static readonly HttpClient Client = CreateClient();
    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
    public static string DisplayVersion => CurrentVersion.ToString(3);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("vUSB-Lab/" + DisplayVersion);
        return client;
    }

    public static async Task<AvailableRelease?> CheckAsync()
    {
        using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var response = await Client.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", timeout.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var release = json.RootElement;
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v'), out var version))
            throw new InvalidDataException("The latest release has an invalid version tag.");
        if (version <= new Version(CurrentVersion.Major, CurrentVersion.Minor, CurrentVersion.Build)) return null;
        string architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException("Release updates support x64 and ARM64.")
        };
        string expected = $"vUSB-Lab-{tag}-win-{architecture}-Setup.exe";
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != expected) continue;
            if (!asset.TryGetProperty("digest", out var digestProperty) ||
                digestProperty.GetString() is not string digest || !digest.StartsWith("sha256:", StringComparison.Ordinal))
                throw new InvalidDataException("This release is missing its SHA256 download digest.");
            string hash = digest[7..];
            if (hash.Length != 64 || !ContainsOnlyHex(hash.AsSpan()))
                throw new InvalidDataException("This release has an invalid SHA256 digest.");
            var url = new Uri(asset.GetProperty("browser_download_url").GetString()!);
            if (url.Scheme != Uri.UriSchemeHttps || url.Host != "github.com" ||
                !url.AbsolutePath.StartsWith("/" + Repository + "/releases/download/", StringComparison.Ordinal))
                throw new InvalidDataException("The release download is not hosted in the expected GitHub repository.");
            return new AvailableRelease(version, expected, url, hash);
        }
        throw new InvalidDataException($"The latest release does not contain {expected}.");
    }

    public static async Task<string> DownloadAsync(AvailableRelease release)
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "vUSB Lab", "Updates");
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, release.AssetName);
        string temporary = Path.Combine(directory, Guid.NewGuid() + ".tmp");
        try
        {
            using var response = await Client.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await response.Content.CopyToAsync(file);
            await using (var file = File.OpenRead(temporary))
            {
                string actual = Convert.ToHexString(await SHA256.HashDataAsync(file));
                if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The update failed SHA256 verification. It has not been installed.");
            }
            File.Move(temporary, destination, true);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void StartInstaller(string path)
    {
        var start = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true };
        start.ArgumentList.Add("/SILENT");
        start.ArgumentList.Add("/NORESTART");
        start.ArgumentList.Add("/UPDATE=1");
        start.ArgumentList.Add("/WAITPID=" + Environment.ProcessId);
        start.ArgumentList.Add("/DIR=" + AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        using var installer = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the update installer.");
    }

    private static bool ContainsOnlyHex(this ReadOnlySpan<char> value)
    {
        foreach (char character in value)
            if (!Uri.IsHexDigit(character)) return false;
        return true;
    }
}
