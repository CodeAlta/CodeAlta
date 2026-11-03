using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace CodeAlta.Fleet.Update;

/// <summary>
/// A version of a package as NuGet and SemVer 2.0 order them: <c>major.minor.patch</c>, an optional prerelease of
/// dot-separated identifiers, build metadata ignored. A fourth number (<c>1.2.3.4</c>) is accepted, as NuGet does.
/// </summary>
public sealed class UpdateVersion : IComparable<UpdateVersion>, IEquatable<UpdateVersion>
{
    private readonly int[] _numbers;
    private readonly string[] _prerelease;

    private UpdateVersion(int[] numbers, string[] prerelease)
    {
        _numbers = numbers;
        _prerelease = prerelease;
    }

    /// <summary>Gets the major version.</summary>
    public int Major => _numbers[0];

    /// <summary>Gets whether the version is a prerelease (<c>4.0.0-alpha.1</c>).</summary>
    public bool IsPrerelease => _prerelease.Length > 0;

    /// <summary>Gets the normalized text: the numbers, then the prerelease, lower case, without build metadata.</summary>
    /// <returns>The text, as a folder of versions and the flat container of a feed name it.</returns>
    public override string ToString()
    {
        var numbers = _numbers.Length == 4 && _numbers[3] == 0 ? _numbers[..3] : _numbers;
        var text = string.Join('.', numbers.Select(static number => number.ToString(CultureInfo.InvariantCulture)));
        return _prerelease.Length == 0 ? text : text + "-" + string.Join('.', _prerelease).ToLowerInvariant();
    }

    /// <summary>Parses a version.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The version.</returns>
    /// <exception cref="FormatException">The text is not a version.</exception>
    public static UpdateVersion Parse(string text)
        => TryParse(text, out var version) ? version : throw new FormatException($"'{text}' is not a version (major.minor.patch[-prerelease]).");

    /// <summary>Parses a version.</summary>
    /// <param name="text">The text.</param>
    /// <param name="version">The version, or null.</param>
    /// <returns>Whether the text is a version.</returns>
    public static bool TryParse(string? text, [NotNullWhen(true)] out UpdateVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 256) return false;
        var span = text.Trim();
        var plus = span.IndexOf('+');
        if (plus >= 0) span = span[..plus];
        var dash = span.IndexOf('-');
        var core = dash >= 0 ? span[..dash] : span;
        var parts = core.Split('.');
        if (parts.Length is < 3 or > 4) return false;
        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
        }

        string[] prerelease = [];
        if (dash >= 0)
        {
            prerelease = span[(dash + 1)..].Split('.');
            foreach (var identifier in prerelease)
            {
                if (identifier.Length == 0 || !identifier.All(static c => char.IsAsciiLetterOrDigit(c) || c == '-')) return false;
            }
        }

        version = new UpdateVersion(numbers, prerelease);
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(UpdateVersion? other)
    {
        if (other is null) return 1;
        for (var i = 0; i < 4; i++)
        {
            var mine = i < _numbers.Length ? _numbers[i] : 0;
            var theirs = i < other._numbers.Length ? other._numbers[i] : 0;
            if (mine != theirs) return mine.CompareTo(theirs);
        }

        // A release is above its prereleases; prereleases compare identifier by identifier, numbers below names.
        if (_prerelease.Length == 0 || other._prerelease.Length == 0) return other._prerelease.Length.CompareTo(_prerelease.Length);
        for (var i = 0; i < Math.Min(_prerelease.Length, other._prerelease.Length); i++)
        {
            var mineNumeric = int.TryParse(_prerelease[i], NumberStyles.None, CultureInfo.InvariantCulture, out var mine);
            var theirsNumeric = int.TryParse(other._prerelease[i], NumberStyles.None, CultureInfo.InvariantCulture, out var theirs);
            var order = (mineNumeric, theirsNumeric) switch
            {
                (true, true) => mine.CompareTo(theirs),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.Compare(_prerelease[i], other._prerelease[i], StringComparison.OrdinalIgnoreCase),
            };
            if (order != 0) return order;
        }

        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    /// <inheritdoc />
    public bool Equals(UpdateVersion? other) => other is not null && CompareTo(other) == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is UpdateVersion other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ToString());

    /// <summary>Compares two versions.</summary>
    /// <param name="left">A version.</param>
    /// <param name="right">Another.</param>
    /// <returns>Whether the first is above the second.</returns>
    public static bool operator >(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) > 0;

    /// <summary>Compares two versions.</summary>
    /// <param name="left">A version.</param>
    /// <param name="right">Another.</param>
    /// <returns>Whether the first is below the second.</returns>
    public static bool operator <(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) < 0;
}
