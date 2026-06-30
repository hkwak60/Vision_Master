using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class MachineRegistry : IMachineRegistry
{
    public MachineRegistry(VisionMasterSettings? settings = null)
    {
        settings ??= VisionMasterSettings.CreateDefault();
        All = settings.Machines
            .Where(machine => machine.Enabled)
            .Select(ToMachine)
            .ToArray();
    }

    public IReadOnlyList<WeldingMachine> All { get; }

    public WeldingMachine Get(string id) =>
        All.First(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static WeldingMachine ToMachine(MachineSetting machine) => new(
        machine.Id,
        machine.Line,
        machine.Polarity,
        machine.IpAddress,
        machine.ImageDrives
            .Where(drive => !string.IsNullOrWhiteSpace(drive))
            .Select(drive => char.ToUpperInvariant(drive.Trim()[0]))
            .ToArray(),
        machine.Model,
        string.IsNullOrWhiteSpace(machine.DataDrive) ? 'D' : char.ToUpperInvariant(machine.DataDrive.Trim()[0]),
        machine.Enabled);
}
