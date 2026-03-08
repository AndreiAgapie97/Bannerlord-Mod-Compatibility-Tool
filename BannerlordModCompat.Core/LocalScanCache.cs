using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BannerlordModCompat.Core;

internal sealed class LocalScanCache
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    private readonly string _areaRoot;

    public LocalScanCache(string areaName)
    {
        string baseRoot = Environment.GetEnvironmentVariable("BANNERLORD_MOD_COMPAT_CACHE_ROOT")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BannerlordModCompat",
                "cache");
        _areaRoot = Path.Combine(baseRoot, areaName);
    }

    public bool TryRead<T>(string scopeKey, string fingerprint, out T? value)
    {
        value = default;
        try
        {
            string path = GetCachePath(scopeKey, fingerprint);
            if (!File.Exists(path))
            {
                return false;
            }

            value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), SerializerOptions);
            return value is not null;
        }
        catch
        {
            value = default;
            return false;
        }
    }

    public void Write<T>(string scopeKey, string fingerprint, T value)
    {
        try
        {
            Directory.CreateDirectory(_areaRoot);
            string path = GetCachePath(scopeKey, fingerprint);
            File.WriteAllText(path, JsonSerializer.Serialize(value, SerializerOptions));
        }
        catch
        {
            // Cache failures must never fail the scan.
        }
    }

    public static string ComputeFingerprint(IEnumerable<string> paths)
    {
        StringBuilder sb = new();
        foreach (string path in paths
                     .Where(File.Exists)
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            FileInfo info = new(path);
            sb.Append(path);
            sb.Append('|');
            sb.Append(info.Length);
            sb.Append('|');
            sb.Append(info.LastWriteTimeUtc.Ticks);
            sb.AppendLine();
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }

    private string GetCachePath(string scopeKey, string fingerprint)
    {
        string safeScope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scopeKey)));
        return Path.Combine(_areaRoot, $"{safeScope}.{fingerprint}.json");
    }
}
