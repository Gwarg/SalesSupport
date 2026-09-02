namespace SalesSupport.Backend.Telephony;

/// <summary>
/// Normalizes caller strings to E.164-ish ("+4681234567") so a webhook's "08-123 45 67",
/// "+46 8 123 45 67", "+46 (0)8 123 45 67" and "0046812345 67" all hit the same index
/// row. Trunk-zero national numbers get the installation's default country code.
/// Hidden callers ("anonymous"), extensions and garbage normalize to null.
/// </summary>
public static class PhoneNumbers
{
    public static string? Normalize(string? raw, string defaultCountryCode = "+46")
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim().Replace("(0)", "");
        var international = trimmed.StartsWith('+');
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        if (digits.Length < 5) return null;

        var cc = defaultCountryCode.TrimStart('+');
        if (international) return "+" + digits;
        if (digits.StartsWith("00", StringComparison.Ordinal)) return "+" + digits[2..];
        if (digits.StartsWith('0')) return "+" + cc + digits[1..];
        if (digits.StartsWith(cc, StringComparison.Ordinal) && digits.Length >= 10) return "+" + digits;
        return "+" + cc + digits;
    }
}
