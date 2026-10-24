using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Runpad.Contracts;
using Runpad.Core;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{ Args = args, ContentRootPath = AppContext.BaseDirectory, WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot") });
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 55_000_000);
builder.Services.AddSingleton<HubStore>();
var adminToken = Environment.GetEnvironmentVariable("RUNPAD_ADMIN_TOKEN");
if (string.IsNullOrWhiteSpace(adminToken) || adminToken.Length < 32)
    throw new InvalidOperationException("Set RUNPAD_ADMIN_TOKEN to a random secret of at least 32 characters. Example: runpad token generate.");
if (builder.Configuration["urls"] is null) builder.WebHost.UseUrls("http://127.0.0.1:7090");
var app = builder.Build();
foreach (var url in (builder.Configuration["urls"] ?? "http://127.0.0.1:7090").Split(';'))
{
    // Kestrel wildcard URLs are normalized only for this configuration check.
    var normalized = url.Replace("0.0.0.0", "192.0.2.1").Replace("*", "192.0.2.1").Replace("+", "192.0.2.1").Replace("[::]", "192.0.2.1");
    Net.ValidateServer(normalized);
}
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.CacheControl = "no-store";
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        var header = context.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : "";
        var store = context.RequestServices.GetRequiredService<HubStore>();
        if (context.Request.Path.StartsWithSegments("/api/admin"))
        {
            if (!SecretEquals(token, adminToken)) { context.Response.StatusCode = 401; return; }
        }
        else if (context.Request.Path.StartsWithSegments("/api/device", out var rest))
        {
            var id = rest.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (id is null || !store.Authenticate(id, token)) { context.Response.StatusCode = 401; return; }
        }
        else { context.Response.StatusCode = 404; return; }
    }
    try { await next(); }
    catch (Exception e) when (e is InvalidDataException or JsonException or KeyNotFoundException or ArgumentException or FormatException)
    { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = e.Message }); }
});
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok(new { status = "ok", version = "0.1.0" }));
app.MapPost("/api/admin/devices", (Registration request, HubStore store) => store.Register(request));
app.MapGet("/api/admin/devices", (HubStore store) => store.List());
app.MapDelete("/api/admin/devices/{id}", (string id, HubStore store) => { store.Remove(id); return Results.NoContent(); });
app.MapPost("/api/admin/commands", (CommandSubmission request, HubStore store) => store.Enqueue(request));
app.MapGet("/api/admin/commands", (HubStore store) => store.Commands());
app.MapPost("/api/admin/files", async (HttpRequest request, HubStore store, CancellationToken ct) =>
{
    if (!request.HasFormContentType) return Results.BadRequest(new { error = "Expected multipart/form-data." });
    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("file");
    if (file is null || file.Length is < 1 or > 50_000_000) return Results.BadRequest(new { error = "File must be 1..50 MB." });
    return Results.Ok(await store.SaveFileAsync(file, ct));
});
app.MapPost("/api/device/{id}/heartbeat", (string id, DeviceHeartbeat heartbeat, HubStore store) => { store.Heartbeat(id, heartbeat); return Results.NoContent(); });
app.MapGet("/api/device/{id}/commands", (string id, string target, HubStore store) => store.Poll(id, target));
app.MapPost("/api/device/{id}/commands/{command}/ack", (string id, string command, CommandResult result, HubStore store) => { store.Ack(id, command, result); return Results.NoContent(); });
app.MapGet("/api/device/{id}/files/{file}", (string id, string file, HubStore store) =>
    store.AuthorizedFile(id, file) is { } path ? Results.File(path, "application/octet-stream") : Results.NotFound());
await app.RunAsync();

static bool SecretEquals(string a, string b) => CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));

sealed record Registration(string Name, string Classroom);
sealed record CommandSubmission(string[] DeviceIds, string Kind, JsonElement Payload);
sealed record StoredCommand(string DeviceId, DeviceCommand Command, DateTimeOffset? DeliveredAt = null, CommandResult? Result = null);
sealed record StoredDevice(string Id, string TokenHash, string Name, string Classroom,
    DateTimeOffset? UserSeenAt = null, DateTimeOffset? ServiceSeenAt = null, JsonElement Modules = default,
    string? Screen = null, DateTimeOffset? ScreenAt = null, string? User = null);
sealed record HubDatabase(StoredDevice[] Devices, StoredCommand[] Commands);

sealed class HubStore
{
    private readonly object gate = new();
    private readonly string root = Environment.GetEnvironmentVariable("RUNPAD_SERVER_DATA") ?? Path.Combine(AppContext.BaseDirectory, "data");
    private readonly Dictionary<string, StoredDevice> devices;
    private readonly List<StoredCommand> commands;
    private string Database => Path.Combine(root, "state.json");
    public HubStore()
    {
        var db = JsonFile.Read(Database, new HubDatabase([], []));
        devices = db.Devices.ToDictionary(x => x.Id); commands = db.Commands.ToList();
    }
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private void Save()
    {
        commands.RemoveAll(x => x.Command.ExpiresAt < DateTimeOffset.UtcNow.AddDays(-7));
        JsonFile.Write(Database, new HubDatabase(devices.Values.Select(d => d with { Screen = null }).ToArray(), commands.ToArray()));
    }
    public bool Authenticate(string id, string token)
    {
        lock (gate) return devices.TryGetValue(id, out var d) && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(d.TokenHash), Convert.FromHexString(Hash(token)));
    }
    public object Register(Registration registration)
    {
        if (string.IsNullOrWhiteSpace(registration.Name) || registration.Name.Length > 100 || string.IsNullOrWhiteSpace(registration.Classroom) || registration.Classroom.Length > 100)
            throw new InvalidDataException("Name and classroom must contain 1..100 characters.");
        lock (gate)
        {
            var id = Guid.NewGuid().ToString("N"); var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            devices.Add(id, new(id, Hash(token), registration.Name, registration.Classroom, Modules: Wire.Element(Array.Empty<object>())));
            Save(); return new { deviceId = id, token };
        }
    }
    public object[] List()
    {
        lock (gate) return devices.Values.Select(d => (object)new { d.Id, d.Name, d.Classroom, d.UserSeenAt, d.ServiceSeenAt,
            modules = d.Modules.ValueKind == JsonValueKind.Undefined ? Wire.Element(Array.Empty<object>()) : d.Modules,
            d.Screen, d.ScreenAt, d.User }).ToArray();
    }
    public void Remove(string id) { lock (gate) { devices.Remove(id); commands.RemoveAll(x => x.DeviceId == id); Save(); } }
    public void Heartbeat(string id, DeviceHeartbeat h)
    {
        if (h.Component is not ("user" or "system") || h.Name.Length > 100 || h.Classroom.Length > 100 || h.Modules.GetRawText().Length > 200_000 || h.Screen?.Length > 500_000)
            throw new InvalidDataException("Invalid heartbeat.");
        if (h.Screen is not null)
        {
            var bytes = Convert.FromBase64String(h.Screen);
            if (bytes.Length < 3 || bytes[0] != 0xff || bytes[1] != 0xd8) throw new InvalidDataException("Expected JPEG screenshot.");
        }
        lock (gate)
        {
            var old = devices[id]; var now = DateTimeOffset.UtcNow;
            devices[id] = h.Component == "user"
                ? old with { Name = h.Name, Classroom = h.Classroom, UserSeenAt = now, Modules = h.Modules.Clone(), Screen = h.Screen,
                    ScreenAt = h.Screen is null ? null : now, User = h.User }
                : old with { ServiceSeenAt = now };
            Save();
        }
    }
    public object Enqueue(CommandSubmission submission)
    {
        string target = submission.Kind switch { "shutdown" or "restart" or "cancel-shutdown" => "system", "message" or "open-url" or "lock" or "file" => "user", _ => throw new InvalidDataException("Unknown command.") };
        if (submission.DeviceIds.Length is < 1 or > 500 || submission.Payload.GetRawText().Length > 10_000) throw new InvalidDataException("Invalid command size.");
        // Validate before enqueueing, not only at the endpoint.
        if (submission.Kind == "open-url" && (!Uri.TryCreate(submission.Payload.GetProperty("url").GetString(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")))
            throw new InvalidDataException("Expected HTTP(S) URL.");
        if (submission.Kind == "message" && (submission.Payload.GetProperty("text").GetString()?.Length is not (>= 1 and <= 4000))) throw new InvalidDataException("Invalid message.");
        lock (gate)
        {
            if (submission.DeviceIds.Any(id => !devices.ContainsKey(id))) throw new InvalidDataException("Unknown device.");
            if (submission.Kind == "file" && !File.Exists(FilePath(submission.Payload.GetProperty("fileId").GetString()!))) throw new InvalidDataException("Unknown file.");
            var now = DateTimeOffset.UtcNow;
            var added = submission.DeviceIds.Distinct().Select(id => new StoredCommand(id,
                new(Guid.NewGuid().ToString("N"), submission.Kind, submission.Payload.Clone(), now, now.AddMinutes(5), target))).ToArray();
            commands.AddRange(added); Save(); return added.Select(c => new { c.DeviceId, c.Command.Id }).ToArray();
        }
    }
    public StoredCommand[] Commands() { lock (gate) return commands.TakeLast(200).Reverse().ToArray(); }
    public DeviceCommand[] Poll(string id, string target)
    {
        if (target is not ("user" or "system")) throw new InvalidDataException("Invalid command target.");
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            var selected = commands.Where(c => c.DeviceId == id && c.Command.Target == target && c.Result is null && c.Command.ExpiresAt > now &&
                (c.DeliveredAt is null || c.DeliveredAt < now.AddSeconds(-30))).Take(10).ToArray();
            foreach (var c in selected) commands[commands.IndexOf(c)] = c with { DeliveredAt = now };
            if (selected.Length > 0) Save();
            return selected.Select(c => c.Command).ToArray();
        }
    }
    public void Ack(string device, string command, CommandResult result)
    {
        if (result.Error?.Length > 4000) throw new InvalidDataException("Error too long.");
        lock (gate)
        {
            var index = commands.FindIndex(c => c.DeviceId == device && c.Command.Id == command);
            if (index < 0) throw new KeyNotFoundException("Command not found.");
            commands[index] = commands[index] with { Result = result }; Save();
        }
    }
    private string FilePath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid file id.");
        return Path.Combine(root, "files", id);
    }
    public async Task<object> SaveFileAsync(IFormFile upload, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N"); var path = FilePath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await upload.CopyToAsync(stream, ct);
        return new { fileId = id, name = Path.GetFileName(upload.FileName), size = upload.Length };
    }
    public string? AuthorizedFile(string device, string file)
    {
        lock (gate)
        {
            var granted = commands.Any(c => c.DeviceId == device && c.Command.Kind == "file" && c.Command.ExpiresAt > DateTimeOffset.UtcNow && c.Command.Payload.GetProperty("fileId").GetString() == file);
            return granted && File.Exists(FilePath(file)) ? FilePath(file) : null;
        }
    }
}
