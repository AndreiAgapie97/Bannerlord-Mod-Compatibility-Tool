using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace BannerlordModCompat.Core;

public sealed class ModuleScanner
{
    private static readonly string[] IdAttributeCandidates = ["id", "Id", "ID", "stringId", "StringId"];
    private readonly LocalScanCache _cache = new("module-scan");

    public IReadOnlyList<ModuleManifest> Scan(
        IReadOnlyList<string> moduleRoots,
        string? workshopRoot,
        List<string> warnings
    )
    {
        HashSet<string> subModulePaths = new(StringComparer.OrdinalIgnoreCase);

        foreach (string root in moduleRoots.Where(Directory.Exists))
        {
            foreach (string path in TryEnumerateFiles(root, "SubModule.xml", warnings))
            {
                subModulePaths.Add(Path.GetFullPath(path));
            }
        }

        if (!string.IsNullOrWhiteSpace(workshopRoot) && Directory.Exists(workshopRoot))
        {
            foreach (string path in TryEnumerateFiles(workshopRoot, "SubModule.xml", warnings))
            {
                subModulePaths.Add(Path.GetFullPath(path));
            }
        }

        List<ModuleManifest> modules = [];
        foreach (string subModulePath in subModulePaths)
        {
            string? moduleRoot = Directory.GetParent(subModulePath)?.FullName;
            if (string.IsNullOrWhiteSpace(moduleRoot))
            {
                continue;
            }

            bool isWorkshop = !string.IsNullOrWhiteSpace(workshopRoot)
                && moduleRoot.StartsWith(workshopRoot, StringComparison.OrdinalIgnoreCase);

            try
            {
                string fingerprint = BuildModuleFingerprint(moduleRoot, subModulePath, warnings);
                string cacheScope = Path.GetFullPath(moduleRoot);
                ModuleManifest? manifest;
                if (!_cache.TryRead(cacheScope, fingerprint, out manifest) || manifest is null)
                {
                    manifest = ParseModuleManifest(moduleRoot, subModulePath, isWorkshop, warnings);
                    _cache.Write(cacheScope, fingerprint, manifest);
                }

                modules.Add(manifest);
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to parse module at {moduleRoot}: {ex.Message}");
            }
        }

        return modules
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(g => g.SourceType == ModSourceType.Local)
                .ThenByDescending(g => g.Version)
                .First())
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildModuleFingerprint(string moduleRoot, string subModulePath, List<string> warnings)
    {
        List<string> relevantFiles = [subModulePath];
        string moduleDataPath = Path.Combine(moduleRoot, "ModuleData");
        string binPath = Path.Combine(moduleRoot, "bin");
        string guiPath = Path.Combine(moduleRoot, "GUI");

        if (Directory.Exists(moduleDataPath))
        {
            relevantFiles.AddRange(TryEnumerateFiles(moduleDataPath, "*.xml", warnings));
            relevantFiles.AddRange(TryEnumerateFiles(moduleDataPath, "*.xslt", warnings));
        }

        if (Directory.Exists(binPath))
        {
            relevantFiles.AddRange(TryEnumerateFiles(binPath, "*.dll", warnings));
        }

        if (Directory.Exists(guiPath))
        {
            relevantFiles.AddRange(TryEnumerateFiles(guiPath, "*.xml", warnings));
        }

        return LocalScanCache.ComputeFingerprint(relevantFiles);
    }

    private static ModuleManifest ParseModuleManifest(
        string moduleRoot,
        string subModulePath,
        bool isWorkshop,
        List<string> warnings
    )
    {
        XDocument doc = XDocument.Load(subModulePath, LoadOptions.None);
        XElement? moduleNode = doc.Root;

        string folderName = Path.GetFileName(moduleRoot);
        string id = ReadValue(moduleNode, "Id") ?? folderName;
        string name = ReadValue(moduleNode, "Name") ?? folderName;
        string? version = ReadValue(moduleNode, "Version");

        List<ModuleDependency> dependencies = ParseDependencies(moduleNode);
        List<string> incompatibleIds = ParseExplicitIncompatibilities(moduleNode);
        List<XmlEntityReference> xmlEntities = ParseXmlEntities(moduleRoot, id, warnings);
        List<DllArtifact> dlls = ParseDllArtifacts(moduleRoot, warnings);

        return new ModuleManifest
        {
            Id = id,
            Name = name,
            Version = version,
            RootPath = moduleRoot,
            SubModulePath = subModulePath,
            SourceType = isWorkshop ? ModSourceType.SteamWorkshop : ModSourceType.Local,
            IsOfficial = ModuleTaxonomy.IsOfficial(id),
            IsFramework = ModuleTaxonomy.IsFramework(id),
            Dependencies = dependencies,
            ExplicitIncompatibilities = incompatibleIds,
            XmlEntities = xmlEntities,
            Dlls = dlls,
        };
    }

    private static string? ReadValue(XElement? moduleNode, string elementName)
    {
        if (moduleNode is null)
        {
            return null;
        }

        XElement? child = moduleNode.Descendants()
            .FirstOrDefault(e => e.Name.LocalName.Equals(elementName, StringComparison.OrdinalIgnoreCase));
        if (child is null)
        {
            return null;
        }

        XAttribute? valueAttribute = child.Attributes()
            .FirstOrDefault(a => a.Name.LocalName.Equals("value", StringComparison.OrdinalIgnoreCase));
        if (valueAttribute is not null && !string.IsNullOrWhiteSpace(valueAttribute.Value))
        {
            return valueAttribute.Value.Trim();
        }

        string value = child.Value.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static List<ModuleDependency> ParseDependencies(XElement? moduleNode)
    {
        if (moduleNode is null)
        {
            return [];
        }

        List<ModuleDependency> dependencies = [];
        IEnumerable<XElement> dependencyNodes = moduleNode.Descendants().Where(e =>
            e.Name.LocalName.Contains("DependedModule", StringComparison.OrdinalIgnoreCase) ||
            e.Name.LocalName.Contains("Dependency", StringComparison.OrdinalIgnoreCase));

        foreach (XElement dep in dependencyNodes)
        {
            string? depId = AttributeValue(dep, "Id") ?? AttributeValue(dep, "id");
            if (string.IsNullOrWhiteSpace(depId))
            {
                continue;
            }

            bool incompatible = ParseBool(AttributeValue(dep, "Incompatible"))
                || ParseBool(AttributeValue(dep, "incompatible"));
            if (incompatible)
            {
                continue;
            }

            bool optional = ParseBool(AttributeValue(dep, "Optional")) || ParseBool(AttributeValue(dep, "IsOptional"));
            string? versionHint = AttributeValue(dep, "DependentVersion")
                ?? AttributeValue(dep, "Version")
                ?? AttributeValue(dep, "MinVersion")
                ?? AttributeValue(dep, "MaxVersion");
            LoadOrderConstraint order = ParseOrderConstraint(AttributeValue(dep, "order"));

            dependencies.Add(new ModuleDependency(depId.Trim(), optional, versionHint?.Trim(), order));
        }

        IEnumerable<XElement> loadAfterNodes = moduleNode.Descendants().Where(e =>
            e.Name.LocalName.Equals("Module", StringComparison.OrdinalIgnoreCase)
            && e.Parent is not null
            && e.Parent.Name.LocalName.Equals("ModulesToLoadAfterThis", StringComparison.OrdinalIgnoreCase));

        foreach (XElement node in loadAfterNodes)
        {
            string? depId = AttributeValue(node, "Id") ?? AttributeValue(node, "id");
            if (string.IsNullOrWhiteSpace(depId))
            {
                continue;
            }

            string? optionalRaw = AttributeValue(node, "Optional");
            string? isOptionalRaw = AttributeValue(node, "IsOptional");
            bool explicitOptional = ParseBool(optionalRaw) || ParseBool(isOptionalRaw);
            bool hasExplicitOptionalFlag = !string.IsNullOrWhiteSpace(optionalRaw) || !string.IsNullOrWhiteSpace(isOptionalRaw);
            bool optional = explicitOptional || !hasExplicitOptionalFlag;
            string? versionHint = AttributeValue(node, "DependentVersion")
                ?? AttributeValue(node, "Version")
                ?? AttributeValue(node, "MinVersion")
                ?? AttributeValue(node, "MaxVersion");

            dependencies.Add(new ModuleDependency(
                depId.Trim(),
                optional,
                versionHint?.Trim(),
                LoadOrderConstraint.LoadAfterThis));
        }

        return dependencies
            .GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                ModuleDependency[] grouped = g.ToArray();
                bool optional = grouped.All(x => x.Optional);
                string? versionHint = grouped
                    .Select(x => x.VersionHint)
                    .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                int beforeCount = grouped.Count(x => x.Order == LoadOrderConstraint.LoadBeforeThis);
                int afterCount = grouped.Length - beforeCount;
                LoadOrderConstraint order = afterCount > beforeCount
                    ? LoadOrderConstraint.LoadAfterThis
                    : LoadOrderConstraint.LoadBeforeThis;

                return new ModuleDependency(g.Key, optional, versionHint, order);
            })
            .ToList();
    }

    private static List<string> ParseExplicitIncompatibilities(XElement? moduleNode)
    {
        if (moduleNode is null)
        {
            return [];
        }

        List<string> ids = [];
        IEnumerable<XElement> incompatibleNodes = moduleNode.Descendants().Where(e =>
            e.Name.LocalName.Contains("Incompatible", StringComparison.OrdinalIgnoreCase));

        foreach (XElement node in incompatibleNodes)
        {
            string? value = AttributeValue(node, "Id")
                ?? AttributeValue(node, "id")
                ?? AttributeValue(node, "value");

            if (!string.IsNullOrWhiteSpace(value))
            {
                ids.Add(value.Trim());
            }
        }

        IEnumerable<XElement> metadataNodes = moduleNode.Descendants().Where(e =>
            e.Name.LocalName.Contains("DependedModuleMetadata", StringComparison.OrdinalIgnoreCase)
            || e.Name.LocalName.Contains("Dependency", StringComparison.OrdinalIgnoreCase));
        foreach (XElement node in metadataNodes)
        {
            bool incompatible = ParseBool(AttributeValue(node, "Incompatible"))
                || ParseBool(AttributeValue(node, "incompatible"));
            if (!incompatible)
            {
                continue;
            }

            string? depId = AttributeValue(node, "Id")
                ?? AttributeValue(node, "id")
                ?? AttributeValue(node, "value");
            if (!string.IsNullOrWhiteSpace(depId))
            {
                ids.Add(depId.Trim());
            }
        }

        return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<XmlEntityReference> ParseXmlEntities(string moduleRoot, string moduleId, List<string> warnings)
    {
        string moduleDataPath = Path.Combine(moduleRoot, "ModuleData");
        if (!Directory.Exists(moduleDataPath))
        {
            return [];
        }

        List<XmlEntityReference> entities = [];
        foreach (string xmlPath in TryEnumerateFiles(moduleDataPath, "*.xml", warnings))
        {
            if (xmlPath.Contains($"{Path.DirectorySeparatorChar}Languages{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                XDocument doc = XDocument.Load(xmlPath, LoadOptions.None);
                foreach (XElement element in doc.Descendants())
                {
                    string? keyValue = IdAttributeCandidates
                        .Select(candidate => AttributeValue(element, candidate))
                        .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

                    if (string.IsNullOrWhiteSpace(keyValue))
                    {
                        continue;
                    }

                    string entityType = element.Name.LocalName.Trim();
                    string entityId = keyValue.Trim();
                    string entityKey = $"{entityType}:{entityId}".ToLowerInvariant();
                    string relativePath = Path.GetRelativePath(moduleRoot, xmlPath);

                    entities.Add(new XmlEntityReference(entityKey, entityType, entityId, relativePath));
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"XML parse warning in {moduleId} ({xmlPath}): {ex.Message}");
            }
        }

        return entities;
    }

    private static List<DllArtifact> ParseDllArtifacts(string moduleRoot, List<string> warnings)
    {
        string binPath = Path.Combine(moduleRoot, "bin");
        if (!Directory.Exists(binPath))
        {
            return [];
        }

        List<DllArtifact> artifacts = [];
        foreach (string dllPath in TryEnumerateFiles(binPath, "*.dll", warnings))
        {
            try
            {
                string sha256 = ComputeFileSha256(dllPath);
                AssemblyName? asm = null;
                try
                {
                    asm = AssemblyName.GetAssemblyName(dllPath);
                }
                catch
                {
                    // Not all files can be inspected as managed assemblies.
                }

                bool referencesHarmony = BinaryContainsToken(dllPath, "Harmony")
                    || BinaryContainsToken(dllPath, "HarmonyLib");

                artifacts.Add(new DllArtifact(
                    Path: dllPath,
                    FileName: Path.GetFileName(dllPath),
                    Sha256: sha256,
                    AssemblyName: asm?.Name,
                    AssemblyVersion: asm?.Version?.ToString(),
                    ReferencesHarmony: referencesHarmony
                ));
            }
            catch (Exception ex)
            {
                warnings.Add($"DLL scan warning in {dllPath}: {ex.Message}");
            }
        }

        return artifacts;
    }

    private static string ComputeFileSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }

    private static string? AttributeValue(XElement element, string attributeName)
    {
        XAttribute? attr = element.Attributes()
            .FirstOrDefault(a => a.Name.LocalName.Equals(attributeName, StringComparison.OrdinalIgnoreCase));
        return attr?.Value;
    }

    private static IEnumerable<string> TryEnumerateFiles(string root, string pattern, List<string> warnings)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).ToArray();
        }
        catch (Exception ex)
        {
            warnings.Add($"File enumeration warning in '{root}' ({pattern}): {ex.Message}");
            return [];
        }
    }

    private static bool BinaryContainsToken(string path, string token)
    {
        byte[] needle = Encoding.ASCII.GetBytes(token);
        if (needle.Length == 0)
        {
            return false;
        }

        const int chunkSize = 128 * 1024;
        byte[] buffer = new byte[chunkSize + needle.Length];
        int carry = 0;

        using FileStream stream = File.OpenRead(path);
        while (true)
        {
            int read = stream.Read(buffer, carry, chunkSize);
            if (read == 0)
            {
                return false;
            }

            int total = carry + read;
            if (ContainsSequence(buffer.AsSpan(0, total), needle))
            {
                return true;
            }

            carry = Math.Min(needle.Length - 1, total);
            if (carry > 0)
            {
                buffer.AsSpan(total - carry, carry).CopyTo(buffer);
            }
        }
    }

    private static bool ContainsSequence(ReadOnlySpan<byte> source, ReadOnlySpan<byte> sequence)
    {
        if (sequence.Length == 0 || source.Length < sequence.Length)
        {
            return false;
        }

        for (int i = 0; i <= source.Length - sequence.Length; i++)
        {
            if (source[i] != sequence[0])
            {
                continue;
            }

            if (source.Slice(i, sequence.Length).SequenceEqual(sequence))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ParseBool(string? value)
    {
        return bool.TryParse(value, out bool result) && result;
    }

    private static LoadOrderConstraint ParseOrderConstraint(string? value)
    {
        return value is not null
            && value.Equals("LoadAfterThis", StringComparison.OrdinalIgnoreCase)
            ? LoadOrderConstraint.LoadAfterThis
            : LoadOrderConstraint.LoadBeforeThis;
    }
}
