using Runpad.Contracts;
using Runpad.Core;
using Xunit;

namespace Runpad.Tests;

[CollectionDefinition("Environment", DisableParallelization = true)]
public sealed class EnvironmentCollection;

[Collection("Environment")]
public sealed class ModuleSettingsTests
{
    [Fact]
    public void ExplicitEnableStartsNonAutostartModuleAndDisableWins()
    {
        var previous = Environment.GetEnvironmentVariable("RUNPAD_DATA");
        var root = Path.Combine(Path.GetTempPath(), "runpad-opt-in-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("RUNPAD_DATA", root);
        try
        {
            var module = Path.Combine(Paths.Modules, "optional"); Directory.CreateDirectory(module);
            File.WriteAllText(Path.Combine(module, "optional.dll"), "test fixture");
            File.WriteAllText(Path.Combine(module, "module.ready"), "ready");
            JsonFile.Write(Path.Combine(module, "module.json"), new ModuleManifest { Id = "optional", Entry = "optional.dll", Autostart = false });
            Assert.False(new ModuleCatalog().Scan().Single().Enabled);
            Settings.Save(new ClientSettings { EnabledModules = ["optional"] });
            Assert.True(new ModuleCatalog().Scan().Single().Enabled);
            Settings.Save(Settings.Load() with { DisabledModules = ["optional"] });
            Assert.False(new ModuleCatalog().Scan().Single().Enabled);
        }
        finally { Environment.SetEnvironmentVariable("RUNPAD_DATA", previous); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
