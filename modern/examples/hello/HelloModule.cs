using System.Text.Json;
using Runpad.Contracts;

public sealed class HelloModule : IRunpadModule
{
    public Task StartAsync(ModuleContext context, CancellationToken ct) => Task.CompletedTask;
    public Task<object?> InvokeAsync(string method, JsonElement payload, CancellationToken ct) =>
        Task.FromResult<object?>(new { message = "Здравствуйте! Модуль работает.", method, time = DateTimeOffset.Now });
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
