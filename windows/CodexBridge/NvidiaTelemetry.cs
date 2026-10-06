using System.Globalization;

namespace CodexBridge;

static class NvidiaTelemetry
{
    // Match the selected LHM card by name, never by unrelated NVAPI/SMI indexes.
    // Identical models are ambiguous: keep LHM readings rather than overwrite
    // them with another card's temperature or VRAM.
    public static (float? Temp, float? FanPct, float? VramUsedMb, float? VramTotalMb)? Parse(string output, string selectedName)
    {
        var rows = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(row => row.Split(',', StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 5 && string.Equals(parts[0], selectedName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (rows.Count != 1)
        {
            return null;
        }
        var row = rows[0];
        static float? Number(string value) => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && float.IsFinite(number) ? number : null;
        return (Number(row[1]), Number(row[2]), Number(row[3]), Number(row[4]));
    }
}
