using System.Text.Json;

namespace Runpad.Contracts;

public static class Wire
{
    public const int ApiVersion = 1;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    public static JsonElement Element(object? value) => JsonSerializer.SerializeToElement(value, Json);
}

public sealed record RpcRequest(string Id, string Method, JsonElement Payload);
public sealed record RpcResponse(string Id, bool Ok, JsonElement Result, string? Error = null)
{
    public static RpcResponse Success(string id, object? value) => new(id, true, Wire.Element(value));
    public static RpcResponse Failure(string id, string error) => new(id, false, Wire.Element(null), error);
}

public sealed record ModuleContext(string Id, string Directory, string DataDirectory);

/// <summary>Only this contract assembly is shared with the module host. Never reference Runpad.Core.</summary>
public interface IRunpadModule : IAsyncDisposable
{
    Task StartAsync(ModuleContext context, CancellationToken cancellationToken);
    Task<object?> InvokeAsync(string method, JsonElement payload, CancellationToken cancellationToken);
}

public sealed record ModuleManifest
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "1.0.0";
    public int ApiVersion { get; init; } = Wire.ApiVersion;
    public string Kind { get; init; } = "dotnet";
    public string Entry { get; init; } = "";
    public string[] Permissions { get; init; } = [];
    public string[] Dependencies { get; init; } = [];
    public bool Autostart { get; init; } = true;
    public string Host { get; init; } = "user";
}

public sealed record ModuleInfo(ModuleManifest Manifest, string Directory, bool Enabled, string State, string? Error);
public sealed record LauncherApp(string Name, string Target, string Arguments = "", string Category = "Программы");
public sealed record Enrollment(string Server, string DeviceId, string Token);
public sealed record ClientSettings
{
    public string Classroom { get; init; } = "Класс";
    public int RefreshMilliseconds { get; init; } = 3000;
    public bool ShareScreens { get; init; }
    public Enrollment? Enrollment { get; init; }
    public string[] DisabledModules { get; init; } = [];
    public string? WallpaperFile { get; init; }
    public LauncherApp[] Applications { get; init; } =
    [
        new("Word", "winword.exe", Category: "Офис"),
        new("Excel", "excel.exe", Category: "Офис"),
        new("Браузер", "https://ya.ru", Category: "Интернет"),
        new("Блокнот", "notepad.exe", Category: "Инструменты")
    ];
}

public sealed record DeviceHeartbeat(string Name, string Classroom, string Component, JsonElement Modules,
    string? Screen = null, string? User = null);
public sealed record DeviceCommand(string Id, string Kind, JsonElement Payload, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, string Target);
public sealed record CommandResult(bool Ok, string? Error = null);
