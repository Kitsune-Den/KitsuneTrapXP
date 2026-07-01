using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

/// <summary>
/// Server-side settings for KitsuneTrapXP, read once at mod load from
/// <c>Settings.xml</c> in the mod folder root. Currently just the trap-XP
/// baseline: the share of a zombie's XP a trap kill awards the owner, as a
/// fraction (1.0 = 100%). Advanced Engineering's bonus (Config/progression.xml)
/// still stacks on top of this.
///
/// Kept as a plain regex read rather than an XML parser so there's no extra
/// assembly reference and a malformed file can never throw past the try/catch ~
/// worst case we log and fall back to the 100% default.
/// </summary>
public static class TrapXPConfig
{
    public const float DefaultBaseline = 1.0f;

    /// <summary>Baseline XP fraction for a trap kill. 1.0 = 100%.</summary>
    public static float Baseline { get; private set; } = DefaultBaseline;

    public static void Load(string modPath)
    {
        Baseline = DefaultBaseline;
        try
        {
            if (string.IsNullOrEmpty(modPath)) return;

            var path = Path.Combine(modPath, "Settings.xml");
            if (!File.Exists(path))
            {
                Log.Out($"[KitsuneTrapXP] No Settings.xml found; using default baseline {DefaultBaseline:P0}.");
                return;
            }

            var text = File.ReadAllText(path);
            var m = Regex.Match(text, "baseline\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (m.Success
                && float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                && v >= 0f)
            {
                Baseline = v;
                Log.Out($"[KitsuneTrapXP] Baseline trap XP set to {Baseline:P0} from Settings.xml.");
            }
            else
            {
                Log.Warning($"[KitsuneTrapXP] Settings.xml present but 'baseline' was missing or unreadable; using default {DefaultBaseline:P0}.");
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"[KitsuneTrapXP] Failed to read Settings.xml ({ex.Message}); using default baseline {DefaultBaseline:P0}.");
        }
    }
}
