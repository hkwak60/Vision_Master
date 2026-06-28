using System.Text.RegularExpressions;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public static partial class ProductionPathMapper
{
    [GeneratedRegex(@"^(?<drive>[A-Za-z]):\\(?<rest>.+)$")]
    private static partial Regex DrivePath();

    public static string ToUnc(
        WeldingMachine machine,
        string productionPath,
        ISharePathResolver? shares = null)
    {
        if (productionPath.StartsWith(@"\\", StringComparison.Ordinal)) return productionPath;
        var match = DrivePath().Match(productionPath);
        if (!match.Success) return productionPath;
        var drive = char.ToUpperInvariant(match.Groups["drive"].Value[0]);
        var root = shares?.GetRoot(machine, drive) ?? machine.ShareRoot(drive);
        return $@"{root}\{match.Groups["rest"].Value}";
    }
}
