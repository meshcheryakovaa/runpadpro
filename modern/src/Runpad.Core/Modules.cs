using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Runpad.Contracts;

namespace Runpad.Core;

public sealed partial class ModuleCatalog
{
    [GeneratedRegex("^[a-z][a-z0-9-]{0,63}$")]
    private static partial Regex IdPattern();
    public static bool ValidId(string id) => IdPattern().IsMatch(id);
    public static ModuleManifest Validate(ModuleManifest m, string directory)
    {
        if (!ValidId(m.Id)) throw new InvalidDataException("Invalid module id.");
        if (!System.Version.TryParse(m.Version, out _)) throw new InvalidDataException("Module version must be numeric, e.g. 1.0.0.");
        if (m.ApiVersion != Wire.ApiVersion) throw new InvalidDataException($"Unsupported API {m.ApiVersion}.");
        if (m.Host != "user") throw new InvalidDataException("Only user modules are supported. Modules cannot run inside the system service.");
        if (m.Kind is not ("dotnet" or "executable")) throw new InvalidDataException("Unknown module kind.");
        var entry = SafeChild(directory, m.Entry);
        if (m.Kind == "dotnet" && !entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Expected DLL entry.");
        if (!File.Exists(entry)) throw new FileNotFoundException("Module entry missing.", entry);
        if (m.Dependencies.Any(d => !ValidId(d) || d == m.Id)) throw new InvalidDataException("Invalid dependency.");
        return m;
    }
    public static string SafeChild(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains(':') || relative.Contains('\\') || Path.IsPathRooted(relative))
            throw new InvalidDataException("Expected relative forward-slash path.");
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path escapes package.");
        for (var dir = Path.GetDirectoryName(full); dir is not null && dir.Length >= fullRoot.Length; dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(dir) && (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked package directories are forbidden.");
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked package files are forbidden.");
        return full;
    }
    public IReadOnlyList<ModuleInfo> Scan()
    {
        var settings = Settings.Load();
        var found = new Dictionary<string, ModuleInfo>(StringComparer.Ordinal);
        var invalid = new List<ModuleInfo>();
        foreach (var root in new[] { Paths.BuiltinModules, Paths.Modules })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root).Where(d => !Path.GetFileName(d).StartsWith('.')))
            {
                if (!File.Exists(Path.Combine(dir, "module.ready"))) continue;
                try
                {
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked module folder.");
                    var m = Validate(JsonFile.Read(Path.Combine(dir, "module.json"), new ModuleManifest()), dir);
                    var enabled = m.Autostart && !settings.DisabledModules.Contains(m.Id);
                    found[m.Id] = new(m, dir, enabled, enabled ? "ready" : "disabled", null);
                }
                catch (Exception e)
                {
                    Log.Write("modules", $"{Path.GetFileName(dir)}: {e.Message}");
                    invalid.Add(new(new ModuleManifest { Id = Path.GetFileName(dir), Name = Path.GetFileName(dir) }, dir, false, "error", e.Message));
                }
            }
        }
        // Cycles and missing dependencies are errors, never a reason to start an incomplete graph.
        bool Ready(string id, HashSet<string> chain)
        {
            if (!found.TryGetValue(id, out var item) || !item.Enabled || !chain.Add(id)) return false;
            return item.Manifest.Dependencies.All(d => Ready(d, new(chain)));
        }
        return found.Values.Select(m => m.Enabled && !Ready(m.Manifest.Id, [])
            ? m with { State = "error", Error = "Missing, disabled or cyclic dependency." } : m).Concat(invalid).OrderBy(m => m.Manifest.Id).ToArray();
    }
}

public sealed class ModuleProcess(ModuleInfo info) : IAsyncDisposable
{
    private Process? process;
    private readonly SemaphoreSlim gate = new(1);
    public DateTimeOffset RetryAfter { get; private set; }
    public string? Error { get; private set; }
    public ModuleInfo Info => info;
    public bool Running => process is { HasExited: false };
    public async Task<RpcResponse> InvokeAsync(string method, object? payload, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!Running)
            {
                if (DateTimeOffset.UtcNow < RetryAfter) throw new IOException(Error ?? "Module restart backoff.");
                process?.Dispose();
                var entry = ModuleCatalog.SafeChild(info.Directory, info.Manifest.Entry);
                var start = new ProcessStartInfo(info.Manifest.Kind == "dotnet" ? Paths.Component("ModuleHost") : entry)
                { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = info.Directory };
                if (info.Manifest.Kind == "dotnet") start.ArgumentList.Add(info.Directory);
                start.Environment["RUNPAD_MODULE_DATA"] = Path.Combine(Paths.Data, "module-data", info.Manifest.Id);
                process = Process.Start(start) ?? throw new IOException("Cannot start module.");
                process.ErrorDataReceived += (_, e) => { if (e.Data is { } text) Log.Write("modules", $"{info.Manifest.Id}: {text}"); };
                process.BeginErrorReadLine();
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var request = new RpcRequest(Guid.NewGuid().ToString("N"), method, Wire.Element(payload));
            await process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, Wire.Json).AsMemory(), timeout.Token);
            var line = await Ipc.ReadLineAsync(process.StandardOutput, timeout.Token);
            var result = JsonSerializer.Deserialize<RpcResponse>(line, Wire.Json) ?? throw new InvalidDataException("Invalid module response.");
            if (result.Id != request.Id) throw new InvalidDataException("Module response ID mismatch.");
            Error = null;
            return result;
        }
        catch (Exception e)
        {
            Error = e is OperationCanceledException ? "Module timed out." : e.Message;
            RetryAfter = DateTimeOffset.UtcNow.AddSeconds(30);
            Stop();
            return RpcResponse.Failure("", Error);
        }
        finally { gate.Release(); }
    }
    private void Stop()
    {
        try { if (process is { HasExited: false }) process.Kill(true); } catch (InvalidOperationException) { }
        process?.Dispose(); process = null;
    }
    public ValueTask DisposeAsync() { Stop(); return ValueTask.CompletedTask; }
}

public sealed class ModuleSupervisor : IAsyncDisposable
{
    private readonly Dictionary<string, ModuleProcess> processes = [];
    private readonly ModuleCatalog catalog = new();
    private readonly SemaphoreSlim gate = new(1);
    public async Task<object> SnapshotAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var modules = catalog.Scan();
            var desired = modules.Where(m => m.Enabled && m.Error is null).ToDictionary(m => m.Manifest.Id);
            foreach (var id in processes.Keys.ToArray())
                if (!desired.TryGetValue(id, out var next) || processes[id].Info.Directory != next.Directory || processes[id].Info.Manifest.Version != next.Manifest.Version)
                { await processes[id].DisposeAsync(); processes.Remove(id); }
            foreach (var m in desired.Values)
                if (!processes.ContainsKey(m.Manifest.Id)) processes.Add(m.Manifest.Id, new(m));
            var snapshots = await Task.WhenAll(modules.Select(async m =>
            {
                RpcResponse? response = null;
                if (processes.TryGetValue(m.Manifest.Id, out var worker)) response = await worker.InvokeAsync("snapshot", null, ct);
                return new { id = m.Manifest.Id, name = m.Manifest.Name, version = m.Manifest.Version, enabled = m.Enabled,
                    state = m.Error is not null ? "error" : response is null ? "disabled" : response.Ok ? "running" : "error",
                    error = m.Error ?? response?.Error, data = response?.Result };
            }));
            return snapshots;
        }
        finally { gate.Release(); }
    }
    public async Task<RpcResponse> InvokeAsync(string id, string method, JsonElement payload, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return processes.TryGetValue(id, out var worker) ? await worker.InvokeAsync(method, payload, ct) : RpcResponse.Failure("", "Module is not running."); }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var worker in processes.Values) await worker.DisposeAsync();
        processes.Clear();
    }
}
