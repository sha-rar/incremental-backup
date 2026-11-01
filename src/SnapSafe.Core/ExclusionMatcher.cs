using System.Text;
using System.Text.RegularExpressions;

namespace SnapSafe.Core;

internal sealed class ExclusionMatcher
{
    private readonly Regex[] patterns;

    internal ExclusionMatcher(IEnumerable<string> exclusions)
    {
        patterns = exclusions.Select(pattern =>
        {
            var normalized = Normalize(pattern);
            var anchored = normalized.Contains('/');
            if (normalized.EndsWith("/**", StringComparison.Ordinal))
                normalized = normalized[..^3];
            var expression = new StringBuilder(anchored ? "^" : "(?:^|/)");
            for (var i = 0; i < normalized.Length; i++)
            {
                if (normalized[i] == '*' && i + 1 < normalized.Length && normalized[i + 1] == '*')
                {
                    i++;
                    if (i + 1 < normalized.Length && normalized[i + 1] == '/')
                    {
                        expression.Append("(?:[^/]+/)*");
                        i++;
                    }
                    else
                        expression.Append(".*");
                }
                else
                    expression.Append(normalized[i] switch { '*' => "[^/]*", '?' => "[^/]", _ => Regex.Escape(normalized[i].ToString()) });
            }
            expression.Append('$');
            return new Regex(expression.ToString(), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking |
                (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None));
        }).ToArray();
    }

    internal static string Normalize(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        pattern = pattern.Replace('\\', '/');
        if (pattern.EndsWith('/'))
            pattern = pattern[..^1];
        foreach (var segment in pattern.Split('/'))
        {
            if (segment.Contains("**", StringComparison.Ordinal) && segment != "**")
                throw new ArgumentException("** must occupy an entire exclusion path component.");
            PathSafety.ValidateSegment(segment.Replace('*', 'x').Replace('?', 'x'));
        }
        return pattern;
    }

    internal bool IsExcluded(string relativePath)
    {
        for (var path = relativePath; ;)
        {
            if (patterns.Any(pattern => pattern.IsMatch(path)))
                return true;
            var slash = path.LastIndexOf('/');
            if (slash < 0)
                return false;
            path = path[..slash];
        }
    }
}
