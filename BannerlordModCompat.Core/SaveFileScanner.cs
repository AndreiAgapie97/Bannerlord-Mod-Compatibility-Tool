using System.Text;

namespace BannerlordModCompat.Core;

public sealed class SaveFileScanner
{
    private const long MaxSaveSizeBytes = 512L * 1024 * 1024;
    private const int ProgressUpdateChunkBytes = 4 * 1024 * 1024;

    public IReadOnlyList<SaveFileInsight> Scan(
        string? saveRoot,
        IReadOnlyList<ModuleManifest> modules,
        List<string> warnings,
        Action<int, string>? progress = null
    )
    {
        if (string.IsNullOrWhiteSpace(saveRoot) || !Directory.Exists(saveRoot))
        {
            progress?.Invoke(100, "No save root found. Skipping save-file scan.");
            return [];
        }

        string[] saveFiles;
        try
        {
            saveFiles = Directory.EnumerateFiles(saveRoot, "*.sav", SearchOption.AllDirectories).ToArray();
        }
        catch (Exception ex)
        {
            warnings.Add($"Save file enumeration failed in '{saveRoot}': {ex.Message}");
            progress?.Invoke(100, "Save-file scan could not enumerate files.");
            return [];
        }

        if (saveFiles.Length == 0)
        {
            progress?.Invoke(100, "No save files found.");
            return [];
        }

        Dictionary<string, long> fileSizeByPath = new(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (string savePath in saveFiles)
        {
            try
            {
                FileInfo info = new(savePath);
                fileSizeByPath[savePath] = info.Length;
                totalBytes += Math.Max(info.Length, 0L);
            }
            catch (Exception ex)
            {
                warnings.Add($"Save scan warning for {savePath}: {ex.Message}");
                fileSizeByPath[savePath] = 0;
            }
        }
        if (totalBytes <= 0)
        {
            totalBytes = saveFiles.Length;
        }

        List<SaveFileInsight> insights = [];

        string[] moduleIds = modules
            .Select(m => m.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id) && id.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(id => id.Length)
            .ToArray();

        int lastPercent = -1;
        long completedBytes = 0;
        progress?.Invoke(0, $"Scanning save files (0/{saveFiles.Length})...");

        for (int index = 0; index < saveFiles.Length; index++)
        {
            string savePath = saveFiles[index];
            try
            {
                FileInfo info = new(savePath);
                if (info.Length > MaxSaveSizeBytes)
                {
                    warnings.Add($"Skipped oversized save file (>{MaxSaveSizeBytes / (1024 * 1024)}MB): {savePath}");
                    completedBytes += Math.Max(info.Length, 0L);
                    ReportByteProgress(
                        progress,
                        ref lastPercent,
                        completedBytes,
                        totalBytes,
                        index + 1,
                        saveFiles.Length
                    );
                    continue;
                }

                long fileProgressBase = completedBytes;
                long fileSize = Math.Max(fileSizeByPath.GetValueOrDefault(savePath), 0L);
                List<string> referenced = ScanSaveForModuleIds(
                    savePath,
                    moduleIds,
                    bytesReadInFile =>
                    {
                        long bounded = Math.Min(bytesReadInFile, fileSize);
                        long totalProcessed = fileProgressBase + bounded;
                        ReportByteProgress(
                            progress,
                            ref lastPercent,
                            totalProcessed,
                            totalBytes,
                            index + 1,
                            saveFiles.Length
                        );
                    });

                insights.Add(new SaveFileInsight
                {
                    SavePath = savePath,
                    FileSizeBytes = info.Length,
                    ReferencedInstalledMods = referenced,
                });
                completedBytes += Math.Max(info.Length, 0L);
            }
            catch (Exception ex)
            {
                warnings.Add($"Save scan warning for {savePath}: {ex.Message}");
                completedBytes += Math.Max(fileSizeByPath.GetValueOrDefault(savePath), 0L);
            }

            ReportByteProgress(
                progress,
                ref lastPercent,
                completedBytes,
                totalBytes,
                index + 1,
                saveFiles.Length
            );
        }

        progress?.Invoke(100, $"Save-file scan complete ({saveFiles.Length} file(s)).");
        return insights;
    }

    private static void ReportByteProgress(
        Action<int, string>? progress,
        ref int lastPercent,
        long processedBytes,
        long totalBytes,
        int scannedCount,
        int totalCount
    )
    {
        if (progress is null)
        {
            return;
        }

        long safeTotal = Math.Max(totalBytes, 1L);
        long bounded = Math.Clamp(processedBytes, 0L, safeTotal);
        int percent = (int)Math.Round((bounded * 100.0) / safeTotal);
        if (percent == lastPercent)
        {
            return;
        }

        lastPercent = percent;
        progress(percent, $"Scanning save files ({scannedCount}/{totalCount})...");
    }

    private static List<string> ScanSaveForModuleIds(
        string savePath,
        IReadOnlyList<string> moduleIds,
        Action<long>? onBytesRead
    )
    {
        const int chunkSize = 256 * 1024;
        byte[] buffer = new byte[chunkSize + 256];
        int carry = 0;
        int bytesSinceLastProgress = 0;
        long totalBytesRead = 0;

        Dictionary<string, byte[]> remaining = moduleIds
            .ToDictionary(id => id, id => Encoding.ASCII.GetBytes(id), StringComparer.OrdinalIgnoreCase);
        List<string> found = [];
        int longestPattern = remaining.Count == 0 ? 1 : remaining.Values.Max(v => v.Length);

        using FileStream stream = File.OpenRead(savePath);
        while (remaining.Count > 0)
        {
            int read = stream.Read(buffer, carry, chunkSize);
            if (read == 0)
            {
                break;
            }
            totalBytesRead += read;
            bytesSinceLastProgress += read;
            if (onBytesRead is not null && bytesSinceLastProgress >= ProgressUpdateChunkBytes)
            {
                onBytesRead(totalBytesRead);
                bytesSinceLastProgress = 0;
            }

            int total = carry + read;
            List<string> matchedThisChunk = [];
            foreach ((string moduleId, byte[] pattern) in remaining)
            {
                if (pattern.Length == 0 || pattern.Length > total)
                {
                    continue;
                }

                if (ContainsSequenceIgnoreCaseAscii(buffer.AsSpan(0, total), pattern))
                {
                    matchedThisChunk.Add(moduleId);
                }
            }

            foreach (string moduleId in matchedThisChunk)
            {
                found.Add(moduleId);
                remaining.Remove(moduleId);
            }

            carry = Math.Min(longestPattern - 1, total);
            if (carry > 0)
            {
                buffer.AsSpan(total - carry, carry).CopyTo(buffer);
            }
        }

        onBytesRead?.Invoke(totalBytesRead);
        return found;
    }

    private static bool ContainsSequenceIgnoreCaseAscii(ReadOnlySpan<byte> source, ReadOnlySpan<byte> sequence)
    {
        if (sequence.Length == 0 || source.Length < sequence.Length)
        {
            return false;
        }

        for (int i = 0; i <= source.Length - sequence.Length; i++)
        {
            if (!EqualsAsciiIgnoreCase(source[i], sequence[0]))
            {
                continue;
            }

            bool matches = true;
            for (int j = 1; j < sequence.Length; j++)
            {
                if (!EqualsAsciiIgnoreCase(source[i + j], sequence[j]))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    private static bool EqualsAsciiIgnoreCase(byte left, byte right)
    {
        return ToLowerAscii(left) == ToLowerAscii(right);
    }

    private static byte ToLowerAscii(byte b)
    {
        return b is >= (byte)'A' and <= (byte)'Z'
            ? (byte)(b + 32)
            : b;
    }
}
