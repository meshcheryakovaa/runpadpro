using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Runpad.Contracts;
using Runpad.Core;

Environment.SetEnvironmentVariable("RUNPAD_DATA", Paths.MachineData);
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "RunpadPro");
builder.Services.AddHostedService<SystemWorker>();
await builder.Build().RunAsync();

sealed class SystemWorker : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pipe = Ipc.ServeAsync(CreatePipe, (request, _) => Task.FromResult(request.Method switch
        {
            "status" => RpcResponse.Success(request.Id, new { service = "RunpadPro", version = "0.1.0", computer = Environment.MachineName }),
            "shutdown" or "restart" or "cancel-shutdown" => Power(request),
            _ => RpcResponse.Failure(request.Id, "Supported: status, shutdown, restart, cancel-shutdown.")
        }), stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var settings = Settings.Load();
                    if (settings.Enrollment is { } enrollment)
                    {
                        using var hub = new HubClient(enrollment);
                        await hub.HeartbeatAsync(new(Environment.MachineName, settings.Classroom, "system", Wire.Element(Array.Empty<object>())), stoppingToken);
                        foreach (var command in await hub.PollAsync("system", stoppingToken))
                        {
                            var receipt = Path.Combine(Paths.Data, "receipts", command.Id + ".json");
                            var result = JsonFile.Read<CommandResult?>(receipt, null);
                            if (result is null)
                            {
                                if (command.ExpiresAt <= DateTimeOffset.UtcNow) result = new(false, "Command expired.");
                                else
                                {
                                    var response = Power(new(command.Id, command.Kind, command.Payload));
                                    result = new(response.Ok, response.Error);
                                }
                                JsonFile.Write(receipt, result);
                            }
                            await hub.AckAsync(command.Id, result, stoppingToken);
                        }
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException) { Log.Write("service", e.Message); }
                await Task.Delay(5000, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await pipe; }
    }
    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(Paths.ServicePipe, PipeDirection.InOut, 4, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4096, 4096, security);
    }
    private static RpcResponse Power(RpcRequest request)
    {
        try
        {
            var args = request.Method switch
            {
                "shutdown" => "/s /t 30 /c \"Runpad Pro: выключение через 30 секунд\"",
                "restart" => "/r /t 30 /c \"Runpad Pro: перезагрузка через 30 секунд\"",
                "cancel-shutdown" => "/a",
                _ => throw new InvalidOperationException("Unsupported system command.")
            };
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), args)
                { UseShellExecute = false, CreateNoWindow = true })!;
            if (!process.WaitForExit(5000) || process.ExitCode != 0) throw new IOException("Windows rejected the power command.");
            return RpcResponse.Success(request.Id, "scheduled");
        }
        catch (Exception e) { return RpcResponse.Failure(request.Id, e.Message); }
    }
}
