namespace BannerlordModCompat.Core;

internal static class EvidenceProfiles
{
    internal static FindingEvidenceDescriptor Create(
        FindingEvidenceScope scope,
        IEnumerable<FindingEvidenceSource> sources,
        IEnumerable<FindingEvidenceKind> kinds,
        IEnumerable<KeyValuePair<string, string>>? details = null)
    {
        Dictionary<string, string> normalizedDetails = new(StringComparer.OrdinalIgnoreCase);
        if (details is not null)
        {
            foreach ((string key, string value) in details)
            {
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                normalizedDetails[key.Trim()] = value.Trim();
            }
        }

        return new FindingEvidenceDescriptor
        {
            Scope = scope,
            Sources = sources
                .Where(source => source != FindingEvidenceSource.Unknown)
                .Distinct()
                .ToList(),
            Kinds = kinds
                .Where(kind => kind != FindingEvidenceKind.Unknown)
                .Distinct()
                .ToList(),
            Details = normalizedDetails,
        };
    }

    internal static FindingEvidenceDescriptor AddSources(
        FindingEvidenceDescriptor? existing,
        params FindingEvidenceSource[] sources)
    {
        if (existing is null)
        {
            return Create(FindingEvidenceScope.Unknown, sources, []);
        }

        return existing with
        {
            Sources = existing.Sources
                .Concat(sources)
                .Where(source => source != FindingEvidenceSource.Unknown)
                .Distinct()
                .ToList(),
        };
    }
}
