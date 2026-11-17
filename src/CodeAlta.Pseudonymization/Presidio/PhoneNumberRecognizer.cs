using PhoneNumbers;

namespace CodeAlta.Pseudonymization.Presidio;

/// <summary>
/// The phone numbers of some countries, as the <c>PhoneRecognizer</c> of Presidio (MIT, Microsoft) finds them: the numbers
/// libphonenumber (Apache-2.0, Google; its .NET port libphonenumber-csharp) finds valid for one of the regions, with the
/// score 0.4 that words of context raise.
/// </summary>
public sealed class PhoneNumberRecognizer : PresidioPatternRecognizer
{
    /// <summary>The score of a number before its context, Presidio's.</summary>
    public const double PhoneScore = 0.4;

    /// <summary>The regions Presidio looks for by default.</summary>
    public static IReadOnlyList<string> DefaultRegions { get; } = ["US", "GB", "DE", "FR", "IL", "IN", "CA", "BR"];

    private readonly PhoneNumberUtil _numbers = PhoneNumberUtil.GetInstance();
    private readonly string[] _regions;

    /// <summary>Creates the recognizer of the numbers of regions, which finds <see cref="PiiEntities.Phone"/>.</summary>
    /// <param name="regions">The regions, ISO 3166-1 codes such as <c>FR</c> or <c>GB</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="regions"/> is <see langword="null"/>.</exception>
    public PhoneNumberRecognizer(IReadOnlyList<string> regions)
        : base(PiiEntities.Phone, [new("phonenumbers", "(?!)", PhoneScore)], ["phone", "number", "telephone", "cell", "cellphone", "mobile", "call"])
    {
        ArgumentNullException.ThrowIfNull(regions);
        _regions = [.. regions];
    }

    // The fewest digits of a number libphonenumber takes as valid in the regions of the window.
    private const int MinimumDigits = 6;

    private static bool HasDigits(string text, int count)
    {
        foreach (var character in text)
        {
            if (char.IsAsciiDigit(character) && --count == 0) return true;
        }

        return false;
    }

    /// <summary>Gets the regions it looks for.</summary>
    public IReadOnlyList<string> Regions => _regions;

    /// <inheritdoc />
    public override IReadOnlyList<PiiSpan> AnalyzeRaw(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        // A text without the digits of a phone number is not read once per region.
        if (!HasDigits(text, MinimumDigits)) return [];
        List<PiiSpan>? spans = null;
        foreach (var region in _regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var match in _numbers.FindNumbers(text, region, PhoneNumberUtil.Leniency.VALID, long.MaxValue))
                (spans ??= []).Add(new PiiSpan(Entity, match.Start, match.Length, PhoneScore, Name));
        }

        return spans is null ? [] : RemoveDuplicates(spans);
    }
}
