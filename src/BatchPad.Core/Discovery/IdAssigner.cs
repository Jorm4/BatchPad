using System.Text;

namespace BatchPad.Core.Discovery;

public static class IdAssigner
{
    /// <summary><c>tools/regen_assets.py</c> → <c>regen-assets</c>, or <c>regen-assets-2</c> when that is taken (§3.2).</summary>
    public static string FromFileName(string path, IReadOnlySet<string> takenIds) =>
        FromName(Path.GetFileNameWithoutExtension(path), takenIds);

    /// <summary><c>Build Release · Rally</c> → <c>build-release-rally</c>, suffixed when taken.</summary>
    public static string FromName(string name, IReadOnlySet<string> takenIds)
    {
        var slug = Slugify(name);
        if (slug.Length == 0)
            slug = "script";
        var id = slug;
        for (var n = 2; takenIds.Contains(id); n++)
            id = $"{slug}-{n}";
        return id;
    }

    private static string Slugify(string name)
    {
        var slug = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
                slug.Append(c);
            else if (slug.Length > 0 && slug[^1] != '-')
                slug.Append('-');
        }
        return slug.ToString().TrimEnd('-');
    }
}
