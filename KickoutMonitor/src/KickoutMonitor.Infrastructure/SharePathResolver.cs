using System.Collections.Concurrent;
using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class SharePathResolver : ISharePathResolver
{
    private readonly ConcurrentDictionary<string, string> _roots =
        new(StringComparer.OrdinalIgnoreCase);

    public string GetRoot(WeldingMachine machine, char drive)
    {
        var key = Key(machine, drive);
        return _roots.TryGetValue(key, out var root)
            ? root
            : machine.ShareRoot(drive);
    }

    public void RecordAccessibleRoot(WeldingMachine machine, char drive, string root) =>
        _roots[Key(machine, drive)] = root.TrimEnd('\\');

    private static string Key(WeldingMachine machine, char drive) =>
        $"{machine.IpAddress}|{char.ToUpperInvariant(drive)}";
}
