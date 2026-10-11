using System.Globalization;
using System.Numerics;
namespace GuitoApi.Services.Import;

internal static class ExactImportTotal
{
    public static string Sum(IEnumerable<decimal> values)
    {
        var total = BigInteger.Zero;
        foreach (var value in values)
        {
            var bits = decimal.GetBits(value);
            var coefficient = (BigInteger)(uint)bits[0] + ((BigInteger)(uint)bits[1] << 32) + ((BigInteger)(uint)bits[2] << 64);
            var scale = (bits[3] >> 16) & 255;
            total += ((bits[3] & int.MinValue) == 0 ? coefficient : -coefficient) * BigInteger.Pow(10, 28 - scale);
        }
        var digits = BigInteger.Abs(total).ToString(CultureInfo.InvariantCulture).PadLeft(29, '0');
        var text = (total.Sign < 0 ? "-" : "") + digits[..^28] + "." + digits[^28..];
        return text.TrimEnd('0').TrimEnd('.');
    }
}
