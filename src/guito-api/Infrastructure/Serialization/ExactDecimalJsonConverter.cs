using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GuitoApi.Infrastructure.Serialization;

public sealed partial class ExactDecimalJsonConverter : JsonConverter<decimal>
{
    private static readonly BigInteger MaximumCoefficient = (BigInteger.One << 96) - 1;

    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString()!,
            JsonTokenType.Number => reader.HasValueSequence
                ? Encoding.UTF8.GetString(reader.ValueSequence.ToArray())
                : Encoding.UTF8.GetString(reader.ValueSpan),
            _ => throw InvalidAmount()
        };
        return ParseExact(text);
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));

    private static decimal ParseExact(string text)
    {
        var match = DecimalToken().Match(text.Trim());
        if (!match.Success) throw InvalidAmount();
        var digits = $"{match.Groups["whole"].Value}{match.Groups["fraction"].Value}";
        if (digits.Length == 0) throw InvalidAmount();
        var exponent = match.Groups["exponent"].Success
            ? BigInteger.Parse(match.Groups["exponent"].Value, CultureInfo.InvariantCulture)
            : BigInteger.Zero;
        var scale = match.Groups["fraction"].Length - exponent;
        var significantDigits = digits.TrimStart('0').TrimEnd('0');
        if (significantDigits.Length == 0) return decimal.Zero;
        scale -= digits.Length - digits.TrimEnd('0').Length;
        return CreateExact(significantDigits, scale, match.Groups["sign"].Value == "-");
    }

    private static decimal CreateExact(string digits, BigInteger scale, bool negative)
    {
        // Canonical coefficient/scale preserves numeric equality, not token spelling.
        // Bound before exponentiation: enormous exponents must not allocate enormous powers.
        if (scale > 28 || digits.Length > 29 || digits.Length - BigInteger.Min(scale, 0) > 29)
            throw InvalidAmount();
        var coefficient = BigInteger.Parse(digits, CultureInfo.InvariantCulture);
        if (scale < 0) coefficient *= BigInteger.Pow(10, (int)-scale);
        if (coefficient > MaximumCoefficient) throw InvalidAmount();
        // Construct the exact 96-bit value directly; decimal.TryParse could silently round.
        return new decimal(Limb(coefficient), Limb(coefficient >> 32), Limb(coefficient >> 64),
            negative, (byte)BigInteger.Max(scale, 0));
    }

    private static int Limb(BigInteger coefficient) => unchecked((int)(uint)(coefficient & uint.MaxValue));

    private static JsonException InvalidAmount() => new("Amount must be an exactly representable decimal.");

    [GeneratedRegex(@"\A(?<sign>[+-]?)(?<whole>[0-9]*)(?:\.(?<fraction>[0-9]*))?(?:[eE](?<exponent>[+-]?[0-9]+))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalToken();
}
