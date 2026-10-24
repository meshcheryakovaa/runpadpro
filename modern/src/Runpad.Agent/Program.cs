using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Runpad.Contracts;
using Runpad.Core;

using var singleton = new Mutex(true, "Local\\" + Paths.PipeName, out var first);
if (!first) return;
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
await using var modules = new ModuleSupervisor();
var state = new AgentState();
var server = Ipc.ServeAsync(() => new NamedPipeServerStream(Paths.PipeName, PipeDirection.InOut, 4,
    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly), async (request, ct) =>
{
    return request.Method switch
    {
        "status" => RpcResponse.Success(request.Id, new { computer = Environment.MachineName, user = Environment.UserName,
            modules = state.Snapshot, serverError = state.ServerError, timestamp = DateTimeOffset.UtcNow }),
        "refresh" => RpcResponse.Success(request.Id, state.Snapshot = Wire.Element(await modules.SnapshotAsync(ct))),
        "notifications" => RpcResponse.Success(request.Id, state.DrainNotifications()),
        "module.invoke" => (await modules.InvokeAsync(request.Payload.GetProperty("id").GetString()!,
            request.Payload.GetProperty("method").GetString()!, request.Payload.GetProperty("payload"), ct)) with { Id = request.Id },
        "stop" => Stop(request.Id),
        _ => RpcResponse.Failure(request.Id, "Unknown agent method.")
    };
}, stop.Token);
try
{
    while (!stop.IsCancellationRequested)
    {
        var delay = 3000;
        try
        {
            var settings = Settings.Load();
            delay = settings.RefreshMilliseconds;
            state.Snapshot = Wire.Element(await modules.SnapshotAsync(stop.Token));
            JsonFile.Write(Path.Combine(Paths.Data, "telemetry.json"), new { computer = Environment.MachineName,
                user = Environment.UserName, classroom = settings.Classroom, timestamp = DateTimeOffset.UtcNow, modules = state.Snapshot });
            if (settings.Enrollment is { } enrollment)
            {
                using var hub = new HubClient(enrollment);
                var screen = settings.ShareScreens ? CaptureScreen() : null;
                await hub.HeartbeatAsync(new(Environment.MachineName, settings.Classroom, "user", state.Snapshot, screen, Environment.UserName), stop.Token);
                foreach (var command in await hub.PollAsync("user", stop.Token))
                {
                    // Durable deduplication: a lost ACK must not reopen a site or redeliver a file.
                    var receipt = Path.Combine(Paths.Data, "receipts", command.Id + ".json");
                    var result = JsonFile.Read<CommandResult?>(receipt, null);
                    if (result is null)
                    {
                        try
                        {
                            if (command.ExpiresAt <= DateTimeOffset.UtcNow) throw new InvalidOperationException("Command expired.");
                            await ExecuteAsync(command, hub, state, stop.Token);
                            result = new(true);
                        }
                        catch (Exception e) { result = new(false, e.Message); }
                        JsonFile.Write(receipt, result);
                    }
                    await hub.AckAsync(command.Id, result, stop.Token);
                }
                state.ServerError = null;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
        catch (Exception e) { state.ServerError = e.Message; Log.Write("agent", e.Message); }
        try { await Task.Delay(delay, stop.Token); } catch (OperationCanceledException) { break; }
    }
}
finally { stop.Cancel(); await server; }

RpcResponse Stop(string id) { stop.CancelAfter(300); return RpcResponse.Success(id, "stopping"); }

static async Task ExecuteAsync(DeviceCommand command, HubClient hub, AgentState state, CancellationToken ct)
{
    switch (command.Kind)
    {
        case "open-url":
            var raw = command.Payload.GetProperty("url").GetString()!;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0) throw new InvalidDataException("Expected HTTP(S) URL.");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            break;
        case "lock":
            if (!LockWorkStation()) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            break;
        case "message":
            var message = command.Payload.GetProperty("text").GetString() ?? "";
            if (message.Length is < 1 or > 4000) throw new InvalidDataException("Message must contain 1..4000 characters.");
            state.Notifications.Enqueue(message);
            break;
        case "file":
            var name = command.Payload.GetProperty("name").GetString()!;
            if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidDataException("Invalid file name.");
            var bytes = await hub.DownloadAsync(command.Payload.GetProperty("fileId").GetString()!, ct);
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "RunpadPro");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, command.Id + "-" + name);
            await File.WriteAllBytesAsync(file, bytes, ct);
            state.Notifications.Enqueue("Получен файл: " + file);
            break;
        default: throw new InvalidOperationException("Unsupported user command.");
    }
}

static string? CaptureScreen()
{
    try
    {
        var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        using var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        using var thumbnail = new Bitmap(bitmap, 480, Math.Max(1, bounds.Height * 480 / bounds.Width));
        using var stream = new MemoryStream();
        thumbnail.Save(stream, ImageFormat.Jpeg);
        return Convert.ToBase64String(stream.ToArray());
    }
    catch { return null; } // Secure/locked desktops are intentionally not captured.
}

[DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
static extern bool LockWorkStation();

sealed class AgentState
{
    public JsonElement Snapshot { get; set; } = Wire.Element(Array.Empty<object>());
    public string? ServerError { get; set; }
    public ConcurrentQueue<string> Notifications { get; } = new();
    public string[] DrainNotifications()
    {
        var result = new List<string>();
        while (Notifications.TryDequeue(out var item)) result.Add(item);
        return result.ToArray();
    }
}
