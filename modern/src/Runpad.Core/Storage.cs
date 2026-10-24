using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Runpad.Contracts;

namespace Runpad.Core;

public static class Paths
{
    public static string Data => Environment.GetEnvironmentVariable("RUNPAD_DATA") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RunpadPro");
    public static string MachineData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RunpadPro");
    public static string SharedData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RunpadProClient");
    public static string App => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    public static string Install => Path.GetFullPath(Path.Combine(App, ".."));
    public static string Modules => Path.Combine(Data, "modules");
    public static string BuiltinModules => Path.Combine(App, "modules");
    public static string PipeName => "runpad-user-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Environment.UserDomainName + "\\" + Environment.UserName + "|" + Path.GetFullPath(Data) + "|" +
        (OperatingSystem.IsWindows() ? System.Diagnostics.Process.GetCurrentProcess().SessionId : 0))))[..20];
    public const string ServicePipe = "runpad-system-v1";
    public static string Component(string name) => Path.Combine(App, name.ToLowerInvariant(), $"Runpad.{name}.exe");
}

public static class JsonFile
{
    public static T Read<T>(string path, T fallback) => !File.Exists(path) ? fallback :
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Wire.Json) ?? throw new InvalidDataException($"Empty JSON: {path}");
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions(Wire.Json) { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public static class Settings
{
    public static string FileName => Path.Combine(Paths.Data, "config.json");
    public static ClientSettings Load()
    {
        var shared = JsonFile.Read(Path.Combine(Paths.SharedData, "config.json"), new ClientSettings());
        var local = JsonFile.Read(FileName, shared);
        return Validate(local with { Enrollment = local.Enrollment ?? shared.Enrollment });
    }
    public static void Save(ClientSettings value) => JsonFile.Write(FileName, Validate(value));
    public static ClientSettings Validate(ClientSettings value)
    {
        if (value.RefreshMilliseconds is < 1000 or > 300000) throw new InvalidDataException("RefreshMilliseconds must be 1000..300000.");
        if (string.IsNullOrWhiteSpace(value.Classroom) || value.Classroom.Length > 100) throw new InvalidDataException("Invalid classroom.");
        if (value.Enrollment is { } e)
        {
            Net.ValidateServer(e.Server);
            if (string.IsNullOrWhiteSpace(e.DeviceId) || string.IsNullOrWhiteSpace(e.Token)) throw new InvalidDataException("Invalid enrollment.");
        }
        return value;
    }
}

public static class Log
{
    private static readonly object Gate = new();
    public static void Write(string component, string message)
    {
        lock (Gate)
        {
            var dir = Path.Combine(Paths.Data, "logs");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, component + ".log");
            if (File.Exists(file) && new FileInfo(file).Length > 2_000_000) File.Move(file, file + ".1", true);
            File.AppendAllText(file, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
    }
}
