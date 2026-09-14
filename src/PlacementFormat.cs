using CounterStrikeSharp.API.Modules.Utils;
using System.Globalization;

namespace WorldText
{
    // The only format for placement coordinates, used by both JSON and the database.
    // Writes invariant "0.###": "1896.372 -5942.969 14312.03", "0 180 90"
    // Also reads the legacy Vector.ToString() form, e.g. "1,822.49 417.00 1,256.00"
    internal static class PlacementFormat
    {
        private const string NumberFormat = "0.###";

        private const NumberStyles ReadStyles = NumberStyles.Float | NumberStyles.AllowThousands;

        private static readonly char[] Separators = [' ', '\t'];

        public static string Format(float value) =>
            value.ToString(NumberFormat, CultureInfo.InvariantCulture);

        public static string Format(float a, float b, float c) =>
            $"{Format(a)} {Format(b)} {Format(c)}";

        public static string Format(Vector v) => Format(v.X, v.Y, v.Z);

        public static string Format(QAngle a) => Format(a.X, a.Y, a.Z);

        // Parses three space separated floats, returns false on anything else.
        public static bool TryParse(string? input, out float a, out float b, out float c)
        {
            a = b = c = 0f;

            if (string.IsNullOrWhiteSpace(input))
                return false;

            var parts = input.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 3)
                return false;

            return float.TryParse(parts[0], ReadStyles, CultureInfo.InvariantCulture, out a)
                && float.TryParse(parts[1], ReadStyles, CultureInfo.InvariantCulture, out b)
                && float.TryParse(parts[2], ReadStyles, CultureInfo.InvariantCulture, out c);
        }

        public static bool TryParseVector(string? input, out Vector result)
        {
            if (!TryParse(input, out var x, out var y, out var z))
            {
                result = new Vector(0, 0, 0);
                return false;
            }

            result = new Vector(x, y, z);
            return true;
        }

        public static bool TryParseQAngle(string? input, out QAngle result)
        {
            if (!TryParse(input, out var pitch, out var yaw, out var roll))
            {
                result = new QAngle(0, 0, 0);
                return false;
            }

            result = new QAngle(pitch, yaw, roll);
            return true;
        }

        public static Vector ParseVector(string? input) =>
            TryParseVector(input, out var result)
                ? result
                : throw new ArgumentException($"Invalid vector string format: '{input}'.");

        public static QAngle ParseQAngle(string? input) =>
            TryParseQAngle(input, out var result)
                ? result
                : throw new ArgumentException($"Invalid angle string format: '{input}'.");
    }
}
