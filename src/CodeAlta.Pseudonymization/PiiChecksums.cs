namespace CodeAlta.Pseudonymization;

/// <summary>The checks of the keys of identifiers, which tell a real number from a run of digits.</summary>
public static class PiiChecksums
{
    /// <summary>Checks the Luhn key of a number (SIREN, SIRET, payment cards).</summary>
    /// <param name="value">The number; spaces, dots and dashes are ignored.</param>
    /// <returns><see langword="true"/> when the key checks out.</returns>
    public static bool IsLuhnValid(ReadOnlySpan<char> value)
    {
        var sum = 0;
        var position = 0;
        for (var index = value.Length - 1; index >= 0; index--)
        {
            var character = value[index];
            if (character is ' ' or '.' or '-' or ' ') continue;
            if (!char.IsAsciiDigit(character)) return false;
            var digit = character - '0';
            if (position++ % 2 == 1)
            {
                digit *= 2;
                if (digit > 9) digit -= 9;
            }

            sum += digit;
        }

        return position > 0 && sum % 10 == 0;
    }

    /// <summary>
    /// Checks the key of a French tax number (numéro fiscal, SPI): thirteen digits, the first 0 to 3, whose last three are
    /// the first ten modulo 511.
    /// </summary>
    /// <param name="value">The number; spaces, dots and dashes are ignored.</param>
    /// <returns><see langword="true"/> when the key checks out.</returns>
    public static bool IsFrenchTaxNumberValid(ReadOnlySpan<char> value)
        => Digits(value, 13, out var number) && number / 1_000_000_000_000 <= 3 && number / 1000 % 511 == number % 1000;

    /// <summary>
    /// Checks the key of the reference of a French tax notice: thirteen digits whose last two are 97 minus the first eleven
    /// modulo 97.
    /// </summary>
    /// <param name="value">The reference; spaces, dots and dashes are ignored.</param>
    /// <returns><see langword="true"/> when the key checks out.</returns>
    public static bool IsFrenchTaxNoticeReferenceValid(ReadOnlySpan<char> value)
        => Digits(value, 13, out var number) && 97 - number / 100 % 97 == number % 100;

    // The digits of a number of a given length, read as one number: separators are skipped, anything else refuses it.
    private static bool Digits(ReadOnlySpan<char> value, int length, out long number)
    {
        number = 0;
        var count = 0;
        foreach (var character in value)
        {
            if (character is ' ' or '.' or '-' or ' ') continue;
            if (!char.IsAsciiDigit(character) || ++count > length) return false;
            number = number * 10 + (character - '0');
        }

        return count == length;
    }

    /// <summary>
    /// Checks the key of a French social security number (NIR): the last two digits are 97 minus the first thirteen
    /// modulo 97. For Corsica, <c>2A</c> counts as 19 and <c>2B</c> as 18 in the department.
    /// </summary>
    /// <param name="value">The fifteen characters of the number; spaces, dots and dashes are ignored.</param>
    /// <returns><see langword="true"/> when the key checks out.</returns>
    public static bool IsNirValid(ReadOnlySpan<char> value)
    {
        Span<char> compact = stackalloc char[15];
        var length = 0;
        foreach (var character in value)
        {
            if (character is ' ' or '.' or '-' or ' ') continue;
            if (length == 15) return false;
            compact[length++] = char.ToUpperInvariant(character);
        }

        if (length != 15 || compact[0] is not ('1' or '2')) return false;
        long body = 0;
        for (var index = 0; index < 13; index++)
        {
            var character = compact[index];
            int digit;
            if (index == 6 && compact[5] == '2' && character is 'A' or 'B')
            {
                // 2A becomes 19 and 2B 18: the department is read as a number below 20.
                body = body / 10 * 100 + (character == 'A' ? 19 : 18);
                continue;
            }

            if (!char.IsAsciiDigit(character)) return false;
            digit = character - '0';
            body = body * 10 + digit;
        }

        if (!char.IsAsciiDigit(compact[13]) || !char.IsAsciiDigit(compact[14])) return false;
        var key = (compact[13] - '0') * 10 + (compact[14] - '0');
        return key == 97 - body % 97;
    }

    /// <summary>
    /// Checks a US employer identification number (EIN): nine digits whose first two are a prefix the IRS gives.
    /// </summary>
    /// <param name="value">The number, such as <c>12-3456789</c>; spaces and dashes are ignored.</param>
    /// <returns><see langword="true"/> when its prefix is one the IRS gives.</returns>
    public static bool IsEinValid(ReadOnlySpan<char> value)
    {
        Span<int> digits = stackalloc int[9];
        if (!Digits(value, digits)) return false;
        var prefix = digits[0] * 10 + digits[1];
        return prefix is (>= 1 and <= 6) or (>= 10 and <= 16) or (>= 20 and <= 27) or (>= 30 and <= 48) or (>= 50 and <= 68)
            or (>= 71 and <= 77) or (>= 80 and <= 88) or (>= 90 and <= 95) or 98 or 99;
    }

    // The digits of a number, which must have exactly as many as the span; spaces, dots and dashes are ignored.
    private static bool Digits(ReadOnlySpan<char> value, Span<int> digits)
    {
        var length = 0;
        foreach (var character in value)
        {
            if (character is ' ' or '.' or '-' or ' ') continue;
            if (length == digits.Length || !char.IsAsciiDigit(character)) return false;
            digits[length++] = character - '0';
        }

        return length == digits.Length;
    }

    /// <summary>Checks the key of an IBAN (ISO 13616): the number, its country moved to its end, is 1 modulo 97.</summary>
    /// <param name="value">The IBAN; spaces are ignored.</param>
    /// <returns><see langword="true"/> when the key checks out.</returns>
    public static bool IsIbanValid(ReadOnlySpan<char> value)
    {
        Span<char> compact = stackalloc char[34];
        var length = 0;
        foreach (var character in value)
        {
            if (character is ' ' or ' ') continue;
            if (length == compact.Length || !char.IsAsciiLetterOrDigit(character)) return false;
            compact[length++] = char.ToUpperInvariant(character);
        }

        if (length < 15 || !char.IsAsciiLetter(compact[0]) || !char.IsAsciiLetter(compact[1]) || !char.IsAsciiDigit(compact[2]) || !char.IsAsciiDigit(compact[3])) return false;
        var remainder = 0;
        for (var index = 0; index < length; index++)
        {
            var character = compact[(index + 4) % length];
            if (char.IsAsciiDigit(character))
            {
                remainder = (remainder * 10 + (character - '0')) % 97;
            }
            else
            {
                var number = character - 'A' + 10;
                remainder = (remainder * 100 + number) % 97;
            }
        }

        return remainder == 1;
    }
}
