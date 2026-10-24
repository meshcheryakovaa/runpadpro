using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Runpad.Contracts;
using Runpad.Core;

if (args.Length != 1) { Console.Error.WriteLine("Usage: Runpad.ModuleHost <module-directory>"); return 2; }
try
{
    var directory = Path.GetFullPath(args[0]);
    var manifest = ModuleCatalog.Validate(JsonFile.Read(Path.Combine(directory, "module.json"), new ModuleManifest()), directory);
    if (manifest.Kind != "dotnet") throw new InvalidDataException("Expected a dotnet module.");
    var entry = ModuleCatalog.SafeChild(directory, manifest.Entry);
    var context = new ModuleLoadContext(entry);
    var assembly = context.LoadFromAssemblyPath(entry);
    var types = assembly.GetTypes().Where(t => !t.IsAbstract && typeof(IRunpadModule).IsAssignableFrom(t)).ToArray();
    if (types.Length != 1) throw new InvalidDataException("A module must expose exactly one IRunpadModule implementation.");
    await using var module = (IRunpadModule)Activator.CreateInstance(types[0])!;
    var data = Environment.GetEnvironmentVariable("RUNPAD_MODULE_DATA") ?? Path.Combine(Paths.Data, "module-data", manifest.Id);
    Directory.CreateDirectory(data);
    // stdout belongs exclusively to the protocol. Module diagnostics go to stderr.
    var protocol = Console.Out;
    Console.SetOut(Console.Error);
    await module.StartAsync(new(manifest.Id, directory, data), CancellationToken.None);
    while (await Console.In.ReadLineAsync() is { } line)
    {
        RpcRequest? request = null;
        RpcResponse response;
        try
        {
            if (line.Length > 1024 * 1024) throw new InvalidDataException("Request too large.");
            request = JsonSerializer.Deserialize<RpcRequest>(line, Wire.Json) ?? throw new InvalidDataException("Invalid request.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            response = RpcResponse.Success(request.Id, await module.InvokeAsync(request.Method, request.Payload, timeout.Token));
        }
        catch (Exception e) { response = RpcResponse.Failure(request?.Id ?? "", e.Message); }
        await protocol.WriteLineAsync(JsonSerializer.Serialize(response, Wire.Json));
        await protocol.FlushAsync();
    }
    return 0;
}
catch (Exception e) { Console.Error.WriteLine(e); return 1; }

sealed class ModuleLoadContext(string entry) : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver resolver = new(entry);
    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name == typeof(IRunpadModule).Assembly.GetName().Name) return typeof(IRunpadModule).Assembly;
        var path = resolver.ResolveAssemblyToPath(name);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
    protected override nint LoadUnmanagedDll(string name)
    {
        var path = resolver.ResolveUnmanagedDllToPath(name);
        return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
    }
}
