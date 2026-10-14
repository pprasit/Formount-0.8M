using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace NARIT_Collimation_Manager;

public sealed class CollimationManagerViewModel
{
    public ObservableCollection<CollimationRunRow> Rows { get; } = new();

    public IReadOnlyList<string> ScrewOptions { get; } = new[]
    {
        "Screw 1",
        "Screw 2",
        "Screw 3"
    };

    public CollimationManagerViewModel()
    {
        SeedRows();
    }

    public string GetAverageScoreDisplay()
    {
        double[] scores = Rows
            .Where(row => row.Score.HasValue)
            .Select(row => row.Score!.Value)
            .ToArray();

        return scores.Length == 0
            ? "-"
            : scores.Average().ToString("0.00", CultureInfo.InvariantCulture);
    }

    private void SeedRows()
    {
        for (int i = 1; i <= 8; i++)
        {
            Rows.Add(new CollimationRunRow(i, ScrewOptions[0], "0.0"));
        }
    }
}
