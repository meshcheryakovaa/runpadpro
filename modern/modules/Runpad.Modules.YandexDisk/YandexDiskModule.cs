using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Runpad.Contracts;

namespace Runpad.Modules.YandexDisk;

public sealed class YandexDiskModule : IRunpadModule
{
    private ModuleContext context = null!;
    private DateTimeOffset? uploaded;
    private DateTimeOffset nextAttempt;
    private string? lastError;
    private string Secret => Path.Combine(context.DataDirectory, "token.dpapi");
    public Task StartAsync(ModuleContext value, CancellationToken ct) { context = value; return Task.CompletedTask; }
    public async Task<object?> InvokeAsync(string method, JsonElement payload, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return new { configured = false, error = "Windows DPAPI required." };
        if (method == "configure")
        {
            var token = payload.GetProperty("token").GetString();
            if (string.IsNullOrWhiteSpace(token)) throw new InvalidDataException("Token is empty.");
            await File.WriteAllBytesAsync(Secret, ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser), ct);
            nextAttempt = default;
            return new { configured = true };
        }
        if (method == "disconnect") { File.Delete(Secret); return new { configured = false }; }
        if (method is not ("snapshot" or "upload")) throw new InvalidOperationException("Supported: snapshot, configure, disconnect, upload.");
        var configured = File.Exists(Secret);
        if (configured && (method == "upload" || DateTimeOffset.UtcNow >= nextAttempt))
        {
            nextAttempt = DateTimeOffset.UtcNow.AddMinutes(5);
            try
            {
                var token = Encoding.UTF8.GetString(ProtectedData.Unprotect(await File.ReadAllBytesAsync(Secret, ct), null, DataProtectionScope.CurrentUser));
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("OAuth", token);
                const string folder = "app:/RunpadPro";
                using (var mkdir = await http.PutAsync("https://cloud-api.yandex.net/v1/disk/resources?path=" + Uri.EscapeDataString(folder), null, ct))
                    if (mkdir.StatusCode != HttpStatusCode.Conflict) mkdir.EnsureSuccessStatusCode();
                var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserDomainName + "\\" + Environment.UserName)))[..8];
                var remote = folder + "/" + Environment.MachineName + "-" + identity + ".json";
                using var link = await http.GetAsync("https://cloud-api.yandex.net/v1/disk/resources/upload?overwrite=true&path=" + Uri.EscapeDataString(remote), ct);
                link.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await link.Content.ReadAsStringAsync(ct));
                var href = new Uri(document.RootElement.GetProperty("href").GetString()!);
                if (href.Scheme != "https" || !(href.Host == "yandex.net" || href.Host.EndsWith(".yandex.net", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Unexpected upload host.");
                var telemetryFile = Path.GetFullPath(Path.Combine(context.DataDirectory, "..", "..", "telemetry.json"));
                var data = File.Exists(telemetryFile) ? await File.ReadAllTextAsync(telemetryFile, ct) : JsonSerializer.Serialize(new { computer = Environment.MachineName, timestamp = DateTimeOffset.UtcNow });
                using var uploader = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
                using var body = new StringContent(data, Encoding.UTF8, "application/json");
                using var result = await uploader.PutAsync(href, body, ct); result.EnsureSuccessStatusCode();
                uploaded = DateTimeOffset.UtcNow; lastError = null;
            }
            catch (Exception e) when (e is not OperationCanceledException) { lastError = e.Message; }
        }
        return new { configured, uploadedAt = uploaded, nextUploadAt = configured ? (DateTimeOffset?)nextAttempt : null, error = lastError };
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
