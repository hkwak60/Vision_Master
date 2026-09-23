using KickoutMonitor.Application;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class IrsRawImageLocator : IIrsRawImageLocator
{
    private readonly ProductionInspectionResolver _resolver;
    public IrsRawImageLocator(ISharePathResolver shares, IDailyCsvLocator csvs, VisionMasterSettings? settings = null)
        => _resolver = new(csvs, shares);
    public void Reset() => _resolver.Reset();
    public Task<IrsImageLookupResult> FindAsync(WeldingMachine machine, IrsReviewCandidate candidate, CancellationToken token)
        => _resolver.ResolveAsync(machine, candidate, token);
}
