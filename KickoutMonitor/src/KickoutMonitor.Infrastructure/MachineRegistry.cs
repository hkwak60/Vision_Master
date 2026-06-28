using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class MachineRegistry : IMachineRegistry
{
    public MachineRegistry()
    {
        All =
        [
            Machine("1-1-an", "1-1", Polarity.Anode, "10.112.99.181"),
            Machine("1-1-ca", "1-1", Polarity.Cathode, "10.112.99.182"),
            Machine("1-2-an", "1-2", Polarity.Anode, "10.112.99.66"),
            Machine("1-2-ca", "1-2", Polarity.Cathode, "10.112.99.67"),
            Machine("2-1-an", "2-1", Polarity.Anode, "10.112.99.71"),
            Machine("2-1-ca", "2-1", Polarity.Cathode, "10.112.99.72"),
            Machine("2-2-an", "2-2", Polarity.Anode, "10.112.99.77"),
            Machine("2-2-ca", "2-2", Polarity.Cathode, "10.112.99.78")
        ];
    }

    public IReadOnlyList<WeldingMachine> All { get; }

    public WeldingMachine Get(string id) =>
        All.First(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static WeldingMachine Machine(string id, string line, Polarity polarity, string ip) =>
        new(id, line, polarity, ip, ['E', 'F', 'G']);
}
