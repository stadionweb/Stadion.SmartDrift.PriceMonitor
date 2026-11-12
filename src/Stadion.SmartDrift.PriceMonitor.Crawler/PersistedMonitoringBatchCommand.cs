using System.Globalization;
using Stadion.SmartDrift.PriceMonitor.Core;

namespace Stadion.SmartDrift.PriceMonitor.Crawler;

public static class PersistedMonitoringBatchCommand
{
    public const int HardMaxTargets = MonitoringBatchSelection.HardMaxTargets;

    public static bool TryParse(string[] args, out MonitoringBatchSelection? selection, out string? fixture)
    {
        selection = null;
        fixture = null;
        if (args.Length < 2 || args.Length > 8 || args.Length % 2 != 0 ||
            args[0] != "--run" || args[1] != "persisted-monitoring-batch")
            return false;

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 2; i < args.Length; i += 2)
        {
            if (args[i] is not ("--website-key" or "--max-targets" or "--offline-fixture") ||
                string.IsNullOrWhiteSpace(args[i + 1]) || !options.TryAdd(args[i], args[i + 1]))
                return false;
        }

        var websiteKey = options.GetValueOrDefault("--website-key");
        if (websiteKey is not null && (websiteKey.Length > 100 || websiteKey != websiteKey.Trim()))
            return false;

        int? maxTargets = null;
        if (options.TryGetValue("--max-targets", out var max) &&
            (!int.TryParse(max, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
             parsed is < 1 or > HardMaxTargets))
            return false;
        else if (max is not null)
            maxTargets = int.Parse(max, NumberStyles.None, CultureInfo.InvariantCulture);

        fixture = options.GetValueOrDefault("--offline-fixture");
        if (fixture is not null && !Path.IsPathFullyQualified(fixture))
            return false;

        selection = new(websiteKey, maxTargets);
        return true;
    }
}
