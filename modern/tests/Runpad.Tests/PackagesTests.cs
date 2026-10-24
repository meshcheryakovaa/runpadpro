using System.IO.Compression;
using System.Security.Cryptography;
using Runpad.Core;
using Xunit;

namespace Runpad.Tests;

public sealed class PackagesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "runpad-tests-" + Guid.NewGuid().ToString("N"));
    public PackagesTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/absolute.txt")]
    [InlineData("a/../../escape.txt")]
    [InlineData("a\\escape.txt")]
    [InlineData("NUL.txt")]
    [InlineData("a/file. ")]
    public void RejectsUnsafeArchiveNames(string entry)
    {
        var zip = Path.Combine(root, "bad.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var stream = new StreamWriter(archive.CreateEntry(entry).Open())) stream.Write("bad");
        Assert.Throws<InvalidDataException>(() => Packages.Extract(zip, Path.Combine(root, "out")));
        Assert.False(File.Exists(Path.Combine(root, "escape.txt")));
    }
    [Fact]
    public void RejectsSymlinkArchiveEntry()
    {
        var zip = Path.Combine(root, "bad.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("linked"); entry.ExternalAttributes = unchecked((int)0xA1FF0000);
            using var writer = new StreamWriter(entry.Open()); writer.Write("/etc/passwd");
        }
        Assert.Throws<InvalidDataException>(() => Packages.Extract(zip, Path.Combine(root, "out")));
    }
    [Fact]
    public void RejectsDuplicateWindowsNames()
    {
        var zip = Path.Combine(root, "bad.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        { archive.CreateEntry("a.txt"); archive.CreateEntry("A.txt"); }
        Assert.Throws<InvalidDataException>(() => Packages.Extract(zip, Path.Combine(root, "out")));
    }
    [Fact]
    public void RejectsOversizedArchive()
    {
        var zip = Path.Combine(root, "big.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var stream = archive.CreateEntry("large").Open()) stream.Write(new byte[100]);
        Assert.Throws<InvalidDataException>(() => Packages.Extract(zip, Path.Combine(root, "out"), 99));
    }
    [Fact]
    public void DetectsTamperedAndExtraClientFiles()
    {
        var files = new Dictionary<string, string>();
        foreach (var relative in new[] { "app/cli/Runpad.Cli.exe", "app/shell/Runpad.Shell.exe", "app/updater/Runpad.Updater.exe" })
        {
            var full = ModuleCatalog.SafeChild(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, relative); files[relative] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full)));
        }
        JsonFile.Write(Path.Combine(root, "package.json"), new PackageManifest("runpadpro-client", "0.1.0", files));
        Assert.Equal("0.1.0", Packages.VerifyClient(root).Version);
        File.WriteAllText(Path.Combine(root, "unexpected"), "extra");
        Assert.Throws<InvalidDataException>(() => Packages.VerifyClient(root));
        File.Delete(Path.Combine(root, "unexpected"));
        File.AppendAllText(Path.Combine(root, "app/cli/Runpad.Cli.exe"), "tampered");
        Assert.Throws<InvalidDataException>(() => Packages.VerifyClient(root));
    }
    [Fact]
    public void RejectsLinkedSourceFolder()
    {
        var real = Path.Combine(root, "real"); Directory.CreateDirectory(real);
        try { Directory.CreateSymbolicLink(Path.Combine(root, "link"), real); }
        catch (UnauthorizedAccessException) { return; } // Non-developer Windows accounts may not create links.
        Assert.Throws<InvalidDataException>(() => Packages.CopyTree(Path.Combine(root, "link"), Path.Combine(root, "out")));
    }
}
