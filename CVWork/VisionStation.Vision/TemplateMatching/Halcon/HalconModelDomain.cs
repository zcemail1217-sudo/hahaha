using System.Collections.ObjectModel;

namespace VisionStation.Vision;

/// <summary>
/// Immutable crop-local model domain shared by feature extraction, HALCON ReduceDomain and
/// model-origin metadata. Rows and columns use inclusive run endpoints when converted to HALCON.
/// </summary>
internal sealed class HalconModelDomain
{
    private readonly HalconSupportRun[] _runs;

    public HalconModelDomain(
        int width,
        int height,
        IReadOnlyList<HalconSupportRun> runs)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Count == 0)
        {
            throw new ArgumentException("The HALCON model domain cannot be empty.", nameof(runs));
        }

        _runs = runs.ToArray();
        long area = 0;
        double weightedRow = 0;
        double weightedColumn = 0;
        var previousRow = -1;
        var previousEnd = -1;
        foreach (HalconSupportRun run in _runs)
        {
            ArgumentNullException.ThrowIfNull(run);
            if (run.Row < 0 || run.Row >= height || run.ColumnStart < 0 || run.Length <= 0)
            {
                throw new ArgumentException("Model-domain runs must lie inside the crop.", nameof(runs));
            }

            int end = checked(run.ColumnStart + (run.Length - 1));
            if (end >= width ||
                run.Row < previousRow ||
                (run.Row == previousRow && run.ColumnStart <= previousEnd))
            {
                throw new ArgumentException(
                    "Model-domain runs must be sorted, non-overlapping and inside the crop.",
                    nameof(runs));
            }

            area = checked(area + run.Length);
            weightedRow += (double)run.Row * run.Length;
            weightedColumn += ((double)run.ColumnStart + end) * run.Length / 2.0;
            previousRow = run.Row;
            previousEnd = end;
        }

        if (area > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(runs), "The model-domain area is too large.");
        }

        Width = width;
        Height = height;
        Runs = new ReadOnlyCollection<HalconSupportRun>(_runs);
        Area = (int)area;
        CentroidRow = weightedRow / area;
        CentroidColumn = weightedColumn / area;
    }

    public int Width { get; }

    public int Height { get; }

    public IReadOnlyList<HalconSupportRun> Runs { get; }

    public int Area { get; }

    public double CentroidRow { get; }

    public double CentroidColumn { get; }
}
