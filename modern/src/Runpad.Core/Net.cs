using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Runpad.Contracts;

namespace Runpad.Core;

public static class Net
{
    public static Uri ValidateServer(string server)
    {
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
            throw new InvalidDataException("Server must use HTTPS (HTTP is allowed only on loopback).");
        return new Uri(server.TrimEnd('/') + "/");
    }
    public static HttpClient Client(string server, string token)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = ValidateServer(server), Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

public sealed class HubClient(Enrollment enrollment) : IDisposable
{
    private readonly HttpClient http = Net.Client(enrollment.Server, enrollment.Token);
    public async Task HeartbeatAsync(DeviceHeartbeat heartbeat, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync($"api/device/{Uri.EscapeDataString(enrollment.DeviceId)}/heartbeat", heartbeat, Wire.Json, ct);
        response.EnsureSuccessStatusCode();
    }
    public async Task<DeviceCommand[]> PollAsync(string target, CancellationToken ct) =>
        await http.GetFromJsonAsync<DeviceCommand[]>($"api/device/{Uri.EscapeDataString(enrollment.DeviceId)}/commands?target={target}", Wire.Json, ct) ?? [];
    public async Task AckAsync(string id, CommandResult result, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync($"api/device/{Uri.EscapeDataString(enrollment.DeviceId)}/commands/{id}/ack", result, Wire.Json, ct);
        response.EnsureSuccessStatusCode();
    }
    public async Task<byte[]> DownloadAsync(string id, CancellationToken ct)
    {
        using var response = await http.GetAsync($"api/device/{Uri.EscapeDataString(enrollment.DeviceId)}/files/{Uri.EscapeDataString(id)}", ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 50_000_000) throw new IOException("File too large.");
        return await response.Content.ReadAsByteArrayAsync(ct);
    }
    public void Dispose() => http.Dispose();
}
