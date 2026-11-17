using System.Text.RegularExpressions;

namespace CodeAlta.Pseudonymization.Presidio;

/// <summary>
/// The recognizers of the identifiers of the United Kingdom, ported from the predefined recognizers of Presidio (MIT,
/// Microsoft; <c>predefined_recognizers/country_specific/uk</c>): their patterns, scores, words of context and checks are
/// Presidio's.
/// </summary>
public static partial class UkRecognizers
{
    /// <summary>Creates the recognizers, a new set for each analyzer.</summary>
    /// <returns>The recognizers.</returns>
    public static IReadOnlyList<PresidioPatternRecognizer> Create() =>
    [
        new(PiiEntities.UkNhs, [new("NHS (medium)", @"\b([0-9]{3})[- ]?([0-9]{3})[- ]?([0-9]{4})\b", 0.5)],
            ["national health service", "nhs", "health services authority", "health authority"])
        { Validate = static value => IsNhsValid(UsRecognizers.Sanitize(value, '-', ' ')) },
        new(PiiEntities.UkNino,
        [
            new("NINO (medium)", @"\b(?!bg|gb|nk|kn|nt|tn|zz|BG|GB|NK|KN|NT|TN|ZZ)([a-ceghj-pr-tw-zA-CEGHJ-PR-TW-Z]{1}[a-ceghj-npr-tw-zA-CEGHJ-NPR-TW-Z]{1}) ?([0-9]{2}) ?([0-9]{2}) ?([0-9]{2}) ?([a-dA-D]{1})\b", 0.5),
        ], ["national insurance", "ni number", "nino"]),
        new(PiiEntities.UkPassport, [new("UK Passport (weak)", @"\b[A-Z]{2}\d{7}\b", 0.1)],
            ["passport", "passport number", "travel document", "uk passport", "british passport", "her majesty", "his majesty", "hm passport", "hmpo"]),
        new(PiiEntities.UkPostcode,
        [
            new("UK Postcode",
                @"\b(GIR\s?0AA|[A-PR-UWYZ][0-9][ABCDEFGHJKPSTUW]?\s?[0-9][ABD-HJLNP-UW-Z]{2}|[A-PR-UWYZ][0-9]{2}\s?[0-9][ABD-HJLNP-UW-Z]{2}|[A-PR-UWYZ][A-HK-Y][0-9][ABEHMNPRVWXY]?\s?[0-9][ABD-HJLNP-UW-Z]{2}|[A-PR-UWYZ][A-HK-Y][0-9]{2}\s?[0-9][ABD-HJLNP-UW-Z]{2})\b",
                0.1),
        ], ["postcode", "post code", "postal code", "zip", "address", "delivery", "mailing", "shipping", "correspondence"]),
        new(PiiEntities.UkDrivingLicence,
        [
            new("UK Driving Licence", @"\b[A-Z9]{5}[0-9](?:0[1-9]|1[0-2]|5[1-9]|6[0-2])(?:0[1-9]|[12][0-9]|3[01])[0-9][A-Z9]{2}[A-Z0-9][A-Z]{2}\b", 0.5),
        ], ["driving licence", "driving license", "driver's licence", "driver's license", "dvla", "dl number", "licence number", "license number"])
        { Validate = IsDrivingLicencePlausible },
        new(PiiEntities.UkVehicleRegistration,
        [
            new("UK Vehicle Registration (current)", @"\b[A-HJ-PR-Y][A-HJ-PR-Y](?:0[1-9]|[1-7][0-9])[- ]?[A-HJ-PR-Z]{3}\b", 0.3),
            new("UK Vehicle Registration (prefix)", @"\b[A-HJ-NPR-TV-Y]\d{1,3}[- ]?[A-HJ-PR-Y][A-HJ-PR-Z]{2}\b", 0.2),
            new("UK Vehicle Registration (suffix)", @"\b[A-HJ-PR-Z]{3}[- ]?\d{1,3}[- ]?[A-HJ-NPR-TV-Y]\b", 0.15),
        ], ["vehicle", "registration", "number plate", "licence plate", "license plate", "reg", "vrn", "dvla", "v5c", "logbook", "mot", "car", "insured vehicle"])
        { Validate = static value => VehicleRegistrationAge(UsRecognizers.Sanitize(value, '-', ' ')) },
    ];

    // NhsRecognizer: the ten digits weighted 10 down to 1 make a multiple of 11.
    private static bool IsNhsValid(string digits)
    {
        if (!digits.All(char.IsAsciiDigit)) return false;
        var total = 0;
        for (var index = 0; index < Math.Min(digits.Length, 11); index++) total += (digits[index] - '0') * (10 - index);
        return total % 11 == 0;
    }

    // UkDrivingLicenceRecognizer: its check digits are not public, so it only drops a surname of 9s or with 9s not trailing.
    private static bool? IsDrivingLicencePlausible(string value)
    {
        var surname = value.ToUpperInvariant()[..5];
        if (surname == "99999") return false;
        return SurnamePattern().IsMatch(surname) ? null : false;
    }

    // UkVehicleRegistrationRecognizer: a current plate has an age identifier from 02 to 29 or from 51 to 79; the older
    // formats keep their score.
    private static bool? VehicleRegistrationAge(string value)
    {
        if (value.Length == 7 && char.IsLetter(value[0]) && char.IsLetter(value[1]) && char.IsAsciiDigit(value[2]) && char.IsAsciiDigit(value[3]))
        {
            var age = (value[2] - '0') * 10 + (value[3] - '0');
            return age is >= 2 and <= 29 or >= 51 and <= 79;
        }

        return null;
    }

    [GeneratedRegex("^[A-Z]+9*$", RegexOptions.CultureInvariant)]
    private static partial Regex SurnamePattern();
}
