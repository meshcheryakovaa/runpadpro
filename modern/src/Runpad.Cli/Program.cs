using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Runpad.Contracts;
using Runpad.Core;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);
try { return await MainAsync(args); }
catch (Exception e) { Console.Error.WriteLine("Ошибка: " + e.Message); return 1; }

static async Task<int> MainAsync(string[] args)
{
    var command = args.FirstOrDefault() ?? "help";
    var rest = args.Skip(1).ToArray();
    switch (command)
    {
        case "help": case "--help":
            Console.WriteLine("""
            Runpad Pro 0.1.0 — Windows 10/11 x64
              runpad shell                       открыть оболочку
              runpad agent start|stop             фоновый агент пользователя
              runpad status                      состояние агента и модулей
              runpad doctor [--offline]          проверка установки
              runpad config show                 настройки (токен скрыт)
              runpad config set <key> <value>     classroom, refreshMilliseconds, shareScreens, wallpaperFile
              runpad app add <name> <target> [arguments]
              runpad app remove <name>
              runpad module list
              runpad module install <folder|zip|id> [--version v0.1.0]
              runpad module update <id> [--version v0.1.0]
              runpad module enable|disable|remove <id>
              runpad module invoke <id> <method> [json]
              runpad module secret <id> --env NAME  передать секрет из переменной окружения
              runpad enroll --server URL [--name NAME] [--classroom NAME] [--machine]
                токен администратора: переменная RUNPAD_ADMIN_TOKEN
              runpad service install|uninstall|start|stop|status   (администратор)
              runpad power shutdown|restart|cancel-shutdown       (администратор)
              runpad server                      запустить центр управления
              runpad token generate              новый случайный токен
              runpad update [--version v0.1.0] [--check]
              runpad rollback                    вернуть предыдущую версию
            Документация: https://github.com/meshcheryakovaa/runpadpro/tree/modern/windows-modular/modern
            """); return 0;
        case "--version": Console.WriteLine("0.1.0"); return 0;
        case "token" when rest.FirstOrDefault() == "generate": Console.WriteLine(Convert.ToHexString(RandomNumberGenerator.GetBytes(32))); return 0;
        case "shell": Start("Shell"); return 0;
        case "agent":
            if (rest.FirstOrDefault() == "start") Start("Agent");
            else if (rest.FirstOrDefault() == "stop") PrintResponse(await Ipc.CallAsync(Paths.PipeName, "stop"));
            else throw new ArgumentException("agent start|stop"); return 0;
        case "status": PrintResponse(await Ipc.CallAsync(Paths.PipeName, "status")); return 0;
        case "doctor":
            Settings.Load();
            foreach (var name in new[] { "Cli", "Agent", "Shell", "ModuleHost", "Updater", "Service" })
                if (!File.Exists(Paths.Component(name))) throw new FileNotFoundException("Missing component: " + name);
            var modules = new ModuleCatalog().Scan();
            if (modules.Any(m => m.Error is not null)) throw new InvalidDataException("Module dependency error. Run: runpad module list");
            Print(new { status = "ok", version = "0.1.0", install = Paths.Install, data = Paths.Data, modules = modules.Count });
            if (!rest.Contains("--offline"))
            {
                try { PrintResponse(await Ipc.CallAsync(Paths.PipeName, "status")); }
                catch { Console.WriteLine("Агент не запущен. Запуск: runpad agent start"); }
            }
            return 0;
        case "config":
            if (rest.FirstOrDefault() == "show")
            {
                var s = Settings.Load(); Print(s with { Enrollment = s.Enrollment is { } e ? e with { Token = "[hidden]" } : null });
            }
            else if (rest.Length == 3 && rest[0] == "set")
            {
                var s = Settings.Load();
                s = rest[1] switch
                {
                    "classroom" => s with { Classroom = rest[2] },
                    "refreshMilliseconds" => s with { RefreshMilliseconds = int.Parse(rest[2]) },
                    "shareScreens" => s with { ShareScreens = bool.Parse(rest[2]) },
                    "wallpaperFile" => s with { WallpaperFile = rest[2] == "none" ? null : Path.GetFullPath(rest[2]) },
                    _ => throw new ArgumentException("Unknown setting. See runpad help.")
                };
                Settings.Save(s); Console.WriteLine("Сохранено.");
            }
            else throw new ArgumentException("config show | config set <key> <value>"); return 0;
        case "app":
            var settings = Settings.Load();
            if (rest.Length >= 3 && rest[0] == "add")
            {
                Settings.Save(settings with { Applications = settings.Applications.Where(a => a.Name != rest[1]).Append(new(rest[1], rest[2], rest.ElementAtOrDefault(3) ?? "")).ToArray() });
            }
            else if (rest.Length == 2 && rest[0] == "remove") Settings.Save(settings with { Applications = settings.Applications.Where(a => a.Name != rest[1]).ToArray() });
            else throw new ArgumentException("app add <name> <target> [arguments] | app remove <name>"); return 0;
        case "module": await ModuleAsync(rest); return 0;
        case "enroll":
            if (rest.Contains("--machine")) RequireAdmin();
            var server = Option(rest, "--server") ?? throw new ArgumentException("Specify --server URL.");
            var token = Environment.GetEnvironmentVariable("RUNPAD_ADMIN_TOKEN") ?? throw new ArgumentException("Set RUNPAD_ADMIN_TOKEN.");
            var cfg = Settings.Load();
            using (var http = Net.Client(server, token))
            {
                using var response = await http.PostAsJsonAsync("api/admin/devices", new { name = Option(rest, "--name") ?? Environment.MachineName, classroom = Option(rest, "--classroom") ?? cfg.Classroom });
                response.EnsureSuccessStatusCode();
                var registration = await response.Content.ReadFromJsonAsync<JsonElement>();
                var enrolled = cfg with { Classroom = Option(rest, "--classroom") ?? cfg.Classroom,
                    Enrollment = new(server, registration.GetProperty("deviceId").GetString()!, registration.GetProperty("token").GetString()!) };
                if (rest.Contains("--machine"))
                {
                    var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
                    foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                    acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                    SecureDirectory(Paths.SharedData, acl);
                    JsonFile.Write(Path.Combine(Paths.SharedData, "config.json"), enrolled with { ShareScreens = false });
                }
                else Settings.Save(enrolled);
            }
            Console.WriteLine("Компьютер подключён. Для работы без входа пользователя установите службу: runpad service install"); return 0;
        case "service": await ServiceAsync(rest.FirstOrDefault() ?? "status"); return 0;
        case "power":
            RequireAdmin();
            PrintResponse(await Ipc.CallAsync(Paths.ServicePipe, rest.FirstOrDefault() ?? "status")); return 0;
        case "server":
            var serverDir = Path.Combine(Paths.Data, "server"); Directory.CreateDirectory(serverDir);
            var start = new ProcessStartInfo(Paths.Component("Server")) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Paths.Component("Server"))! };
            start.Environment["RUNPAD_SERVER_DATA"] = serverDir;
            foreach (var arg in rest) start.ArgumentList.Add(arg);
            using (var process = Process.Start(start)!) { await process.WaitForExitAsync(); return process.ExitCode; }
        case "update": case "rollback": await UpdateAsync(command == "rollback", rest); return 0;
        default: throw new ArgumentException("Неизвестная команда. runpad help");
    }
}

static void Start(string component) => Process.Start(new ProcessStartInfo(Paths.Component(component)) { UseShellExecute = true });
static void Print(object? value) => Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions(Wire.Json) { WriteIndented = true }));
static void PrintResponse(RpcResponse r) { if (!r.Ok) throw new IOException(r.Error); Print(r.Result); }
static string? Option(string[] args, string name) { var index = Array.IndexOf(args, name); return index < 0 ? null : index + 1 < args.Length ? args[index + 1] : throw new ArgumentException("Missing value: " + name); }
static void RequireAdmin()
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows required.");
    using var identity = WindowsIdentity.GetCurrent();
    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException("Откройте терминал от имени администратора.");
}
static async Task NotifyAgentAsync()
{
    try { using var ct = new CancellationTokenSource(1500); await Ipc.CallAsync(Paths.PipeName, "refresh", ct: ct.Token); } catch { }
}
static void SetModuleEnabled(string id, bool enabled)
{
    var settings = Settings.Load();
    Settings.Save(settings with
    {
        DisabledModules = enabled ? settings.DisabledModules.Where(x => x != id).ToArray() : settings.DisabledModules.Append(id).Distinct().ToArray(),
        EnabledModules = enabled ? settings.EnabledModules.Append(id).Distinct().ToArray() : settings.EnabledModules.Where(x => x != id).ToArray()
    });
}
static async Task ModuleAsync(string[] args)
{
    var verb = args.FirstOrDefault() ?? "list";
    if (verb == "list") { Print(new ModuleCatalog().Scan()); return; }
    if (args.Length < 2) throw new ArgumentException("Specify module id or package path.");
    var id = args[1];
    if (verb is "install" or "update")
    {
        var staging = Path.Combine(Paths.Modules, ".staging-" + Guid.NewGuid().ToString("N"));
        string? downloaded = null;
        try
        {
            if (Directory.Exists(id)) Packages.CopyTree(Path.GetFullPath(id), staging);
            else if (File.Exists(id)) Packages.Extract(Path.GetFullPath(id), staging, 100_000_000);
            else
            {
                if (!ModuleCatalog.ValidId(id)) throw new ArgumentException("Invalid module id.");
                var release = await Packages.DownloadReleaseAsync($"RunpadPro-Module-{id}.zip", Option(args, "--version"), CancellationToken.None);
                downloaded = release.Path; Packages.Extract(downloaded, staging, 100_000_000);
            }
            var manifest = ModuleCatalog.Validate(JsonFile.Read(Path.Combine(staging, "module.json"), new ModuleManifest()), staging);
            if (verb == "update" && manifest.Id != id) throw new InvalidDataException("Module ID mismatch.");
            var destination = Path.Combine(Paths.Modules, manifest.Id);
            var wasDisabled = Settings.Load().DisabledModules.Contains(manifest.Id);
            SetModuleEnabled(manifest.Id, false); await NotifyAgentAsync();
            var backup = Path.Combine(Paths.Modules, "." + manifest.Id + ".previous");
            try
            {
                if (Directory.Exists(backup)) Directory.Delete(backup, true);
                if (Directory.Exists(destination)) Directory.Move(destination, backup);
                File.WriteAllText(Path.Combine(staging, "module.ready"), "ready");
                Directory.Move(staging, destination);
            }
            catch
            {
                if (!Directory.Exists(destination) && Directory.Exists(backup)) Directory.Move(backup, destination);
                throw;
            }
            finally { SetModuleEnabled(manifest.Id, !wasDisabled); await NotifyAgentAsync(); }
            Console.WriteLine($"Установлен {manifest.Id} {manifest.Version}");
        }
        finally { if (downloaded is not null) File.Delete(downloaded); if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        return;
    }
    if (!ModuleCatalog.ValidId(id)) throw new ArgumentException("Invalid module id.");
    if (verb is "enable" or "disable") { SetModuleEnabled(id, verb == "enable"); await NotifyAgentAsync(); return; }
    if (verb == "remove")
    {
        SetModuleEnabled(id, false); await NotifyAgentAsync();
        var dir = Path.Combine(Paths.Modules, id);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Console.WriteLine("Модуль отключён. Встроенные файлы сохраняются до обновления приложения."); return;
    }
    if (verb == "invoke" && args.Length >= 3)
    {
        PrintResponse(await Ipc.CallAsync(Paths.PipeName, "module.invoke", new { id, method = args[2], payload = args.Length > 3 ? JsonSerializer.Deserialize<JsonElement>(args[3]) : Wire.Element(null) })); return;
    }
    if (verb == "secret")
    {
        var variable = Option(args, "--env") ?? throw new ArgumentException("Specify --env NAME.");
        var token = Environment.GetEnvironmentVariable(variable) ?? throw new ArgumentException("Environment variable is empty.");
        PrintResponse(await Ipc.CallAsync(Paths.PipeName, "module.invoke", new { id, method = "configure", payload = new { token } })); return;
    }
    throw new ArgumentException("Unknown module command.");
}
static async Task<int> ScAsync(params string[] arguments)
{
    var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe")) { UseShellExecute = false };
    foreach (var arg in arguments) start.ArgumentList.Add(arg);
    using var process = Process.Start(start)!; await process.WaitForExitAsync(); return process.ExitCode;
}
static void SecureDirectory(string path, DirectorySecurity security)
{
    RequireAdmin();
    var directory = new DirectoryInfo(path);
    if (directory.Exists)
    {
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Linked system data directory is not allowed.");
        var owner = directory.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier))?.Value;
        using var identity = WindowsIdentity.GetCurrent();
        if (owner != identity.User?.Value && owner != "S-1-5-18" && owner != "S-1-5-32-544")
            throw new UnauthorizedAccessException("System data directory is owned by another user. Remove it after inspection before installing.");
        void CheckChildren(DirectoryInfo parent)
        {
            foreach (var child in parent.EnumerateFileSystemInfos())
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Linked system data entry is not allowed.");
                if (child is DirectoryInfo subdirectory) CheckChildren(subdirectory);
            }
        }
        CheckChildren(directory);
    }
    security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
    directory.Create(security);
    directory.SetAccessControl(security);
}
static async Task ServiceAsync(string verb)
{
    RequireAdmin();
    if (verb == "status") { PrintResponse(await Ipc.CallAsync(Paths.ServicePipe, "status")); return; }
    if (verb == "install")
    {
        var trustedRoot = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) + Path.DirectorySeparatorChar;
        if (!Paths.App.StartsWith(trustedRoot, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Установите клиент в Program Files через Setup. Служба не запускается из папки, доступной для записи пользователю.");
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        SecureDirectory(Paths.MachineData, acl);
        JsonFile.Write(Path.Combine(Paths.MachineData, "config.json"), Settings.Load() with { ShareScreens = false });
        if (await ScAsync("create", "RunpadPro", "binPath=", "\"" + Paths.Component("Service") + "\"", "start=", "auto", "DisplayName=", "Runpad Pro System Service") != 0)
            throw new IOException("Не удалось создать службу. Если она уже установлена: runpad service uninstall");
        await ScAsync("failure", "RunpadPro", "reset=", "86400", "actions=", "restart/10000/restart/30000/restart/60000");
        if (await ScAsync("start", "RunpadPro") != 0) throw new IOException("Service start failed.");
    }
    else if (verb == "uninstall") { await ScAsync("stop", "RunpadPro"); if (await ScAsync("delete", "RunpadPro") != 0) throw new IOException("Service deletion failed."); }
    else if (verb is "start" or "stop") { if (await ScAsync(verb, "RunpadPro") != 0) throw new IOException("Service command failed."); }
    else throw new ArgumentException("service install|uninstall|start|stop|status");
}
static async Task UpdateAsync(bool rollback, string[] args)
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows required.");
    var portable = !Paths.Install.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    if (!portable) RequireAdmin();
    string? candidate = null;
    if (!rollback)
    {
        var download = await Packages.DownloadReleaseAsync("RunpadPro-Client-win-x64.zip", Option(args, "--version"), CancellationToken.None);
        candidate = Path.Combine(Paths.Install, ".next-" + Guid.NewGuid().ToString("N"));
        try
        {
            Packages.Extract(download.Path, candidate);
            var manifest = Packages.VerifyClient(candidate);
            if (args.Contains("--check")) { Print(new { available = manifest.Version, current = "0.1.0" }); Directory.Delete(candidate, true); return; }
        }
        catch { if (Directory.Exists(candidate)) Directory.Delete(candidate, true); throw; }
        finally { File.Delete(download.Path); }
    }
    else if (!Directory.Exists(Path.Combine(Paths.Install, "previous-app"))) throw new DirectoryNotFoundException("Предыдущая версия отсутствует.");
    var updater = Path.Combine(Path.GetTempPath(), "RunpadUpdater-" + Guid.NewGuid().ToString("N"));
    Packages.CopyTree(Path.Combine(Paths.App, "updater"), updater);
    var start = new ProcessStartInfo(Path.Combine(updater, "Runpad.Updater.exe")) { UseShellExecute = false };
    foreach (var arg in new[] { "--root", Paths.Install, "--wait-pid", Environment.ProcessId.ToString() }) start.ArgumentList.Add(arg);
    if (rollback) start.ArgumentList.Add("--rollback");
    else { start.ArgumentList.Add("--candidate"); start.ArgumentList.Add(candidate!); }
    Process.Start(start); Console.WriteLine("Обновление запущено. Результат: " + Path.Combine(Paths.Install, "update-result.json"));
}
