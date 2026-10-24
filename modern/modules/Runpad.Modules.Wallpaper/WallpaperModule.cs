using System.Text.Json;
using Runpad.Contracts;

namespace Runpad.Modules.Wallpaper;

public sealed class WallpaperModule : IRunpadModule
{
    private ModuleContext context = null!;
    public Task StartAsync(ModuleContext value, CancellationToken cancellationToken) { context = value; return Task.CompletedTask; }
    public Task<object?> InvokeAsync(string method, JsonElement payload, CancellationToken cancellationToken)
    {
        if (method != "snapshot") throw new InvalidOperationException("Supported method: snapshot.");
        return Task.FromResult<object?>(new { html = Path.Combine(context.Directory, "wallpaper.html"), title = "Runpad Pro" });
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
