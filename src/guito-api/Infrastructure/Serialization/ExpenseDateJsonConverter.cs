using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GuitoApi.Exceptions;
using GuitoApi.Model;

namespace GuitoApi.Infrastructure.Serialization;

/// <summary>One offset-aware date input, with legacy Lisbon-midnight compatibility.</summary>
public sealed partial class ExpenseDateJsonConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw InvalidDate();
        var text = reader.GetString()!;
        if (DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            try { return ExpenseTime.Midnight(day); }
            catch (ProblemException exception) { throw new JsonException(exception.Message, exception); }
        }
        if (!Timestamp().IsMatch(text)
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
            throw InvalidDate();
        return ExpenseTime.InLisbon(instant);
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);

    private static JsonException InvalidDate() => new("date must be yyyy-MM-dd or ISO8601 with an explicit offset and at most six fractional digits.");

    [GeneratedRegex(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,6})?(Z|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex Timestamp();
}
