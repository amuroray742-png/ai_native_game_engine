using System;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace AiNative.PhysicsSandbox.Hosting
{
    // Unity Mono writes nine significant digits for most floats. v2 uses the shortest
    // round-tripping binary32 spelling produced by the .NET CLI (including its exponent style).
    internal static class PhysicsJsonFloat
    {
        private static readonly bool IsMono = Type.GetType("Mono.Runtime") != null;
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        internal static void Write(Utf8JsonWriter writer, float value)
        {
            if (IsMono)
            {
                writer.WriteRawValue(Format(value), skipInputValidation: true);
            }
            else
            {
                writer.WriteNumberValue(value);
            }
        }

        internal static float Read(JsonElement value) => IsMono ? Parse(value.GetRawText()) : value.GetSingle();

        // JsonDocument has already validated the JSON grammar. Binary32 midpoints have
        // fewer than 160 significant decimal digits (at worst a 25-bit integer times 5^150).
        // Further digits only decide which side of an exact midpoint the token lies on.
        internal static float Parse(string token)
        {
            const int maxDigits = 160;
            int sign = token[0] == '-' ? int.MinValue : 0;
            int index = sign == 0 ? 0 : 1;
            BigInteger digits = 0;
            int significant = 0, fractional = 0;
            bool pastPoint = false, sticky = false;
            for (; index < token.Length && token[index] != 'e' && token[index] != 'E'; index++)
            {
                char c = token[index];
                if (c == '.')
                {
                    pastPoint = true;
                    continue;
                }
                if (pastPoint)
                { fractional++; }
                if (significant == 0 && c == '0')
                { continue; }
                significant++;
                if (significant <= maxDigits)
                { digits = digits * 10 + (c - '0'); }
                else if (c != '0')
                { sticky = true; }
            }
            if (significant == 0)
            { return BitConverter.Int32BitsToSingle(sign); }
            long exponent = 0;
            if (index < token.Length)
            {
                index++;
                bool negative = token[index] == '-';
                if (negative || token[index] == '+')
                { index++; }
                for (; index < token.Length; index++)
                {
                    exponent = Math.Min(1L << 32, exponent * 10 + (token[index] - '0'));
                }
                if (negative)
                { exponent = -exponent; }
            }
            long order = exponent - fractional + significant - 1;
            if (order < -46)
            { return BitConverter.Int32BitsToSingle(sign); }
            if (order > 38)
            { return BitConverter.Int32BitsToSingle(sign | 0x7f800000); }
            int scale = (int)order + 1 - Math.Min(significant, maxDigits);
            BigInteger numerator = digits * BigInteger.Pow(10, Math.Max(0, scale));
            BigInteger denominator = BigInteger.Pow(10, Math.Max(0, -scale));

            int low = -126, high = 128;
            while (high - low > 1)
            {
                int mid = low + (high - low) / 2;
                bool atLeast = mid >= 0 ? numerator >= (denominator << mid) : (numerator << -mid) >= denominator;
                if (atLeast)
                { low = mid; }
                else
                { high = mid; }
            }
            int shift = low - 23;
            numerator <<= Math.Max(0, -shift);
            denominator <<= Math.Max(0, shift);
            BigInteger rounded = BigInteger.DivRem(numerator, denominator, out var remainder);
            int side = (2 * remainder).CompareTo(denominator);
            if (side > 0 || (side == 0 && (sticky || !rounded.IsEven)))
            { rounded++; }
            if (rounded >= 1 << 24)
            {
                rounded >>= 1;
                low++;
            }
            int bits = low > 127 ? 0x7f800000 : (int)rounded + ((low + 126) << 23);
            return BitConverter.Int32BitsToSingle(sign | bits);
        }

        internal static string Format(float value)
        {
            int bits = BitConverter.SingleToInt32Bits(value);
            int magnitude = bits & int.MaxValue;
            string sign = bits < 0 ? "-" : "";
            if (magnitude == 0)
            { return sign + "0"; }
            int field = magnitude >> 23;
            if (field == 255)
            { throw new ArgumentOutOfRangeException(nameof(value)); }
            int mantissa = magnitude & 0x7fffff;
            if (field != 0)
            { mantissa |= 1 << 23; }
            int exponent = Math.Max(1, field) - 127 - 23;

            // Work in exact rational units of one quarter ULP. At a normal power of two
            // the previous float can be nearer than the next. Midpoints round to even.
            BigInteger unit = BigInteger.One << Math.Max(0, exponent - 2);
            BigInteger divisor = BigInteger.One << Math.Max(0, 2 - exponent);
            BigInteger center = 4 * mantissa * unit;
            BigInteger low = center - (field > 1 && mantissa == 1 << 23 ? unit : 2 * unit);
            BigInteger high = center + 2 * unit;
            bool even = (mantissa & 1) == 0;
            int power = (int)Math.Floor(Math.Log10(Math.Abs((double)value)));
            BigInteger factor = BigInteger.Pow(10, Math.Abs(power));
            if (power >= 0)
            { divisor *= factor; }
            else
            { center *= factor; low *= factor; high *= factor; }
            if (center < divisor)
            { center *= 10; low *= 10; high *= 10; power--; }
            else if (center >= 10 * divisor)
            { divisor *= 10; power++; }

            for (int length = 1; length <= 9; length++, power--)
            {
                BigInteger min = BigInteger.DivRem(low, divisor, out var lr);
                if (!even || !lr.IsZero)
                { min++; }
                BigInteger max = BigInteger.DivRem(high, divisor, out var hr);
                if (!even && hr.IsZero)
                { max--; }
                if (min <= max)
                {
                    BigInteger nearest = BigInteger.DivRem(center, divisor, out var remainder);
                    int side = (2 * remainder).CompareTo(divisor);
                    if (side > 0 || (side == 0 && !nearest.IsEven))
                    { nearest++; }
                    int digits = (int)BigInteger.Min(max, BigInteger.Max(min, nearest));
                    return sign + DecimalText(digits, power);
                }
                center *= 10;
                low *= 10;
                high *= 10;
            }
            throw new InvalidOperationException("No round-trip decimal for finite binary32.");
        }

        private static string DecimalText(int digits, int power)
        {
            while (digits % 10 == 0)
            { digits /= 10; power++; }
            string text = digits.ToString(Invariant);
            int exponent = power + text.Length - 1;
            if (exponent < -4 || exponent >= 9)
            {
                string fraction = text.Length == 1 ? text : text.Insert(1, ".");
                return fraction + "E" + (exponent < 0 ? "-" : "+") + Math.Abs(exponent).ToString("D2", Invariant);
            }
            if (power >= 0)
            { return text + new string('0', power); }
            int point = text.Length + power;
            return point > 0 ? text.Insert(point, ".") : "0." + new string('0', -point) + text;
        }
    }
}
