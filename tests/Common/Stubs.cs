using System.Globalization;

namespace Posegraph;

public static class Loc
{
    public static string Lang { get; set; } = "zh";
    public static string T(string text) => text;
    public static string F(string text, params object?[] args)
        => string.Format(CultureInfo.InvariantCulture, text, args);
}

public sealed class Configuration
{
    public string BackendDevice { get; set; } = "auto";
    public int ReleaseAfter { get; set; }
    public bool MhrOnCpu { get; set; } = true;
    public bool FastRecognition { get; set; } = true;
    public bool UseDepth { get; set; } = true;
}
