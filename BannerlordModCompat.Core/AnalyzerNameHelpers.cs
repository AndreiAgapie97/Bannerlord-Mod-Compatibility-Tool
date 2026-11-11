namespace BannerlordModCompat.Core;

internal static class AnalyzerNameHelpers
{
    public static string ShortName(string fullName)
    {
        int idx = fullName.LastIndexOf('.');
        return idx >= 0 && idx < fullName.Length - 1 ? fullName[(idx + 1)..] : fullName;
    }
}
