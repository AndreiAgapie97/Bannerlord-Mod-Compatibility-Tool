using BannerlordModCompat.Core;
using Xunit;

namespace BannerlordModCompat.Tests;

public class ModuleScannerCacheTests
{
    [Fact]
    public void Scan_InvalidatesCachedManifestWhenSubModuleChanges()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BannerlordModuleScannerTests", Guid.NewGuid().ToString("N"));
        string cacheRoot = Path.Combine(tempRoot, "cache");
        string modulesRoot = Path.Combine(tempRoot, "Modules");
        string moduleRoot = Path.Combine(modulesRoot, "TestMod");
        Directory.CreateDirectory(moduleRoot);

        string? previousCacheRoot = Environment.GetEnvironmentVariable("BANNERLORD_MOD_COMPAT_CACHE_ROOT");
        Environment.SetEnvironmentVariable("BANNERLORD_MOD_COMPAT_CACHE_ROOT", cacheRoot);

        try
        {
            string subModulePath = Path.Combine(moduleRoot, "SubModule.xml");
            WriteSubModule(subModulePath, "1.0.0");

            ModuleScanner scanner = new();
            List<string> warnings = [];
            ModuleManifest first = Assert.Single(scanner.Scan([modulesRoot], workshopRoot: null, warnings));
            Assert.Equal("1.0.0", first.Version);

            Thread.Sleep(20);
            WriteSubModule(subModulePath, "2.0.0");

            warnings.Clear();
            ModuleManifest second = Assert.Single(scanner.Scan([modulesRoot], workshopRoot: null, warnings));
            Assert.Equal("2.0.0", second.Version);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BANNERLORD_MOD_COMPAT_CACHE_ROOT", previousCacheRoot);
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static void WriteSubModule(string path, string version)
    {
        File.WriteAllText(path,
            $$"""
            <Module>
              <Id value="TestMod" />
              <Name value="TestMod" />
              <Version value="{{version}}" />
            </Module>
            """);
    }
}
