using System.Text.Json;
using System.Text.Json.Serialization;

namespace KickoutMonitor.Domain;

public sealed class VisionMasterSettings
{
    public string StorageRoot { get; set; } = @"E:\KWAK\VisionMaster";
    public ProductionPathSettings ProductionPaths { get; set; } = new();
    public KickoutRuleSettings KickoutRules { get; set; } = new();
    public List<MachineSetting> Machines { get; set; } = [];
    public IrsRuleSettings IrsRules { get; set; } = new();

    public static VisionMasterSettings CreateDefault() => new()
    {
        StorageRoot = @"E:\KWAK\VisionMaster",
        ProductionPaths = new(),
        KickoutRules = new(),
        Machines =
        [
            Machine("1-1-an", "1-1", Polarity.Anode, "10.112.99.181", "E81C"),
            Machine("1-1-ca", "1-1", Polarity.Cathode, "10.112.99.182", "E81C"),
            Machine("1-2-an", "1-2", Polarity.Anode, "10.112.99.66", "E81C"),
            Machine("1-2-ca", "1-2", Polarity.Cathode, "10.112.99.67", "E81C"),
            Machine("2-1-an", "2-1", Polarity.Anode, "10.112.99.71", "E81C"),
            Machine("2-1-ca", "2-1", Polarity.Cathode, "10.112.99.72", "E81C"),
            Machine("2-2-an", "2-2", Polarity.Anode, "10.112.99.77", "E69B"),
            Machine("2-2-ca", "2-2", Polarity.Cathode, "10.112.99.78", "E69B")
        ],
        IrsRules = IrsRuleSettings.CreateDefault()
    };

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(StorageRoot)) errors.Add("StorageRoot is required.");
        if (!TimeSpan.TryParse(KickoutRules.ReportStartTime, out _)) errors.Add("KickoutRules.ReportStartTime must parse as HH:mm.");
        var machineIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var machine in Machines)
        {
            if (string.IsNullOrWhiteSpace(machine.Id)) errors.Add("Machine Id is required.");
            else if (!machineIds.Add(machine.Id)) errors.Add($"Duplicate machine Id: {machine.Id}");
            if (string.IsNullOrWhiteSpace(machine.Line)) errors.Add($"Machine {machine.Id}: Line is required.");
            if (string.IsNullOrWhiteSpace(machine.IpAddress)) errors.Add($"Machine {machine.Id}: IpAddress is required.");
            if (string.IsNullOrWhiteSpace(machine.Model)) errors.Add($"Machine {machine.Id}: Model is required.");
            if (machine.ImageDrives.Count == 0) errors.Add($"Machine {machine.Id}: at least one image drive is required.");
        }

        var firstStageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in IrsRules.FirstStageSelections)
        {
            if (string.IsNullOrWhiteSpace(option.Id)) errors.Add("IRS first-stage selection Id is required.");
            else if (!firstStageIds.Add(option.Id)) errors.Add($"Duplicate IRS first-stage selection Id: {option.Id}");
            if (string.IsNullOrWhiteSpace(option.DisplayName)) errors.Add($"IRS selection {option.Id}: DisplayName is required.");
            if (string.IsNullOrWhiteSpace(option.CategoryFolder)) errors.Add($"IRS selection {option.Id}: CategoryFolder is required.");
        }

        foreach (var group in IrsRules.FinalClassGroups)
        {
            if (string.IsNullOrWhiteSpace(group.Folder)) errors.Add("IRS final class group Folder is required.");
            if (group.Classes.Count == 0) errors.Add($"IRS final class group {group.Folder}: at least one class is required.");
        }

        return errors;
    }

    private static MachineSetting Machine(string id, string line, Polarity polarity, string ip, string model) => new()
    {
        Id = id,
        Line = line,
        Polarity = polarity,
        IpAddress = ip,
        Model = model,
        DataDrive = "D",
        ImageDrives = ["E", "F", "G"],
        Enabled = true
    };
}

public sealed class MachineSetting
{
    public string Id { get; set; } = string.Empty;
    public string Line { get; set; } = string.Empty;
    public Polarity Polarity { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    public string Model { get; set; } = "E81C";
    public string DataDrive { get; set; } = "D";
    public List<string> ImageDrives { get; set; } = ["E", "F", "G"];
    public bool Enabled { get; set; } = true;
}

public sealed class ProductionPathSettings
{
    public List<string> DataResultSegments { get; set; } = ["Files", "Data", "Result", "Day"];
    public List<string> ImageSegments { get; set; } = ["Files", "Image"];
    public string MavinFolderName { get; set; } = "Mavin";
}

public sealed class KickoutRuleSettings
{
    public string NgJudgeText { get; set; } = "NG";
    public List<string> IgnoredCellPrefixes { get; set; } = ["OCR", "AGING"];
    public string ReportStartTime { get; set; } = "06:00";
}

public sealed class IrsRuleSettings
{
    public List<IrsFirstStageSelectionSetting> FirstStageSelections { get; set; } = [];
    public List<IrsFinalClassGroupSetting> FinalClassGroups { get; set; } = [];

    public static IrsRuleSettings CreateDefault() => new()
    {
        FirstStageSelections =
        [
            First("RULEBASE", "Rulebase (R)", "RULEBASE", IrsSelectionKind.Rulebase, null, null),
            First("UNDETECTABLE", "Undetectable (U)", "UNDETECTABLE", IrsSelectionKind.Undetectable, null, null),
            First("A_L", "A L", "Crop_A", IrsSelectionKind.Crop, "Crop_A", "A_L"),
            First("A_R", "A R", "Crop_A", IrsSelectionKind.Crop, "Crop_A", "A_R"),
            First("B_L", "B L", "Crop_B", IrsSelectionKind.Crop, "Crop_B", "B_L"),
            First("B_R", "B R", "Crop_B", IrsSelectionKind.Crop, "Crop_B", "B_R"),
            First("MICRO_LL", "LL", "Crop_micro", IrsSelectionKind.Crop, "Crop_micro", "Micro_LL"),
            First("MICRO_LM", "LM", "Crop_micro", IrsSelectionKind.Crop, "Crop_micro", "Micro_LM"),
            First("MICRO_MM", "MM", "Crop_micro", IrsSelectionKind.Crop, "Crop_micro", "Micro_MM"),
            First("MICRO_MR", "MR", "Crop_micro", IrsSelectionKind.Crop, "Crop_micro", "Micro_MR"),
            First("MICRO_RR", "RR", "Crop_micro", IrsSelectionKind.Crop, "Crop_micro", "Micro_RR"),
            First("TABSIDE_L", "Tabside L", "Crop_micro_tabside", IrsSelectionKind.Crop, "Crop_micro_tabside", "_L"),
            First("TABSIDE_R", "Tabside R", "Crop_micro_tabside", IrsSelectionKind.Crop, "Crop_micro_tabside", "_R"),
            First("GAP", "GAP", "Gap_DL", IrsSelectionKind.Crop, "GAP_DL", null),
            First("SEPA", "SEPA", "SEPA", IrsSelectionKind.Crop, "SEPA", null),
            First("SEPA_SHOULDER_L", "Sepa Shoulder L", "SEPA_SHOULDER", IrsSelectionKind.Crop, "SEPA_SHOULDER", "SHOULDER_L"),
            First("SEPA_SHOULDER_R", "Sepa Shoulder R", "SEPA_SHOULDER", IrsSelectionKind.Crop, "SEPA_SHOULDER", "SHOULDER_R")
        ],
        FinalClassGroups =
        [
            Final("Crop_A", Polarity.Anode, ["01_OK_TOP_ANODE", "02_OK_BACK_ANODE", "03_NG_TORN", "04_NG_PTCL", "05_NG_FOLDED", "No Need to Retrain"]),
            Final("Crop_A", Polarity.Cathode, ["01_OK_TOP_CATHODE", "02_OK_BACK_CATHODE", "03_NG_TORN", "04_NG_PTCL", "05_NG_FOLDED", "No Need to Retrain"]),
            Final("Crop_B", Polarity.Anode, ["01_OK_ANODE", "02_NG_TORN", "03_NG_PTCL", "04_NG_FOLDED", "No Need to Retrain"]),
            Final("Crop_B", Polarity.Cathode, ["01_OK_CATHODE", "02_NG_TORN", "03_NG_PTCL", "04_NG_FOLDED", "No Need to Retrain"]),
            Final("Crop_micro", null, ["01_OK_TAB", "02_OK_BTM", "03_OK_QNG_DENT", "04_NG_TORN_DENT", "05_NG_TORN_CRACK", "06_NG_TORN_VERTICAL_CRACK", "No Need to Retrain"]),
            Final("Crop_micro_tabside", null, ["01_OK_TAB_SIDE", "02_OK_NG_MARK", "03_QNG_WRINKLE", "04_NG_SIDE_TORN", "05_NG_SIDE_PTCL", "No Need to Retrain"]),
            Final("Gap_DL", null, ["Real", "No Need to Retrain"]),
            Final("SEPA", null, ["Real", "No Need to Retrain"]),
            Final("SEPA_SHOULDER", null, ["Real", "No Need to Retrain"])
        ]
    };

    private static IrsFirstStageSelectionSetting First(
        string id,
        string displayName,
        string categoryFolder,
        IrsSelectionKind kind,
        string? mavinFolder,
        string? token) => new()
    {
        Id = id,
        DisplayName = displayName,
        CategoryFolder = categoryFolder,
        Kind = kind,
        MavinFolder = mavinFolder,
        Token = token
    };

    private static IrsFinalClassGroupSetting Final(string folder, Polarity? polarity, List<string> classes) => new()
    {
        Folder = folder,
        Polarity = polarity,
        Classes = classes
    };
}

public sealed class IrsFirstStageSelectionSetting
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string CategoryFolder { get; set; } = string.Empty;
    public IrsSelectionKind Kind { get; set; } = IrsSelectionKind.Crop;
    public string? MavinFolder { get; set; }
    public string? Token { get; set; }
}

public sealed class IrsFinalClassGroupSetting
{
    public string Folder { get; set; } = string.Empty;
    public Polarity? Polarity { get; set; }
    public List<string> Classes { get; set; } = [];
}

