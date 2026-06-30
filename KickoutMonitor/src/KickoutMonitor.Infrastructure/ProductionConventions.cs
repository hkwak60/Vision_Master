using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public static class ProductionCsvSchema
{
    public const string CellId = "CELL-ID";
    public const string Judge = "JUDGE";
    public const string JudgeDefect = "JUDGE-DEFECT";
    public const string UpperJudge = "UPPER_JUDGE";
    public const string LowerJudge = "LOWER_JUDGE";
    public const string Date = "DATE";
    public const string Time = "TIME";
    public const string ModelId = "MODEL-ID";
    public const string LotId = "LOT-ID";

    public static string ImagePath(string side, int index) => $"{side}_IMAGE-PATH-{index}";
    public static string OverlayPath(string side, int index) => $"{side}_OVERLAY-IMAGE-PATH-{index}";
}

public static class ProductionImageConventions
{
    public const int ImagesPerSide = 3;
    public const string Upper = "UPPER";
    public const string Lower = "LOWER";

    public static int SideIndex(string cameraLocation) =>
        cameraLocation.Trim().Equals("BTM", StringComparison.OrdinalIgnoreCase)
            || cameraLocation.Trim().Equals("BOTTOM", StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;

    public static string SideName(string cameraLocation) => SideIndex(cameraLocation) == 1 ? Lower : Upper;

    public static bool IsOverlay(string fileName) => fileName.Contains("overlay", StringComparison.OrdinalIgnoreCase);

    public static int RawImageOrder(string path, int sideIndex)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        for (var index = 0; index < ImagesPerSide; index++)
        {
            if (name.Contains($"_{sideIndex}_{index}", StringComparison.OrdinalIgnoreCase)) return index;
        }

        return 99;
    }

    public static IReadOnlyList<string> ProductionPath(VisionMasterSettings settings, string model, DateTime timestamp, params string[] extraSegments) =>
        settings.ProductionPaths.ImageSegments
            .Concat([model, timestamp.ToString("yyyy"), timestamp.ToString("MM"), timestamp.ToString("dd")])
            .Concat(extraSegments)
            .ToArray();
}
