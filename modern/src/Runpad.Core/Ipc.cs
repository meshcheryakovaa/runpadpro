using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Runpad.Contracts;

namespace Runpad.Core;

public static class Ipc
{
    public static async Task<RpcResponse> CallAsync(string pipeName, string method, object? payload = null, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        var request = new RpcRequest(Guid.NewGuid().ToString("N"), method, Wire.Element(payload));
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, Wire.Json).AsMemory(), timeout.Token);
        var response = JsonSerializer.Deserialize<RpcResponse>(await ReadLineAsync(reader, timeout.Token), Wire.Json)
            ?? throw new IOException("Empty IPC response.");
        if (response.Id != request.Id) throw new IOException("IPC response mismatch.");
        return response;
    }
    public static async Task<string> ReadLineAsync(StreamReader reader, CancellationToken ct, int limit = 1024 * 1024)
    {
        var result = new StringBuilder();
        var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), ct) != 0)
        {
            if (buffer[0] == '\n') return result.ToString().TrimEnd('\r');
            if (result.Length >= limit) throw new InvalidDataException("IPC message too large.");
            result.Append(buffer[0]);
        }
        throw new EndOfStreamException();
    }
    public static async Task ServeAsync(Func<NamedPipeServerStream> create, Func<RpcRequest, CancellationToken, Task<RpcResponse>> dispatch, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await using var pipe = create();
            try
            {
                await pipe.WaitForConnectionAsync(ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                var request = JsonSerializer.Deserialize<RpcRequest>(await ReadLineAsync(reader, timeout.Token), Wire.Json) ?? throw new IOException("Invalid request.");
                RpcResponse response;
                try { response = await dispatch(request, timeout.Token); }
                catch (Exception e) { response = RpcResponse.Failure(request.Id, e.Message); }
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, Wire.Json).AsMemory(), timeout.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { Log.Write("ipc", e.Message); }
        }
    }
}
