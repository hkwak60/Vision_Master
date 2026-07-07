using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class AppStorage
{
    public AppStorage(string? root) : this(null, root)
    {
    }

    public AppStorage(VisionMasterSettings? settings = null, string? root = null)
    {
        RequestedRoot = root
            ?? Environment.GetEnvironmentVariable("KICKOUT_MONITOR_ROOT")
            ?? settings?.StorageRoot
            ?? @"E:\KWAK\VisionMaster";
        Root = ResolveRoot(RequestedRoot);
        Staging = Path.Combine(Root, ".staging");
        Temp = Path.Combine(Root, ".temp");
        Summary = Path.Combine(Root, "NG_Summary");
        ReviewFile = Path.Combine(Root, "kickout-reviews.json");
        DlngReviewFile = Path.Combine(Root, "dlng-reviews.json");
        DlngReport = Path.Combine(Root, "DLNG_REPORT");
    }

    public string RequestedRoot { get; }
    public string Root { get; }
    public string Staging { get; }
    public string Temp { get; }
    public string Summary { get; }
    public string ReviewFile { get; }
    public string DlngReviewFile { get; }
    public string DlngReport { get; }

    public void EnsureCreated(IEnumerable<WeldingMachine> machines)
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Staging);
        Directory.CreateDirectory(Temp);
        Directory.CreateDirectory(Summary);
        Directory.CreateDirectory(DlngReport);
        foreach (var machine in machines)
        {
            var machineRoot = Path.Combine(Root, machine.OutputFolderName);
            Directory.CreateDirectory(Path.Combine(machineRoot, "NG"));
            Directory.CreateDirectory(Path.Combine(machineRoot, "DLNG"));
            Directory.CreateDirectory(Path.Combine(machineRoot, "IRS_LEAK"));
            Directory.CreateDirectory(Path.Combine(machineRoot, "OVERKILL"));
        }
    }

    public string MachineRoot(WeldingMachine machine) =>
        Path.Combine(Root, machine.OutputFolderName);

    public string CandidateTempFolder(WeldingMachine machine, KickoutCandidate candidate)
    {
        var sourceName = Path.GetFileName(
            candidate.SourceFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return Path.Combine(
            Temp,
            machine.OutputFolderName,
            candidate.InspectedAt.ToString("yyyyMMdd"),
            sourceName);
    }

    private static string ResolveRoot(string requested)
    {
        var driveRoot = Path.GetPathRoot(requested);
        if (!string.IsNullOrWhiteSpace(driveRoot) && Directory.Exists(driveRoot))
        {
            return requested;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KickoutMonitor");
    }
}
