using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Runpad.Contracts;

namespace Runpad.Core;

public sealed record PackageManifest(string Product, string Version, Dictionary<string, string> Files);

public static class Packages
{
    public const string Repository = "meshcheryakovaa/runpadpro";
    public static void Extract(string archive, string destination, long maxBytes = 750_000_000)
    {
        Directory.CreateDirectory(destination);
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 15000) throw new InvalidDataException("Too many package entries.");
        long total = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            total = checked(total + entry.Length);
            if (total > maxBytes) throw new InvalidDataException("Package is too large.");
            if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000) throw new InvalidDataException("Symlinks are forbidden.");
            var name = entry.FullName.TrimEnd('/');
            if (string.IsNullOrEmpty(name)) continue;
            if (name.Split('/').Any(p => p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') ||
                p.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 || IsReserved(p))) throw new InvalidDataException("Invalid package path.");
            var target = ModuleCatalog.SafeChild(destination, name);
            if (!paths.Add(target)) throw new InvalidDataException("Duplicate package path.");
            if (entry.FullName.EndsWith('/')) Directory.CreateDirectory(target);
            else { Directory.CreateDirectory(Path.GetDirectoryName(target)!); entry.ExtractToFile(target, false); }
        }
    }
    private static bool IsReserved(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
            stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '0' and <= '9';
    }
    public static PackageManifest VerifyClient(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked package root.");
        var manifest = JsonFile.Read<PackageManifest?>(Path.Combine(directory, "package.json"), null) ?? throw new InvalidDataException("Missing package manifest.");
        if (manifest.Product != "runpadpro-client" || !Version.TryParse(manifest.Version, out _)) throw new InvalidDataException("Invalid client package.");
        var actual = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(directory, p).Replace('\\', '/')).Where(p => p != "package.json").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actual.SetEquals(manifest.Files.Keys)) throw new InvalidDataException("Package file list differs from manifest.");
        foreach (var (file, hash) in manifest.Files)
        {
            var path = ModuleCatalog.SafeChild(directory, file);
            using var stream = File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Checksum mismatch: " + file);
        }
        if (!actual.Contains("app/cli/Runpad.Cli.exe") || !actual.Contains("app/shell/Runpad.Shell.exe") || !actual.Contains("app/updater/Runpad.Updater.exe"))
            throw new InvalidDataException("Incomplete client package.");
        return manifest;
    }
    public static async Task<(string Path, string Version)> DownloadReleaseAsync(string assetName, string? version, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RunpadPro/0.1.0");
        var endpoint = version is null ? "latest" : "tags/" + Uri.EscapeDataString(version.StartsWith('v') ? version : "v" + version);
        var release = await http.GetFromJsonAsync<JsonElement>($"https://api.github.com/repos/{Repository}/releases/{endpoint}", ct);
        var assets = release.GetProperty("assets").EnumerateArray().ToArray();
        string Url(string name)
        {
            var asset = assets.FirstOrDefault(x => x.GetProperty("name").GetString() == name);
            if (asset.ValueKind == JsonValueKind.Undefined) throw new FileNotFoundException("Release asset not found: " + name);
            var url = asset.GetProperty("browser_download_url").GetString()!;
            if (!url.StartsWith($"https://github.com/{Repository}/releases/download/", StringComparison.Ordinal)) throw new InvalidDataException("Unexpected release URL.");
            return url;
        }
        var checksum = (await http.GetStringAsync(Url(assetName + ".sha256"), ct)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        if (checksum.Length != 64 || !checksum.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid release checksum.");
        var path = Path.Combine(Path.GetTempPath(), "runpad-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var response = await http.GetAsync(Url(assetName), HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 750_000_000) throw new InvalidDataException("Download too large.");
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(path);
                var buffer = new byte[81920]; long length = 0; int count;
                while ((count = await source.ReadAsync(buffer, ct)) > 0)
                { length += count; if (length > 750_000_000) throw new InvalidDataException("Download too large."); await output.WriteAsync(buffer.AsMemory(0, count), ct); }
            }
            await using var file = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
            if (!actual.Equals(checksum, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Release checksum mismatch.");
            return (path, release.GetProperty("tag_name").GetString()!);
        }
        catch { File.Delete(path); throw; }
    }
    public static void CopyTree(string source, string destination)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked source directory.");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked source file.");
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        }
        foreach (var child in Directory.GetDirectories(source)) CopyTree(child, Path.Combine(destination, Path.GetFileName(child)));
    }
}
