using System.Globalization;
using NxOpen.Foundation.Contracts.Materials;

namespace Core.Assignment.Rules;

/// <summary>Shared parsing for the two coating-related material properties, used by both
/// <see cref="ValidateCoatingDisplayMaterialRule"/> and <see cref="SyncCoatingDisplayMaterialEffectRule"/>
/// so the two rules can't drift apart on what counts as valid coating data.
///
/// NOTE: the RGB source format is not confirmed against real coating property data — it could plausibly
/// be either 0-255 integers or already-normalized 0-1 floats depending on the library, so each component
/// is auto-detected and normalized to 0-1 (see <see cref="TryParseColorComponent"/>). Adjust once a real
/// sample is available, same treatment as the assumed MatML schema.</summary>
internal static class CoatingPropertyReader
{
    public const string MaterialNamePropertyName = "CoatingStudioMaterialName";
    public const string ColorPropertyName = "CoatingVisualizationColor";

    public static MaterialPropertyValue? FindProperty(Material material, string propertyName) =>
        material.Properties.FirstOrDefault(p => string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The coating display material name, or null if the property is missing or blank.</summary>
    public static string? GetDisplayMaterialName(Material material)
    {
        var raw = FindProperty(material, MaterialNamePropertyName)?.AsString();
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    /// <summary>True if <see cref="ColorPropertyName"/> is present and parses into exactly 3 valid
    /// components. Each output is normalized to the 0-1 range: a component &gt; 1 is assumed to be on a
    /// 0-255 scale and divided by 255; a component already in [0, 1] is assumed to be pre-normalized and
    /// used as-is. (This means a raw value of exactly 1 is read as "full intensity," not "1 out of 255"
    /// — an inherent ambiguity of auto-detecting the scale, not a bug.)</summary>
    public static bool TryGetRgb(Material material, out double r, out double g, out double b)
    {
        r = g = b = 0;

        var colorProperty = FindProperty(material, ColorPropertyName);
        if (colorProperty is null)
            return false;

        var components = colorProperty.AsArray();
        if (components.Count != 3)
            return false;

        return TryParseColorComponent(components[0], out r)
            && TryParseColorComponent(components[1], out g)
            && TryParseColorComponent(components[2], out b);
    }

    private static bool TryParseColorComponent(string raw, out double value)
    {
        value = 0;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return false;
        if (parsed < 0)
            return false;

        var normalized = parsed > 1 ? parsed / 255 : parsed;
        if (normalized > 1)
            return false; // original was > 255 on the assumed 0-255 scale

        value = normalized;
        return true;
    }

    private const double ColorTolerance = 1.0 / 255.0;

    /// <summary>True if each channel of the normalized 0-1 <paramref name="r"/>/<paramref name="g"/>/
    /// <paramref name="b"/> is within one 0-255 step of <paramref name="rgb"/> once converted to the same
    /// 0-1 scale. Used to compare a library material's coating color against a body's current display
    /// material color without floating-point rounding causing a false mismatch.</summary>
    public static bool ColorsApproximatelyEqual(double r, double g, double b, (byte R, byte G, byte B) rgb) =>
        Math.Abs(r - rgb.R / 255.0) <= ColorTolerance
        && Math.Abs(g - rgb.G / 255.0) <= ColorTolerance
        && Math.Abs(b - rgb.B / 255.0) <= ColorTolerance;
}
