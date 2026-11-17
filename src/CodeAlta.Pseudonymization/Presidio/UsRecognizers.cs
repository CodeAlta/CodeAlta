namespace CodeAlta.Pseudonymization.Presidio;

/// <summary>
/// The recognizers of the identifiers of the United States, ported from the predefined recognizers of Presidio (MIT,
/// Microsoft; <c>predefined_recognizers/country_specific/us</c>): their patterns, scores, words of context and checks are
/// Presidio's. The healthcare administration identifiers (claims, prior authorizations, referrals...) are not ported.
/// </summary>
public static class UsRecognizers
{
    /// <summary>Creates the recognizers, a new set for each analyzer.</summary>
    /// <returns>The recognizers.</returns>
    public static IReadOnlyList<PresidioPatternRecognizer> Create() =>
    [
        new(PiiEntities.UsSsn,
        [
            new("SSN1 (very weak)", @"\b([0-9]{5})-([0-9]{4})\b", 0.05),
            new("SSN2 (very weak)", @"\b([0-9]{3})-([0-9]{6})\b", 0.05),
            new("SSN3 (very weak)", @"\b(([0-9]{3})-([0-9]{2})-([0-9]{4}))\b", 0.05),
            new("SSN4 (very weak)", @"\b[0-9]{9}\b", 0.05),
            new("SSN5 (medium)", @"\b([0-9]{3})[- .]([0-9]{2})[- .]([0-9]{4})\b", 0.5),
        ], ["social", "security", "ssn", "ssns", "ssid"])
        { Invalidate = IsInvalidSsn },
        new(PiiEntities.UsItin,
        [
            new("Itin (very weak)", @"\b9\d{2}[- ](5\d|6[0-5]|7\d|8[0-8]|9([0-2]|[4-9]))\d{4}\b|\b9\d{2}(5\d|6[0-5]|7\d|8[0-8]|9([0-2]|[4-9]))[- ]\d{4}\b", 0.05),
            new("Itin (weak)", @"\b9\d{2}(5\d|6[0-5]|7\d|8[0-8]|9([0-2]|[4-9]))\d{4}\b", 0.3),
            new("Itin (medium)", @"\b9\d{2}[- ](5\d|6[0-5]|7\d|8[0-8]|9([0-2]|[4-9]))[- ]\d{4}\b", 0.5),
        ], ["individual", "taxpayer", "itin", "tax", "payer", "taxid", "tin"]),
        new(PiiEntities.UsPassport,
        [
            new("Passport (very weak)", @"(\b[0-9]{9}\b)", 0.05),
            new("Passport Next Generation (very weak)", @"(\b[A-Z][0-9]{8}\b)", 0.1),
        ], ["us", "united", "states", "passport", "passport#", "travel", "document"]),
        new(PiiEntities.UsBankNumber, [new("Bank Account (weak)", @"\b[0-9]{8,17}\b", 0.05)],
            ["check", "account", "account#", "acct", "bank", "save", "debit"]),
        new(PiiEntities.UsDriverLicense,
        [
            new("Driver License - Alphanumeric (weak)",
                @"\b([A-Z][0-9]{3,6}|[A-Z][0-9]{5,9}|[A-Z][0-9]{6,8}|[A-Z][0-9]{4,8}|[A-Z][0-9]{9,11}|[A-Z]{1,2}[0-9]{5,6}|H[0-9]{8}|V[0-9]{6}|X[0-9]{8}|A-Z]{2}[0-9]{2,5}|[A-Z]{2}[0-9]{3,7}|[0-9]{2}[A-Z]{3}[0-9]{5,6}|[A-Z][0-9]{13,14}|[A-Z][0-9]{18}|[A-Z][0-9]{6}R|[A-Z][0-9]{9}|[A-Z][0-9]{1,12}|[0-9]{9}[A-Z]|[A-Z]{2}[0-9]{6}[A-Z]|[0-9]{8}[A-Z]{2}|[0-9]{3}[A-Z]{2}[0-9]{4}|[A-Z][0-9][A-Z][0-9][A-Z]|[0-9]{7,8}[A-Z])\b",
                0.3),
            new("Driver License - Digits (very weak)", @"\b([0-9]{6,14}|[0-9]{16})\b", 0.01),
        ], ["driver", "license", "permit", "lic", "identification", "dls", "cdls", "lic#", "driving"]),
        new(PiiEntities.AbaRoutingNumber,
        [
            new("ABA routing number (weak)", @"\b[0123678]\d{8}\b", 0.05),
            new("ABA routing number", @"\b[0123678]\d{3}-\d{4}-\d\b", 0.3),
        ], ["aba", "routing", "abarouting", "association", "bankrouting"])
        { Validate = static value => IsAbaRoutingValid(Sanitize(value, '-')) },
        new(PiiEntities.UsNpi,
        [
            new("NPI (weak)", @"\b[12]\d{9}\b", 0.1),
            new("NPI (medium)", @"\b[12]\d{3}[ -]\d{3}[ -]\d{3}\b", 0.4),
        ], ["npi", "national provider", "provider", "npi number", "provider id", "provider identifier", "taxonomy"])
        {
            Validate = static value => IsNpiValid(Sanitize(value, '-', ' ')),
            Invalidate = static value => Sanitize(value, '-', ' ') is { Length: > 0 } digits && digits[..^1].Distinct().Count() == 1,
        },
        new(PiiEntities.UsMbi,
        [
            new("MBI (weak)", @"\b" + MbiNoDash + @"\b", 0.3),
            new("MBI (medium)", @"\b" + MbiWithDash + @"\b", 0.5),
        ], ["medicare", "mbi", "beneficiary", "cms", "medicaid", "hic", "hicn"]),
        new(PiiEntities.MedicalLicense,
        [
            new("USA DEA Certificate Number (weak)", @"[abcdefghjklmprstuxABCDEFGHJKLMPRSTUX]{1}[a-zA-Z]{1}\d{7}|[abcdefghjklmprstuxABCDEFGHJKLMPRSTUX]{1}9\d{7}", 0.4),
        ], ["medical", "certificate", "DEA"])
        { Validate = static value => IsDeaValid(Sanitize(value, '-', ' ')) },
    ];

    // The letters of an MBI: A to Z without S, L, O, I, B and Z.
    private const string MbiLetter = "[ACDEFGHJKMNPQRTUVWXY]";
    private const string MbiAlphanumeric = "[0-9ACDEFGHJKMNPQRTUVWXY]";
    private const string MbiNoDash = "[0-9]" + MbiLetter + MbiAlphanumeric + "[0-9]" + MbiLetter + MbiAlphanumeric + "[0-9]" + MbiLetter + MbiLetter + "[0-9][0-9]";
    private const string MbiWithDash = "[0-9]" + MbiLetter + MbiAlphanumeric + "[0-9]-" + MbiLetter + MbiAlphanumeric + "[0-9]-" + MbiLetter + MbiLetter + "[0-9][0-9]";

    /// <summary>Removes characters from a value, as <c>sanitize_value</c> does with its replacement pairs.</summary>
    internal static string Sanitize(string value, params ReadOnlySpan<char> removed)
    {
        var buffer = new System.Text.StringBuilder(value.Length);
        foreach (var character in value) if (!removed.Contains(character)) buffer.Append(character);
        return buffer.ToString();
    }

    // UsSsnRecognizer.invalidate_result: one kind of delimiter, not one digit, no group of zeros, no area 000 or 666, no sample.
    private static bool IsInvalidSsn(string value)
    {
        if (value.Where(static character => character is '.' or '-' or ' ').Distinct().Count() > 1) return true;
        var digits = new string([.. value.Where(char.IsDigit)]);
        if (digits.Length == 0 || digits.All(character => character == digits[0])) return true;
        if (digits.Length >= 5 && digits[3..5] == "00" || digits.Length > 5 && digits[5..] == "0000") return true;
        if (digits.Length >= 3 && digits[..3] is "000" or "666") return true;
        return digits is "123456789" or "987654320" or "078051120";
    }

    // AbaRoutingRecognizer: the nine digits weighted 3, 7, 1 make a multiple of 10.
    private static bool IsAbaRoutingValid(string digits)
    {
        if (digits.Length < 9 || !digits.All(char.IsAsciiDigit)) return false;
        int[] weights = [3, 7, 1, 3, 7, 1, 3, 7, 1];
        var sum = 0;
        for (var index = 0; index < 9; index++) sum += (digits[index] - '0') * weights[index];
        return sum % 10 == 0;
    }

    // UsNpiRecognizer: the Luhn key of the ten digits behind the prefix 80840.
    private static bool IsNpiValid(string digits)
        => digits.All(char.IsAsciiDigit) && PiiChecksums.IsLuhnValid("80840" + digits);

    // MedicalLicenseRecognizer: the DEA key, over the digits after the two letters.
    private static bool IsDeaValid(string value)
    {
        if (value.Length < 4) return false;
        var digits = value[2..];
        if (!digits.All(char.IsAsciiDigit)) return false;
        var list = digits.Select(static character => character - '0').ToList();
        var checksum = -list[^1];
        list.RemoveAt(list.Count - 1);
        for (int index = list.Count - 1, position = 0; index >= 0; index--, position++) checksum += position % 2 == 0 ? 2 * list[index] : list[index];
        return checksum % 10 == 0;
    }
}
