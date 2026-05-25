namespace KuzushiClassifierApp.Services;

public static class UnicodeLabelDecoder
{
    public static string Decode(string label)
    {
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
        catch (ArgumentOutOfRangeException)
        {
            return label;
        }
    }
}
