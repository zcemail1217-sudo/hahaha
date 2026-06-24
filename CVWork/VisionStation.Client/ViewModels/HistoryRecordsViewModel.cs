using System.Collections.ObjectModel;
using Prism.Commands;
using Prism.Mvvm;
using VisionStation.Domain;
using VisionStation.Vision.UI.ViewModels;

namespace VisionStation.Client.ViewModels;

public sealed class HistoryRecordsViewModel : BindableBase
{
    private readonly IInspectionRecordRepository _records;
    private InspectionRecordItem? _selectedRecord;

    public HistoryRecordsViewModel(IInspectionRecordRepository records)
    {
        _records = records;
        ReloadCommand = new DelegateCommand(async () => await LoadAsync());
        _ = LoadAsync();
    }

    public ObservableCollection<InspectionRecordItem> Records { get; } = new();

    public ObservableCollection<ResultFieldItem> SelectedResultFields { get; } = new();

    public DelegateCommand ReloadCommand { get; }

    public InspectionRecordItem? SelectedRecord
    {
        get => _selectedRecord;
        set
        {
            if (SetProperty(ref _selectedRecord, value))
            {
                ApplySelectedRecord(value);
            }
        }
    }

    private async Task LoadAsync()
    {
        var records = await _records.RecentAsync(100);
        Records.Clear();
        foreach (var record in records)
        {
            Records.Add(new InspectionRecordItem(
                record.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                record.RecipeName,
                record.Outcome.ToString(),
                $"{record.CycleTime.TotalMilliseconds:0} ms",
                string.IsNullOrWhiteSpace(record.Barcode) ? "-" : record.Barcode,
                BuildResultSummary(record.ResultData),
                ToResultFieldItems(record.ResultData),
                record.OriginalImagePath,
                record.ResultImagePath));
        }

        SelectedRecord = Records.FirstOrDefault();
    }

    private void ApplySelectedRecord(InspectionRecordItem? record)
    {
        SelectedResultFields.Clear();
        if (record is null)
        {
            return;
        }

        foreach (var item in record.ResultFields)
        {
            SelectedResultFields.Add(item);
        }
    }

    private static string BuildResultSummary(IReadOnlyDictionary<string, string> resultData)
    {
        if (resultData.Count == 0)
        {
            return "-";
        }

        return string.Join(" | ", resultData.Take(4).Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private static IReadOnlyList<ResultFieldItem> ToResultFieldItems(IReadOnlyDictionary<string, string> resultData)
    {
        return resultData
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new ResultFieldItem(pair.Key, pair.Value))
            .ToArray();
    }
}
