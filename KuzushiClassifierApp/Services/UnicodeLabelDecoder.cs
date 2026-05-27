using System;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace KuzushiClassifierApp.Services;

public static class UnicodeLabelDecoder
{
    public static string Decode(string label, ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(label))
        {
            return label;
        }

        if (!label.StartsWith("U+", StringComparison.OrdinalIgnoreCase))
        {
            return label;
        }

        var hex = label[2..];

        if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var codePoint))
        {
            return label;
        }

        try
        {
            return char.ConvertFromUtf32(codePoint);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            logger?.ZLogWarning(ex, $"Failed to convert code point {codePoint} from hex {hex}");
            return label;
        }
    }
}
