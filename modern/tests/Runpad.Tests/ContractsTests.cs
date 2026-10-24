using System.Text;
using System.Text.Json;
using Runpad.Contracts;
using Runpad.Core;
using Xunit;

namespace Runpad.Tests;

public sealed class ContractsTests
{
    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://user:password@example.com")]
    [InlineData("file:///tmp/server")]
    [InlineData("https://example.com?token=secret")]
    public void RejectsInsecureOrCredentialedServer(string url) => Assert.Throws<InvalidDataException>(() => Net.ValidateServer(url));

    [Theory]
    [InlineData("http://127.0.0.1:7090")]
    [InlineData("https://class.example.com")]
    public void AcceptsTlsAndLocalDevelopment(string url) => Assert.NotNull(Net.ValidateServer(url));

    [Fact]
    public void RejectsServiceContextModule()
    {
        var root = Path.Combine(Path.GetTempPath(), "runpad-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "example.dll"), "");
            var manifest = new ModuleManifest { Id = "example", Entry = "example.dll", Host = "system" };
            Assert.Throws<InvalidDataException>(() => ModuleCatalog.Validate(manifest, root));
            Assert.Throws<InvalidDataException>(() => ModuleCatalog.Validate(manifest with { Host = "user", ApiVersion = 999 }, root));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task LimitsInboundIpcMessage()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("123456789\n"));
        using var reader = new StreamReader(stream);
        await Assert.ThrowsAsync<InvalidDataException>(() => Ipc.ReadLineAsync(reader, CancellationToken.None, 8));
    }
    [Fact]
    public void RoundTripsRequestWithoutLosingPayload()
    {
        var original = new RpcRequest("a", "snapshot", Wire.Element(new { text = "Привет", number = 42 }));
        var copy = JsonSerializer.Deserialize<RpcRequest>(JsonSerializer.Serialize(original, Wire.Json), Wire.Json)!;
        Assert.Equal("Привет", copy.Payload.GetProperty("text").GetString());
        Assert.Equal(42, copy.Payload.GetProperty("number").GetInt32());
    }
}
