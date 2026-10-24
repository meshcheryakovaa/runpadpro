using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;
using Runpad.Core;

string? root = null;
try
{
    string? Option(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    root = Path.GetFullPath(Option("--root") ?? throw new ArgumentException("--root required"));
    if (!File.Exists(Path.Combine(root, "runpad.cmd")) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        throw new InvalidDataException("Not a Runpad installation directory.");
    using var updateLock = new FileStream(Path.Combine(root, ".update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    var current = Path.Combine(root, "app");
    var previous = Path.Combine(root, "previous-app");
    var rollback = args.Contains("--rollback");
    var candidate = rollback ? null : Path.GetFullPath(Option("--candidate") ?? throw new ArgumentException("--candidate required"));
    if (candidate is not null)
    {
        if (Path.GetDirectoryName(candidate) != root || !Path.GetFileName(candidate).StartsWith(".next-", StringComparison.Ordinal)) throw new InvalidDataException("Candidate must be staged in the installation directory.");
        Packages.VerifyClient(candidate);
    }
    if (int.TryParse(Option("--wait-pid"), out var pid))
    {
        try
        {
            using var caller = Process.GetProcessById(pid);
            if (caller.MainModule?.FileName?.StartsWith(current + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) != true)
                throw new InvalidDataException("Unexpected calling process.");
            using var timeout = new CancellationTokenSource(20000); await caller.WaitForExitAsync(timeout.Token);
        }
        catch (ArgumentException) { }
    }
    // Recover the interrupted rename phase of an earlier update before attempting another one.
    if (!Directory.Exists(current) && Directory.Exists(previous)) Directory.Move(previous, current);
    var image = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\RunpadPro", "ImagePath", null)?.ToString()?.Trim('"');
    var ownsService = image?.Equals(Path.Combine(current, "service", "Runpad.Service.exe"), StringComparison.OrdinalIgnoreCase) == true;
    using var service = ownsService ? new ServiceController("RunpadPro") : null;
    var restartService = service is { Status: not ServiceControllerStatus.Stopped };
    var restartShell = false; var restartAgent = false;
    try
    {
        if (restartService) { service!.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)); }
        foreach (var name in new[] { "Runpad.Shell", "Runpad.Agent", "Runpad.ModuleHost", "Runpad.Cli" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (process.MainModule?.FileName?.StartsWith(current + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) != true) continue;
                        var sameSession = process.SessionId == Process.GetCurrentProcess().SessionId;
                        restartShell |= sameSession && name == "Runpad.Shell";
                        restartAgent |= sameSession && name == "Runpad.Agent";
                        process.Kill(true);
                        using var timeout = new CancellationTokenSource(15000); await process.WaitForExitAsync(timeout.Token);
                    }
                    catch (InvalidOperationException) { }
                }
            }
        }
        var swap = Path.Combine(root, ".swap-" + Guid.NewGuid().ToString("N"));
        if (rollback)
        {
            if (!Directory.Exists(previous)) throw new DirectoryNotFoundException("Previous version missing.");
            Directory.Move(current, swap);
            try { Directory.Move(previous, current); Directory.Move(swap, previous); }
            catch { if (!Directory.Exists(current) && Directory.Exists(swap)) Directory.Move(swap, current); throw; }
        }
        else
        {
            if (Directory.Exists(previous)) Directory.Delete(previous, true);
            Directory.Move(current, previous);
            try { Directory.Move(Path.Combine(candidate!, "app"), current); }
            catch { Directory.Move(previous, current); throw; }
        }
        try
        {
            var health = new ProcessStartInfo(Path.Combine(current, "cli", "Runpad.Cli.exe")) { UseShellExecute = false, CreateNoWindow = true };
            health.ArgumentList.Add("doctor"); health.ArgumentList.Add("--offline");
            using var process = Process.Start(health)!;
            using var timeout = new CancellationTokenSource(30000);
            try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
            if (process.ExitCode != 0) throw new IOException("New version failed its offline health check.");
            if (restartService)
            {
                service!.Start(); service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                var check = await Ipc.CallAsync(Paths.ServicePipe, "status");
                if (!check.Ok) throw new IOException("System service health check failed.");
            }
        }
        catch
        {
            if (restartService)
            {
                service!.Refresh();
                if (service.Status != ServiceControllerStatus.Stopped) { service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)); }
            }
            Directory.Move(current, swap);
            Directory.Move(previous, current);
            Directory.Move(swap, previous);
            throw;
        }
        JsonFile.Write(Path.Combine(root, "update-result.json"), new { ok = true, rollback, timestamp = DateTimeOffset.UtcNow });
    }
    finally
    {
        if (restartService) { service!.Refresh(); if (service.Status == ServiceControllerStatus.Stopped) service.Start(); }
        // Never relaunch a user's shell elevated from an administrator updater. The shell restarts at the next sign-in.
        if (restartShell || restartAgent) Console.WriteLine("Обновление завершено. Запустите Runpad Pro из меню Пуск в обычном сеансе пользователя.");
    }
    if (candidate is not null && Directory.Exists(candidate)) Directory.Delete(candidate, true);
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine(e.Message);
    if (root is not null) JsonFile.Write(Path.Combine(root, "update-result.json"), new { ok = false, error = e.Message, timestamp = DateTimeOffset.UtcNow });
    return 1;
}
