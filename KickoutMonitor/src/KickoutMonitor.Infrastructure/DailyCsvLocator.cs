using KickoutMonitor.Application;
using KickoutMonitor.Domain;
using System.Text.RegularExpressions;

namespace KickoutMonitor.Infrastructure;

public sealed class DailyCsvLocator : IDailyCsvLocator
{
    private readonly ISharePathResolver _shares;

    public DailyCsvLocator(ISharePathResolver shares)
    {
        _shares = shares;
    }

    public Task<IReadOnlyList<string>> FindAsync(
        WeldingMachine machine,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<string>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preferredRoot = Path.Combine(
                _shares.GetRoot(machine, 'D'),
                "Files", "Data", "Result", "Day");
            foreach (var root in new[] { preferredRoot, machine.DataRoot(administrative: true) }
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    var token = date.ToString("yyyyMMdd");
                    var validName = new Regex(
                        $@"{Regex.Escape(token)}(?:_[0-9]+)?\.csv$",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    var files = Directory.EnumerateFiles(root, $"*{token}*.csv")
                        .Where(path => validName.IsMatch(Path.GetFileName(path)))
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (files.Length > 0) return files;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return [];
        }, cancellationToken);
    }
}
