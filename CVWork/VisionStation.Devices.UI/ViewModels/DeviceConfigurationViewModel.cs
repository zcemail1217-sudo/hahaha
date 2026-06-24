using System.Collections.ObjectModel;
using System.Globalization;
using Prism.Commands;
using Prism.Mvvm;
using VisionStation.Application.Presentation;
using VisionStation.Devices;
using VisionStation.Domain;

namespace VisionStation.Devices.UI.ViewModels;

public sealed class DeviceConfigurationViewModel : BindableBase
{
    private const string UnsavedChangesKey = "device-configuration";

    private readonly IDeviceConfigurationRepository _configurationRepository;
    private readonly IDigitalIoController _io;
    private readonly IUnsavedChangesService _unsavedChanges;
    private DeviceConfiguration _configuration;
    private string _axisControllerMode = AxisControllerKind.Simulated.ToString();
    private string _googolCardNo = "0";
    private string _googolConfigPath = string.Empty;
    private string _statusText = "设备点位配置已载入";
    private bool _hasUnsavedChanges;
    private bool _isLoadingConfiguration;
    private bool _refreshingSelectionOptions;

    public DeviceConfigurationViewModel(
        DeviceConfiguration configuration,
        IDeviceConfigurationRepository configurationRepository,
        IDigitalIoController io,
        IUnsavedChangesService unsavedChanges)
    {
        _configuration = configuration;
        _configurationRepository = configurationRepository;
        _io = io;
        _unsavedChanges = unsavedChanges;

        AddCardCommand = new DelegateCommand(AddCard);
        AddExtendedModuleCommand = new DelegateCommand(AddExtendedModule);
        AddAxisCommand = new DelegateCommand(AddAxis);
        AddInputPointCommand = new DelegateCommand(() => AddIoPoint(IoPointDirection.Input, IoPointSource.Onboard));
        AddOutputPointCommand = new DelegateCommand(() => AddIoPoint(IoPointDirection.Output, IoPointSource.Onboard));
        AddAxisInputPointCommand = new DelegateCommand(() => AddIoPoint(IoPointDirection.Input, IoPointSource.AxisOnboard));
        AddAxisOutputPointCommand = new DelegateCommand(() => AddIoPoint(IoPointDirection.Output, IoPointSource.AxisOnboard));
        AddExtendedInputPointCommand = new DelegateCommand(() => AddIoPoint(IoPointDirection.Input, IoPointSource.ExtendedModule));
        AddExtendedOutputPointCommand = new DelegateCommand(() => AddIoPoint(IoPointDirection.Output, IoPointSource.ExtendedModule));
        RemoveCardCommand = new DelegateCommand<object>(RemoveCard);
        RemoveExtendedModuleCommand = new DelegateCommand<object>(RemoveExtendedModule);
        RemoveAxisCommand = new DelegateCommand<object>(RemoveAxis);
        RemoveIoPointCommand = new DelegateCommand<object>(RemoveIoPoint);
        SaveConfigurationCommand = new DelegateCommand(async () => await SaveConfigurationAsync());
        ReloadConfigurationCommand = new DelegateCommand(async () => await ReloadConfigurationAsync());

        LoadConfiguration(configuration);
    }

    public ObservableCollection<AxisPointItem> Axes { get; } = new();

    public ObservableCollection<GoogolCardItem> Cards { get; } = new();

    public ObservableCollection<NumericSelectionOption> CardNoOptions { get; } = new();

    public ObservableCollection<AxisCardSelectionOption> AxisCardOptions { get; } = new();

    public ObservableCollection<TextSelectionOption> AxisCardDriverOptions { get; } = new(
        GoogolCardItem.CreateDriverOptions());

    public ObservableCollection<ExtendedIoModuleItem> ExtendedModules { get; } = new();

    public ObservableCollection<IoPointItem> IoPoints { get; } = new();

    public ObservableCollection<IoPointItem> OnboardInputPoints { get; } = new();

    public ObservableCollection<IoPointItem> OnboardOutputPoints { get; } = new();

    public ObservableCollection<IoPointItem> AxisInputPoints { get; } = new();

    public ObservableCollection<IoPointItem> AxisOutputPoints { get; } = new();

    public ObservableCollection<IoPointItem> ExtendedInputPoints { get; } = new();

    public ObservableCollection<IoPointItem> ExtendedOutputPoints { get; } = new();

    public ObservableCollection<TextSelectionOption> AxisControllerModes { get; } = new(
    [
        new TextSelectionOption(AxisControllerKind.Simulated.ToString(), "仿真"),
        new TextSelectionOption(AxisControllerKind.Googol.ToString(), "真实硬件")
    ]);

    public DelegateCommand AddAxisCommand { get; }

    public DelegateCommand AddCardCommand { get; }

    public DelegateCommand AddExtendedModuleCommand { get; }

    public DelegateCommand AddInputPointCommand { get; }

    public DelegateCommand AddOutputPointCommand { get; }

    public DelegateCommand AddAxisInputPointCommand { get; }

    public DelegateCommand AddAxisOutputPointCommand { get; }

    public DelegateCommand AddExtendedInputPointCommand { get; }

    public DelegateCommand AddExtendedOutputPointCommand { get; }

    public DelegateCommand<object> RemoveCardCommand { get; }

    public DelegateCommand<object> RemoveExtendedModuleCommand { get; }

    public DelegateCommand<object> RemoveAxisCommand { get; }

    public DelegateCommand<object> RemoveIoPointCommand { get; }

    public DelegateCommand SaveConfigurationCommand { get; }

    public DelegateCommand ReloadConfigurationCommand { get; }

    public string AxisControllerMode
    {
        get => _axisControllerMode;
        set
        {
            if (SetProperty(ref _axisControllerMode, ResolveAxisControllerKind(value).ToString()))
            {
                MarkDirty("轴卡设置已修改，保存后生效。");
            }
        }
    }

    public string GoogolCardNo
    {
        get => _googolCardNo;
        set
        {
            if (SetProperty(ref _googolCardNo, value))
            {
                RefreshSelectionOptions();
                MarkDirty("轴卡设置已修改，保存后生效。");
            }
        }
    }

    public string GoogolConfigPath
    {
        get => _googolConfigPath;
        set
        {
            if (SetProperty(ref _googolConfigPath, value))
            {
                MarkDirty("轴卡设置已修改，保存后生效。");
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set
        {
            if (SetProperty(ref _hasUnsavedChanges, value))
            {
                _unsavedChanges.SetUnsaved(
                    UnsavedChangesKey,
                    "轴卡设置",
                    value,
                    _ => SaveConfigurationAsync(),
                    "轴卡、轴、扩展 IO 或点位配置");
            }
        }
    }

    private void MarkDirty(string statusText)
    {
        if (_isLoadingConfiguration || _refreshingSelectionOptions)
        {
            return;
        }

        HasUnsavedChanges = true;
        StatusText = statusText;
    }

    private void ConfigureCardItem(GoogolCardItem card)
    {
        card.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(GoogolCardItem.Key)
                or nameof(GoogolCardItem.Name)
                or nameof(GoogolCardItem.Driver)
                or nameof(GoogolCardItem.CardNo)
                or nameof(GoogolCardItem.AxisCount)
                or nameof(GoogolCardItem.InputCount)
                or nameof(GoogolCardItem.OutputCount))
            {
                if (args.PropertyName == nameof(GoogolCardItem.Driver)
                    && card.DriverKind != AxisCardDriverKind.Simulated)
                {
                    AxisControllerMode = AxisControllerKind.Googol.ToString();
                }

                RefreshSelectionOptions();
                MarkDirty("轴卡设置已修改，保存后生效。");
            }
        };
    }

    private void ConfigureExtendedModuleItem(ExtendedIoModuleItem module)
    {
        module.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ExtendedIoModuleItem.ParentCardKey)
                or nameof(ExtendedIoModuleItem.ParentCardNo)
                or nameof(ExtendedIoModuleItem.ModuleNo)
                or nameof(ExtendedIoModuleItem.Model)
                or nameof(ExtendedIoModuleItem.StartAddress)
                or nameof(ExtendedIoModuleItem.InputCount)
                or nameof(ExtendedIoModuleItem.OutputCount)
                or nameof(ExtendedIoModuleItem.ConfigPath))
            {
                RefreshSelectionOptions();
                MarkDirty("扩展 IO 模块配置已修改，保存后生效。");
            }
        };
    }

    private void ConfigureAxisItem(AxisPointItem axis)
    {
        axis.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(AxisPointItem.CardKey)
                or nameof(AxisPointItem.CardNo))
            {
                RefreshSelectionOptions();
                MarkDirty("轴配置已修改，保存后生效。");
            }
        };
    }

    private void ConfigureIoPointItem(IoPointItem point)
    {
        point.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(IoPointItem.CardKey)
                or nameof(IoPointItem.CardNo)
                or nameof(IoPointItem.ParentCardKey)
                or nameof(IoPointItem.ParentCardNo)
                or nameof(IoPointItem.AxisNo)
                or nameof(IoPointItem.ModuleNo)
                or nameof(IoPointItem.PointNo)
                or nameof(IoPointItem.Direction)
                or nameof(IoPointItem.Source))
            {
                RefreshSelectionOptions();
                MarkDirty("IO 点位配置已修改，保存后生效。");
            }
        };
    }

    private void RefreshSelectionOptions()
    {
        if (_refreshingSelectionOptions)
        {
            return;
        }

        _refreshingSelectionOptions = true;
        try
        {
            var cards = Cards.OrderBy(card => card.CardNo).ToArray();
            NumericSelectionOption.Replace(
                CardNoOptions,
                cards.Length == 0
                    ? new[] { new NumericSelectionOption(ParseShort(GoogolCardNo, 0), $"C{ParseShort(GoogolCardNo, 0)}") }
                    : cards.Select(card => new NumericSelectionOption(card.CardNo, $"C{card.CardNo}")));
            AxisCardSelectionOption.Replace(
                AxisCardOptions,
                cards.Length == 0
                    ? new[] { new AxisCardSelectionOption("card1", ParseShort(GoogolCardNo, 0), $"card1 / C{ParseShort(GoogolCardNo, 0)}") }
                    : cards.Select(card => new AxisCardSelectionOption(ResolveCardKey(card), card.CardNo, FormatAxisCardOption(card))));

            foreach (var module in ExtendedModules)
            {
                RefreshExtendedModuleOptions(module);
            }

            foreach (var axis in Axes)
            {
                RefreshAxisOptions(axis);
            }

            foreach (var point in IoPoints)
            {
                RefreshIoPointOptions(point);
            }
        }
        finally
        {
            _refreshingSelectionOptions = false;
        }
    }

    private void RefreshExtendedModuleOptions(ExtendedIoModuleItem module)
    {
        var card = ResolveModuleParentCard(module);
        if (card is not null)
        {
            var cardKey = ResolveCardKey(card);
            if (!string.Equals(module.ParentCardKey, cardKey, StringComparison.OrdinalIgnoreCase))
            {
                module.ParentCardKey = cardKey;
            }

            if (module.ParentCardNo != card.CardNo)
            {
                module.ParentCardNo = card.CardNo;
            }
        }
        else if (!ContainsOption(CardNoOptions, module.ParentCardNo))
        {
            module.ParentCardNo = FirstOptionValue(CardNoOptions, ParseShort(GoogolCardNo, 0));
        }

        module.SetModuleNoOptions(RangeWithCurrent(0, 15, module.ModuleNo, value => $"M{value}"));
        module.SetStartAddressOptions(RangeWithCurrent(0, 15, module.StartAddress, value => value.ToString(CultureInfo.InvariantCulture)));
    }

    private void RefreshAxisOptions(AxisPointItem axis)
    {
        var card = ResolveAxisCard(axis);
        if (card is not null)
        {
            var cardKey = ResolveCardKey(card);
            if (!string.Equals(axis.CardKey, cardKey, StringComparison.OrdinalIgnoreCase))
            {
                axis.CardKey = cardKey;
            }

            if (axis.CardNo != card.CardNo)
            {
                axis.CardNo = card.CardNo;
            }
        }
        else if (!ContainsOption(CardNoOptions, axis.CardNo))
        {
            axis.CardNo = FirstOptionValue(CardNoOptions, ParseShort(GoogolCardNo, 0));
        }

        if (axis.AxisNo <= 0)
        {
            axis.AxisNo = 1;
        }

        var axisCount = Math.Max(card?.AxisCount ?? 8, 1);
        axis.SetAxisNoOptions(RangeWithCurrent(1, ToPositiveShort(axisCount), axis.AxisNo, value => $"CH-{value:00}"));
    }

    private void RefreshIoPointOptions(IoPointItem point)
    {
        if (point.PointNo <= 0)
        {
            point.PointNo = 1;
        }

        if (point.Source == IoPointSource.ExtendedModule)
        {
            var parentCard = ResolvePointParentCard(point);
            if (parentCard is not null)
            {
                var parentCardKey = ResolveCardKey(parentCard);
                if (!string.Equals(point.ParentCardKey, parentCardKey, StringComparison.OrdinalIgnoreCase))
                {
                    point.ParentCardKey = parentCardKey;
                }

                if (!string.Equals(point.CardKey, parentCardKey, StringComparison.OrdinalIgnoreCase))
                {
                    point.CardKey = parentCardKey;
                }

                if (point.ParentCardNo != parentCard.CardNo)
                {
                    point.ParentCardNo = parentCard.CardNo;
                }
            }
            else if (!ContainsOption(CardNoOptions, point.ParentCardNo))
            {
                point.ParentCardNo = FirstOptionValue(CardNoOptions, ParseShort(GoogolCardNo, 0));
            }

            point.CardNo = point.ParentCardNo;
            point.AxisNo = -1;
            point.SetAxisNoOptions(Array.Empty<NumericSelectionOption>());
            var modules = ExtendedModules
                .Where(module => IsModuleOnCard(module, point.ParentCardKey, point.ParentCardNo))
                .OrderBy(module => module.ModuleNo)
                .ToArray();

            var moduleOptions = modules
                .Select(module => new NumericSelectionOption(module.ModuleNo, $"M{module.ModuleNo}"))
                .ToList();
            if (moduleOptions.Count == 0)
            {
                var moduleNo = point.ModuleNo >= 0 ? point.ModuleNo : (short)0;
                moduleOptions.Add(new NumericSelectionOption(moduleNo, $"M{moduleNo}"));
            }
            else if (!moduleOptions.Any(option => option.Value == point.ModuleNo) && point.ModuleNo >= 0)
            {
                moduleOptions.Add(new NumericSelectionOption(point.ModuleNo, $"M{point.ModuleNo}"));
            }

            point.SetModuleNoOptions(moduleOptions.OrderBy(option => option.Value));
            if (point.ModuleNo < 0)
            {
                point.ModuleNo = FirstOptionValue(point.ModuleNoOptions, 0);
            }

            var selectedModule = modules.FirstOrDefault(module => module.ModuleNo == point.ModuleNo);
            if (!string.IsNullOrWhiteSpace(selectedModule?.ConfigPath))
            {
                point.ModuleConfigPath = selectedModule.ConfigPath;
            }

            var pointCount = point.Direction == IoPointDirection.Input
                ? selectedModule?.InputCount ?? 16
                : selectedModule?.OutputCount ?? 16;
            point.SetPointNoOptions(RangeWithCurrent(1, ToPositiveShort(pointCount), point.PointNo, value => $"{point.DirectionCode}{value}"));
        }
        else
        {
            var card = ResolvePointCard(point);
            if (card is not null)
            {
                var cardKey = ResolveCardKey(card);
                if (!string.Equals(point.CardKey, cardKey, StringComparison.OrdinalIgnoreCase))
                {
                    point.CardKey = cardKey;
                }

                if (point.CardNo != card.CardNo)
                {
                    point.CardNo = card.CardNo;
                }
            }
            else if (!ContainsOption(CardNoOptions, point.CardNo))
            {
                point.CardNo = FirstOptionValue(CardNoOptions, ParseShort(GoogolCardNo, 0));
            }

            point.ParentCardKey = string.Empty;
            point.ParentCardNo = -1;
            point.ModuleNo = -1;
            point.ModuleConfigPath = string.Empty;
            point.SetModuleNoOptions(Array.Empty<NumericSelectionOption>());

            if (point.Source == IoPointSource.AxisOnboard)
            {
                if (point.AxisNo <= 0)
                {
                    point.AxisNo = 1;
                }

                var axisCount = Math.Max(card?.AxisCount ?? 4, 1);
                point.SetAxisNoOptions(RangeWithCurrent(1, ToPositiveShort(axisCount), point.AxisNo, value => $"CH-{value:00}"));
                if (point.Direction == IoPointDirection.Output && point.PointNo < 4)
                {
                    point.PointNo = 4;
                }

                point.SetPointNoOptions(point.Direction == IoPointDirection.Input
                    ? RangeWithCurrent(1, 4, point.PointNo, value => $"AXDI{value}")
                    : RangeWithCurrent(4, 7, point.PointNo, value => $"AXDO{value}"));
            }
            else
            {
                point.AxisNo = -1;
                point.SetAxisNoOptions(Array.Empty<NumericSelectionOption>());
                var pointCount = point.Direction == IoPointDirection.Input
                    ? card?.InputCount ?? 16
                    : card?.OutputCount ?? 16;
                point.SetPointNoOptions(RangeWithCurrent(1, ToPositiveShort(pointCount), point.PointNo, value => $"{point.DirectionCode}{value}"));
            }
        }

        point.RefreshAddress();
    }

    private short ResolveNextAxisNo(string cardKey, short cardNo)
    {
        var card = ResolveCardByKey(cardKey) ?? ResolveCard(cardNo);
        var axisCount = Math.Max(card?.AxisCount ?? 8, 1);
        var used = Axes
            .Where(axis => IsAxisOnCard(axis, cardKey, cardNo))
            .Select(axis => axis.AxisNo)
            .ToHashSet();

        for (short axisNo = 1; axisNo <= axisCount && axisNo < short.MaxValue; axisNo++)
        {
            if (!used.Contains(axisNo))
            {
                return axisNo;
            }
        }

        return ToPositiveShort(used.Select(value => (int)value).DefaultIfEmpty(0).Max() + 1);
    }

    private short ResolveFirstAxisNo(string cardKey, short cardNo)
    {
        return Axes
            .Where(axis => axis.Enabled)
            .Where(axis => IsAxisOnCard(axis, cardKey, cardNo))
            .OrderBy(axis => axis.AxisNo)
            .Select(axis => axis.AxisNo)
            .FirstOrDefault((short)1);
    }

    private short ResolveNextIoPointNo(
        IoPointDirection direction,
        IoPointSource source,
        string cardKey,
        short cardNo,
        string parentCardKey,
        short parentCardNo,
        short moduleNo,
        short axisNo)
    {
        var card = ResolveCardByKey(cardKey) ?? ResolveCard(cardNo);
        var module = ExtendedModules.FirstOrDefault(item => IsModuleOnCard(item, parentCardKey, parentCardNo) && item.ModuleNo == moduleNo);
        var firstPointNo = source == IoPointSource.AxisOnboard && direction == IoPointDirection.Output
            ? (short)4
            : (short)1;
        var lastPointNo = source switch
        {
            IoPointSource.ExtendedModule => direction == IoPointDirection.Input
                ? ToPositiveShort(module?.InputCount ?? 16)
                : ToPositiveShort(module?.OutputCount ?? 16),
            IoPointSource.AxisOnboard => direction == IoPointDirection.Input ? (short)4 : (short)7,
            _ => direction == IoPointDirection.Input
                ? ToPositiveShort(card?.InputCount ?? 16)
                : ToPositiveShort(card?.OutputCount ?? 16)
        };
        var used = IoPoints
            .Where(point => point.Direction == direction)
            .Where(point => point.Source == source)
            .Where(point => source == IoPointSource.ExtendedModule
                ? IsPointOnCard(point, parentCardKey, parentCardNo) && point.ModuleNo == moduleNo
                : source == IoPointSource.AxisOnboard
                    ? IsPointOnCard(point, cardKey, cardNo) && point.AxisNo == axisNo
                    : IsPointOnCard(point, cardKey, cardNo))
            .Select(point => point.PointNo)
            .ToHashSet();

        for (var pointNo = firstPointNo; pointNo <= lastPointNo && pointNo < short.MaxValue; pointNo++)
        {
            if (!used.Contains(pointNo))
            {
                return pointNo;
            }
        }

        return ToPositiveShort(used.Select(value => (int)value).DefaultIfEmpty(0).Max() + 1);
    }

    private GoogolCardItem? ResolveCard(short cardNo)
    {
        return Cards.FirstOrDefault(card => card.CardNo == cardNo) ?? Cards.FirstOrDefault();
    }

    private GoogolCardItem? ResolveCardByKey(string? cardKey)
    {
        if (string.IsNullOrWhiteSpace(cardKey))
        {
            return null;
        }

        return Cards.FirstOrDefault(
            card => string.Equals(ResolveCardKey(card), cardKey.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private GoogolCardItem? ResolveAxisCard(AxisPointItem axis)
    {
        return ResolveCardByKey(axis.CardKey) ?? ResolveCard(axis.CardNo);
    }

    private GoogolCardItem? ResolveModuleParentCard(ExtendedIoModuleItem module)
    {
        return ResolveCardByKey(module.ParentCardKey) ?? ResolveCard(module.ParentCardNo);
    }

    private GoogolCardItem? ResolvePointCard(IoPointItem point)
    {
        return ResolveCardByKey(point.CardKey) ?? ResolveCard(point.CardNo);
    }

    private GoogolCardItem? ResolvePointParentCard(IoPointItem point)
    {
        return ResolveCardByKey(point.ParentCardKey)
            ?? ResolveCardByKey(point.CardKey)
            ?? ResolveCard(point.ParentCardNo >= 0 ? point.ParentCardNo : point.CardNo);
    }

    private static bool IsAxisOnCard(AxisPointItem axis, string cardKey, short cardNo)
    {
        if (!string.IsNullOrWhiteSpace(axis.CardKey) && !string.IsNullOrWhiteSpace(cardKey))
        {
            return string.Equals(axis.CardKey, cardKey, StringComparison.OrdinalIgnoreCase);
        }

        return axis.CardNo == cardNo;
    }

    private static bool IsModuleOnCard(ExtendedIoModuleItem module, string cardKey, short cardNo)
    {
        if (!string.IsNullOrWhiteSpace(module.ParentCardKey) && !string.IsNullOrWhiteSpace(cardKey))
        {
            return string.Equals(module.ParentCardKey, cardKey, StringComparison.OrdinalIgnoreCase);
        }

        return module.ParentCardNo == cardNo;
    }

    private static bool IsPointOnCard(IoPointItem point, string cardKey, short cardNo)
    {
        var pointCardKey = point.Source == IoPointSource.ExtendedModule ? point.ParentCardKey : point.CardKey;
        var pointCardNo = point.Source == IoPointSource.ExtendedModule ? point.ParentCardNo : point.CardNo;
        if (!string.IsNullOrWhiteSpace(pointCardKey) && !string.IsNullOrWhiteSpace(cardKey))
        {
            return string.Equals(pointCardKey, cardKey, StringComparison.OrdinalIgnoreCase);
        }

        return pointCardNo == cardNo;
    }

    private static string ResolveCardKey(GoogolCardItem card)
    {
        return string.IsNullOrWhiteSpace(card.Key) ? $"card{card.CardNo}" : card.Key.Trim();
    }

    private static string FormatAxisCardOption(GoogolCardItem card)
    {
        return $"{ResolveCardKey(card)} / C{card.CardNo} / {card.DriverDisplayText}";
    }

    private static IEnumerable<NumericSelectionOption> RangeWithCurrent(short first, short last, short current, Func<short, string> format)
    {
        var options = NumericSelectionOption.Range(first, last, format).ToList();
        if (!options.Any(option => option.Value == current) && current >= 0)
        {
            options.Add(new NumericSelectionOption(current, format(current)));
        }

        return options.OrderBy(option => option.Value);
    }

    private static bool ContainsOption(IEnumerable<NumericSelectionOption> options, short value)
    {
        return options.Any(option => option.Value == value);
    }

    private static short FirstOptionValue(IEnumerable<NumericSelectionOption> options, short fallback)
    {
        return options.FirstOrDefault()?.Value ?? fallback;
    }

    private static short ToPositiveShort(int value)
    {
        return (short)Math.Clamp(Math.Max(value, 1), 1, short.MaxValue);
    }

    private static int RemoveMatching<T>(ICollection<T> collection, Func<T, bool> predicate)
    {
        var items = collection.Where(predicate).ToArray();
        foreach (var item in items)
        {
            collection.Remove(item);
        }

        return items.Length;
    }

    private void LoadConfiguration(DeviceConfiguration configuration)
    {
        _isLoadingConfiguration = true;
        AxisControllerMode = configuration.AxisController.ToString();
        GoogolCardNo = configuration.GoogolCardNo.ToString(CultureInfo.InvariantCulture);
        GoogolConfigPath = configuration.GoogolConfigPath;

        Cards.Clear();
        var cardDefinitions = configuration.AxisCards.Count == 0
            ? configuration.GoogolCards.Select(GoogolCardItem.FromDefinition)
            : configuration.AxisCards.Select(GoogolCardItem.FromDefinition);
        foreach (var item in cardDefinitions)
        {
            ConfigureCardItem(item);
            Cards.Add(item);
        }

        if (Cards.Count == 0)
        {
            var item = CreateDefaultCardItem(configuration);
            ConfigureCardItem(item);
            Cards.Add(item);
        }

        ExtendedModules.Clear();
        foreach (var module in configuration.ExtendedIoModules)
        {
            var item = ExtendedIoModuleItem.FromDefinition(module);
            ConfigureExtendedModuleItem(item);
            ExtendedModules.Add(item);
        }

        Axes.Clear();
        foreach (var axis in configuration.Axes)
        {
            var item = AxisPointItem.FromDefinition(axis);
            ConfigureAxisItem(item);
            Axes.Add(item);
        }

        IoPoints.Clear();
        foreach (var point in configuration.IoPoints)
        {
            var item = IoPointItem.FromDefinition(point);
            ConfigureIoPointItem(item);
            IoPoints.Add(item);
        }

        RefreshSelectionOptions();
        RebuildConfigurationIoGroups();
        _isLoadingConfiguration = false;
        HasUnsavedChanges = false;
    }

    private void RebuildConfigurationIoGroups()
    {
        OnboardInputPoints.Clear();
        OnboardOutputPoints.Clear();
        AxisInputPoints.Clear();
        AxisOutputPoints.Clear();
        ExtendedInputPoints.Clear();
        ExtendedOutputPoints.Clear();

        foreach (var item in IoPoints)
        {
            AddToConfigurationIoGroup(item);
        }
    }

    private void AddToConfigurationIoGroup(IoPointItem item)
    {
        if (item.Source == IoPointSource.ExtendedModule)
        {
            if (item.Direction == IoPointDirection.Output)
            {
                ExtendedOutputPoints.Add(item);
                return;
            }

            ExtendedInputPoints.Add(item);
            return;
        }

        if (item.Source == IoPointSource.AxisOnboard)
        {
            if (item.Direction == IoPointDirection.Output)
            {
                AxisOutputPoints.Add(item);
                return;
            }

            AxisInputPoints.Add(item);
            return;
        }

        if (item.Direction == IoPointDirection.Output)
        {
            OnboardOutputPoints.Add(item);
            return;
        }

        OnboardInputPoints.Add(item);
    }

    private void AddCard()
    {
        var nextCardNo = Cards
            .Select(card => (int)card.CardNo)
            .DefaultIfEmpty(ParseShort(GoogolCardNo, 0) - 1)
            .Max() + 1;
        var card = new GoogolCardItem
        {
            Key = CreateUniqueKey("card", Cards.Count + 1, Cards.Select(item => item.Key)),
            Name = $"固高脉冲轴卡 C{nextCardNo}",
            Driver = AxisCardDriverKind.GoogolPulse.ToString(),
            Vendor = "Googol",
            CardNo = (short)nextCardNo,
            AxisCount = 8,
            InputCount = 16,
            OutputCount = 16,
            ConfigPath = GoogolConfigPath?.Trim() ?? string.Empty,
            Description = $"固高卡 {nextCardNo}"
        };
        ConfigureCardItem(card);
        Cards.Add(card);
        RefreshSelectionOptions();
        MarkDirty("已新增轴卡配置，保存后生效。");
        StatusText = "已新增轴卡配置，保存后生效。";
    }

    private void AddExtendedModule()
    {
        var parentCard = Cards.FirstOrDefault();
        var parentCardKey = parentCard is null ? "card1" : ResolveCardKey(parentCard);
        var parentCardNo = parentCard?.CardNo ?? FirstOptionValue(CardNoOptions, ParseShort(GoogolCardNo, 0));
        var nextModuleNo = ExtendedModules
            .Where(module => IsModuleOnCard(module, parentCardKey, parentCardNo))
            .Select(module => (int)module.ModuleNo)
            .DefaultIfEmpty(-1)
            .Max() + 1;
        var nextStartAddress = ExtendedModules
            .Where(module => IsModuleOnCard(module, parentCardKey, parentCardNo))
            .Select(module => (int)module.StartAddress + Hcb2ModuleCatalog.Resolve(module.Model).AddressSpan)
            .DefaultIfEmpty(0)
            .Max();
        var profile = Hcb2ModuleCatalog.Resolve(Hcb2ModuleCatalog.DefaultModel);
        var module = new ExtendedIoModuleItem
        {
            Key = CreateUniqueKey("ext", ExtendedModules.Count + 1, ExtendedModules.Select(item => item.Key)),
            ParentCardKey = parentCardKey,
            ParentCardNo = parentCardNo,
            ModuleNo = (short)nextModuleNo,
            Model = profile.Model,
            ModuleType = profile.ModuleType,
            StartAddress = (short)Math.Clamp(nextStartAddress, 0, 15),
            InputCount = profile.InputCount,
            OutputCount = profile.OutputCount,
            ConfigPath = string.Empty,
            Description = $"扩展IO模块 {nextModuleNo}"
        };
        ConfigureExtendedModuleItem(module);
        ExtendedModules.Add(module);
        RefreshSelectionOptions();
        MarkDirty("已新增扩展 IO 模块，保存后生效。");
        StatusText = "已新增扩展IO模块，保存后生效。";
    }

    private void AddAxis()
    {
        var card = Cards.FirstOrDefault();
        var cardKey = card is null ? "card1" : ResolveCardKey(card);
        var cardNo = card?.CardNo ?? FirstOptionValue(CardNoOptions, ParseShort(GoogolCardNo, 0));
        var nextNo = ResolveNextAxisNo(cardKey, cardNo);
        var axis = new AxisPointItem
        {
            Key = CreateUniqueKey("Axis", nextNo, Axes.Select(item => item.Key)),
            Name = $"新增轴{nextNo}",
            CardKey = cardKey,
            CardNo = cardNo,
            AxisNo = nextNo,
            Enabled = true,
            PulsesPerUnit = 1000,
            PositionBand = 0.01,
            SoftLimitNegative = -500,
            SoftLimitPositive = 500,
            DefaultSpeed = 80,
            DefaultAcceleration = 120,
            HomeMode = "LimitHomeIndex"
        };
        ConfigureAxisItem(axis);
        Axes.Add(axis);
        RefreshSelectionOptions();
        MarkDirty("已新增轴，保存后生效。");
        StatusText = "已新增轴，保存后生效。";
    }

    private void AddIoPoint(IoPointDirection direction, IoPointSource source)
    {
        var prefix = source switch
        {
            IoPointSource.ExtendedModule => direction == IoPointDirection.Input ? "EDI" : "EDO",
            IoPointSource.AxisOnboard => direction == IoPointDirection.Input ? "AXDI" : "AXDO",
            _ => direction == IoPointDirection.Input ? "DI" : "DO"
        };
        var card = Cards.FirstOrDefault();
        var cardKey = card is null ? "card1" : ResolveCardKey(card);
        var cardNo = card?.CardNo ?? FirstOptionValue(CardNoOptions, ParseShort(GoogolCardNo, 0));
        var module = source == IoPointSource.ExtendedModule
            ? ExtendedModules.FirstOrDefault()
            : null;
        var parentCardKey = module?.ParentCardKey ?? cardKey;
        var parentCardNo = module?.ParentCardNo ?? cardNo;
        var moduleNo = module?.ModuleNo ?? (short)0;
        var moduleConfigPath = module?.ConfigPath ?? GoogolConfigPath?.Trim() ?? string.Empty;
        var axisNo = source == IoPointSource.AxisOnboard ? ResolveFirstAxisNo(cardKey, cardNo) : (short)-1;
        var nextNo = ResolveNextIoPointNo(direction, source, cardKey, cardNo, parentCardKey, parentCardNo, moduleNo, axisNo);
        var item = new IoPointItem
        {
            Key = CreateUniqueKey(prefix, nextNo, IoPoints.Select(point => point.Key)),
            Name = source == IoPointSource.ExtendedModule
                ? direction == IoPointDirection.Input ? $"扩展输入点{nextNo}" : $"扩展输出点{nextNo}"
                : source == IoPointSource.AxisOnboard
                    ? direction == IoPointDirection.Input ? $"轴输入点{nextNo}" : $"轴输出点{nextNo}"
                : direction == IoPointDirection.Input ? $"输入点{nextNo}" : $"输出点{nextNo}",
            Direction = direction,
            Source = source,
            Address = string.Empty,
            CardKey = source == IoPointSource.ExtendedModule ? parentCardKey : cardKey,
            CardNo = source == IoPointSource.ExtendedModule ? parentCardNo : cardNo,
            ParentCardKey = source == IoPointSource.ExtendedModule ? parentCardKey : string.Empty,
            ParentCardNo = source == IoPointSource.ExtendedModule ? parentCardNo : (short)-1,
            ModuleNo = source == IoPointSource.ExtendedModule ? moduleNo : (short)-1,
            AxisNo = source == IoPointSource.AxisOnboard ? axisNo : (short)-1,
            ModuleConfigPath = source == IoPointSource.ExtendedModule ? moduleConfigPath : string.Empty,
            PointNo = nextNo,
            Enabled = true,
            ActiveLow = true
        };

        ConfigureIoPointItem(item);
        IoPoints.Add(item);
        RefreshSelectionOptions();
        AddToConfigurationIoGroup(item);
        MarkDirty("已新增 IO 点，保存后生效。");
        StatusText = source == IoPointSource.ExtendedModule
            ? "已新增扩展IO点，请填写父卡、模块号和模块配置文件。"
            : "已新增IO点，保存后生效。";
    }

    private void RemoveCard(object? parameter)
    {
        if (parameter is not GoogolCardItem card || !Cards.Contains(card))
        {
            return;
        }

        var cardNo = card.CardNo;
        var cardKey = ResolveCardKey(card);
        Cards.Remove(card);
        var removedAxes = RemoveMatching(Axes, axis => IsAxisOnCard(axis, cardKey, cardNo));
        var removedModules = RemoveMatching(ExtendedModules, module => IsModuleOnCard(module, cardKey, cardNo));
        var removedIoPoints = RemoveMatching(
            IoPoints,
            point => IsPointOnCard(point, cardKey, cardNo));

        RefreshConfigurationCollections();
        MarkDirty("已删除轴卡配置，保存后生效。");
        StatusText =
            $"已删除轴卡 C{cardNo}，同时移除 {removedAxes} 个轴、{removedModules} 个扩展模块、{removedIoPoints} 个IO点；保存后生效。";
    }

    private void RemoveExtendedModule(object? parameter)
    {
        if (parameter is not ExtendedIoModuleItem module || !ExtendedModules.Contains(module))
        {
            return;
        }

        ExtendedModules.Remove(module);
        var removedIoPoints = RemoveMatching(
            IoPoints,
            point => point.Source == IoPointSource.ExtendedModule
                && IsPointOnCard(point, module.ParentCardKey, module.ParentCardNo)
                && point.ModuleNo == module.ModuleNo);

        RefreshConfigurationCollections();
        MarkDirty("已删除扩展 IO 模块，保存后生效。");
        StatusText =
            $"已删除扩展模块 C{module.ParentCardNo}-M{module.ModuleNo}，同时移除 {removedIoPoints} 个扩展IO点；保存后生效。";
    }

    private void RemoveAxis(object? parameter)
    {
        if (parameter is not AxisPointItem axis || !Axes.Remove(axis))
        {
            return;
        }

        var removedIoPoints = RemoveMatching(
            IoPoints,
            point => point.Source == IoPointSource.AxisOnboard
                && IsPointOnCard(point, axis.CardKey, axis.CardNo)
                && point.AxisNo == axis.AxisNo);
        RefreshConfigurationCollections();
        MarkDirty("已删除轴，保存后生效。");
        StatusText = $"已删除轴 {axis.Key} / {axis.Name}，同时移除 {removedIoPoints} 个轴上IO点；保存后生效。";
    }

    private void RemoveIoPoint(object? parameter)
    {
        if (parameter is not IoPointItem point || !IoPoints.Remove(point))
        {
            return;
        }

        RefreshConfigurationCollections();
        MarkDirty("已删除 IO 点，保存后生效。");
        StatusText = $"已删除IO点 {point.Key} / {point.Name}，保存后生效。";
    }

    private void RefreshConfigurationCollections()
    {
        RefreshSelectionOptions();
        RebuildConfigurationIoGroups();
    }

    private async Task SaveConfigurationAsync()
    {
        var current = await _configurationRepository.GetAsync();
        var controllerChanged = ResolveAxisControllerKind(AxisControllerMode) != current.AxisController;
        var updated = current with
        {
            AxisController = ResolveAxisControllerKind(AxisControllerMode),
            GoogolCardNo = ParseShort(GoogolCardNo, 0),
            GoogolConfigPath = GoogolConfigPath?.Trim() ?? string.Empty,
            AxisCards = Cards.Select(card => card.ToAxisCardDefinition()).ToArray(),
            GoogolCards = Cards
                .Where(card => card.DriverKind == AxisCardDriverKind.GoogolPulse)
                .Select(card => card.ToDefinition())
                .ToArray(),
            ExtendedIoModules = ExtendedModules.Select(module => module.ToDefinition()).ToArray(),
            Axes = Axes.Select(axis => axis.ToDefinition()).ToArray(),
            IoPoints = IoPoints.Select(point => point.ToDefinition()).ToArray()
        };

        await _configurationRepository.SaveAsync(updated);
        _configuration = await _configurationRepository.GetAsync();
        await _io.ApplyConfigurationAsync(_configuration);
        LoadConfiguration(_configuration);
        StatusText = controllerChanged
            ? "设备配置已保存，轴卡模式变更需要重启软件后生效。"
            : "设备配置已保存。";
    }

    private async Task ReloadConfigurationAsync()
    {
        _configuration = await _configurationRepository.GetAsync();
        LoadConfiguration(_configuration);
        StatusText = "设备配置已重新载入。";
    }

    private static string CreateUniqueKey(string prefix, int start, IEnumerable<string> existingKeys)
    {
        var existing = existingKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var index = Math.Max(start, 1);
        string key;
        do
        {
            key = $"{prefix}{index}";
            index++;
        }
        while (existing.Contains(key));

        return key;
    }

    private static GoogolCardItem CreateDefaultCardItem(DeviceConfiguration configuration)
    {
        return new GoogolCardItem
        {
            Key = "card1",
            Name = $"固高脉冲轴卡 C{configuration.GoogolCardNo}",
            Driver = AxisCardDriverKind.GoogolPulse.ToString(),
            Vendor = "Googol",
            CardNo = configuration.GoogolCardNo,
            AxisCount = Math.Max(8, configuration.Axes.Select(axis => (int)axis.AxisNo).DefaultIfEmpty(1).Max()),
            InputCount = configuration.IoPoints.Count(point => point.Direction == IoPointDirection.Input),
            OutputCount = configuration.IoPoints.Count(point => point.Direction == IoPointDirection.Output),
            ConfigPath = configuration.GoogolConfigPath?.Trim() ?? string.Empty,
            Description = "Default Googol card"
        };
    }

    private static short ParseShort(string? text, short fallback)
    {
        return short.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
               short.TryParse(text, out value)
            ? value
            : fallback;
    }

    private static AxisControllerKind ResolveAxisControllerKind(string? value)
    {
        return Enum.TryParse<AxisControllerKind>(value, true, out var kind)
            ? kind
            : AxisControllerKind.Simulated;
    }
}
