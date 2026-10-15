using System.Text;
using System.Text.RegularExpressions;

namespace EllediTransfer;

/// <summary>
/// Subset of the WinSCP file mask syntax: <c>include1; include2 | exclude1; exclude2</c>.
/// Masks ending with <c>/</c> apply to directories, other masks to files.
/// Masks containing <c>/</c> elsewhere are matched against the path relative to the transfer root.
/// </summary>
public sealed class FileMask
{
    private readonly List<Entry> _include;
    private readonly List<Entry> _exclude;

    private FileMask(List<Entry> include, List<Entry> exclude)
    {
        _include = include;
        _exclude = exclude;
    }

    public static FileMask All { get; } = new([], []);

    public static FileMask Parse(string? mask)
    {
        if (string.IsNullOrWhiteSpace(mask))
        {
            return All;
        }

        var parts = mask.Split('|', 2);
        return new FileMask(ParseList(parts[0]), parts.Length > 1 ? ParseList(parts[1]) : []);
    }

    public bool Matches(string name, bool isDirectory, string? relativePath = null)
    {
        relativePath ??= name;
        relativePath = relativePath.Replace('\\', '/');

        var includes = _include.Where(e => e.IsDirectory == isDirectory).ToList();
        // Directories are traversed unless a directory include mask exists and none matches
        var included = includes.Count == 0 || includes.Any(e => e.Matches(name, relativePath));
        return included && !_exclude.Any(e => e.IsDirectory == isDirectory && e.Matches(name, relativePath));
    }

    private static List<Entry> ParseList(string list) =>
        list.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Entry.Create)
            .ToList();

    private sealed record Entry(Regex Regex, bool IsDirectory, bool IsPath)
    {
        public static Entry Create(string mask)
        {
            var isDirectory = mask.EndsWith('/') || mask.EndsWith('\\');
            if (isDirectory)
            {
                mask = mask[..^1];
            }
            mask = mask.Replace('\\', '/');
            return new Entry(WildcardToRegex(mask), isDirectory, mask.Contains('/'));
        }

        public bool Matches(string name, string relativePath) => Regex.IsMatch(IsPath ? relativePath : name);
    }

    public static Regex WildcardToRegex(string mask)
    {
        var sb = new StringBuilder("^");
        foreach (var c in mask)
        {
            sb.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(c.ToString()),
            });
        }
        // WinSCP: "*.*" also matches names without extension
        var pattern = sb.Append('$').ToString().Replace(@".*\..*$", ".*$");
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool HasWildcard(string name) => name.IndexOfAny(['*', '?']) >= 0;

    /// <summary>
    /// Applies a WinSCP-style operation mask to a source file name
    /// (<c>*</c> keeps the name, <c>*.bak</c> replaces the extension, a literal renames).
    /// </summary>
    internal static string ApplyOperationMask(string name, string mask)
    {
        if (mask is "*" or "*.*" or "")
        {
            return name;
        }
        if (mask.StartsWith("*.", StringComparison.Ordinal) && mask.IndexOf('*', 1) < 0)
        {
            return Path.GetFileNameWithoutExtension(name) + mask[1..];
        }
        return mask.Contains('*') ? mask.Replace("*", name) : mask;
    }
}
