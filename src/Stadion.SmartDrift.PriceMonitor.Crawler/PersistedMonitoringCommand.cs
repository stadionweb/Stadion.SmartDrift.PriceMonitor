using System.Globalization;
using Stadion.SmartDrift.PriceMonitor.Core;

namespace Stadion.SmartDrift.PriceMonitor.Crawler;

public static class PersistedMonitoringCommand
{
    public static bool TryParse(string[] args, out MonitoringSelection? selection, out string? fixture)
    {
        selection = null;
        fixture = null;
        if (args.Length is < 4 or > 8 || args.Length % 2 != 0 ||
            args[0] != "--run" || args[1] != "persisted-monitoring")
            return false;
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 2; i < args.Length; i += 2)
        {
            if (args[i] is not ("--monitoring-target-id" or "--gtin" or "--offline-fixture") ||
                string.IsNullOrWhiteSpace(args[i + 1]) || !options.TryAdd(args[i], args[i + 1]))
                return false;
        }
        if (!options.TryGetValue("--monitoring-target-id", out var id) ||
            !long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var targetId) || targetId <= 0)
            return false;
        var gtin = options.GetValueOrDefault("--gtin");
        if (gtin is not null && (gtin.Length is < 8 or > 20 || !gtin.All(char.IsAsciiDigit)))
            return false;
        fixture = options.GetValueOrDefault("--offline-fixture");
        if (fixture is not null && !Path.IsPathFullyQualified(fixture))
            return false;
        selection = new(targetId, gtin);
        return true;
    }
}
