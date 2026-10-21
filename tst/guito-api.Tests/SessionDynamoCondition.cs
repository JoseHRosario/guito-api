using System.Globalization;
using System.Text.RegularExpressions;
using Amazon.DynamoDBv2.Model;

namespace GuitoApi.Tests;

// Evaluate the submitted predicate, not a canned interpretation of the intended operation.
internal sealed class SessionDynamoCondition
{
    private readonly string[] _tokens;
    private readonly IReadOnlyDictionary<string, AttributeValue> _item;
    private readonly IReadOnlyDictionary<string, AttributeValue> _values;
    private int _position;

    public SessionDynamoCondition(string expression, IReadOnlyDictionary<string, AttributeValue> item,
        IReadOnlyDictionary<string, AttributeValue>? values)
    {
        _tokens = Regex.Matches(expression, @"attribute_not_exists|attribute_exists|[a-zA-Z_:][a-zA-Z_0-9:]*|<=|>=|<>|[()=<>]")
            .Select(match => match.Value).ToArray();
        _item = item;
        _values = values ?? new Dictionary<string, AttributeValue>();
    }

    public bool Evaluate()
    {
        if (_tokens.Length == 0) return true;
        var result = Or();
        Assert.Equal(_tokens.Length, _position);
        return result;
    }

    private bool Or()
    {
        var result = And();
        while (Take("OR")) { var right = And(); result |= right; }
        return result;
    }

    private bool And()
    {
        var result = Atom();
        while (Take("AND")) { var right = Atom(); result &= right; }
        return result;
    }

    private bool Atom()
    {
        if (Take("(")) { var result = Or(); Expect(")"); return result; }
        var name = _tokens[_position++];
        if (name is "attribute_exists" or "attribute_not_exists") return Exists(name);
        var comparison = _tokens[_position++];
        var right = _values[_tokens[_position++]];
        if (!_item.TryGetValue(name, out var left)) return false;
        var order = left.N is not null && right.N is not null
            ? decimal.Parse(left.N, CultureInfo.InvariantCulture).CompareTo(decimal.Parse(right.N, CultureInfo.InvariantCulture))
            : string.CompareOrdinal(left.S, right.S);
        return comparison switch { "=" => order == 0, ">" => order > 0, "<=" => order <= 0, _ => throw new NotSupportedException(comparison) };
    }

    private bool Exists(string function)
    {
        Expect("(");
        var exists = _item.ContainsKey(_tokens[_position++]);
        Expect(")");
        return function == "attribute_exists" ? exists : !exists;
    }

    private bool Take(string token)
    {
        if (_position >= _tokens.Length || _tokens[_position] != token) return false;
        _position++;
        return true;
    }

    private void Expect(string token) => Assert.True(Take(token), $"Expected {token}");
}
