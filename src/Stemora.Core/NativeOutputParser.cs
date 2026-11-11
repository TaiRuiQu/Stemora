using System.Globalization;
using System.Text.RegularExpressions;

namespace Stemora.Core;

public static partial class NativeOutputParser
{
    [GeneratedRegex(@"(?<!\d)(\d{1,3})\s*%")]
    private static partial Regex PercentPattern();
    public static double? ParseFraction(string line)
    {
        var match = PercentPattern().Match(line);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var percent)
            && percent <= 100 ? percent / 100d : null;
    }
}
