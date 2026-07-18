using VisionStation.Domain;

namespace VisionStation.Vision.UI.ViewModels;

public static class VisionToolCatalog
{
    public const int DefaultResultInputCount = 1;

    private const int MaxResultInputCount = 32;

    public static IReadOnlyList<CatalogPortDefinition> GetInputPorts(VisionToolKind kind, string? measurementMode = null)
    {
        if (kind == VisionToolKind.MeasureDistance)
        {
            return NormalizeMeasurementMode(measurementMode) switch
            {
                "PointLine" =>
                [
                    new("PointInput", "点", "Point"),
                    new("LineInput", "线", "Line")
                ],
                "LineLine" =>
                [
                    new("Line1Input", "线1", "Line"),
                    new("Line2Input", "线2", "Line")
                ],
                _ =>
                [
                    new("Point1Input", "点1", "Point"),
                    new("Point2Input", "点2", "Point")
                ]
            };
        }

        return kind switch
        {
            VisionToolKind.ImageProcess =>
            [
                new("ImageInput", "输入图像", "Image")
            ],
            VisionToolKind.TemplateLocate =>
            [
                new("ImageInput", "输入图像", "Image")
            ],
            VisionToolKind.MultiTargetMatch =>
            [
                new("ImageInput", "输入图像", "Image"),
                new("PositionInput", "模板位置", "Pose")
            ],
            VisionToolKind.CoordinateTransform =>
            [
                new("PointInput", "图像点", "Point"),
                new("PositionInput", "图像位姿", "Pose")
            ],
            VisionToolKind.RoiMap =>
            [
                new("PositionInput", "输入位置", "Pose")
            ],
            VisionToolKind.FindLine or VisionToolKind.FindCircle or VisionToolKind.DefectDetect =>
            [
                new("ImageInput", "输入图像", "Image"),
                new("PositionInput", "模板位置", "Pose")
            ],
            VisionToolKind.LineAngle or VisionToolKind.LineIntersection =>
            [
                new("Line1Input", "线1", "Line"),
                new("Line2Input", "线2", "Line")
            ],
            VisionToolKind.FitLineFromPoints =>
            [
                new("Point1Input", "点1", "Point"),
                new("Point2Input", "点2", "Point")
            ],
            VisionToolKind.TemplatePoint =>
            [
                new("PositionInput", "模板位置", "Pose")
            ],
            VisionToolKind.CodeRead or VisionToolKind.Ocr =>
            [
                new("ImageInput", "输入图像", "Image")
            ],
            VisionToolKind.Judge =>
            [
                new("ResultInput", "工具结果", "Result")
            ],
            VisionToolKind.Result =>
                GetResultInputPorts(),
            _ => []
        };
    }

    public static IReadOnlyList<CatalogPortDefinition> GetResultInputPorts(IReadOnlyDictionary<string, string>? parameters = null)
    {
        var count = ResolveResultInputCount(parameters);
        return Enumerable.Range(1, count)
            .Select(index => new CatalogPortDefinition($"ResultInput{index}", $"结果{index}", GetResultInputDataType(parameters, index)))
            .ToArray();
    }

    public static IReadOnlyList<CatalogPortDefinition> GetOutputPorts(VisionToolKind kind)
    {
        return kind switch
        {
            VisionToolKind.ImageProcess =>
            [
                new("ImageOutput", "输出图像", "Image"),
                new("ResultOutput", "OK/NG", "Result")
            ],
            VisionToolKind.AcquireImage =>
            [
                new("ImageOutput", "输出图像", "Image")
            ],
            VisionToolKind.TemplateLocate =>
            [
                new("PositionOutput", "位置", "Pose"),
                new("ScoreOutput", "分数", "Number"),
                new("XOutput", "X坐标", "Number"),
                new("YOutput", "Y坐标", "Number"),
                new("AngleOutput", "角度", "Number"),
                new("ScaleOutput", "尺度", "Number"),
                new("OriginOutput", "训练原点", "Pose"),
                new("ResultOutput", "OK/NG", "Result")
            ],
            VisionToolKind.MultiTargetMatch =>
            [
                new("CountOutput", "数量", "Number"),
                new("PositionOutput", "位置", "Pose"),
                new("ScoreOutput", "分数", "Number"),
                new("XOutput", "X坐标", "Number"),
                new("YOutput", "Y坐标", "Number"),
                new("AngleOutput", "角度", "Number"),
                new("OriginOutput", "训练原点", "Pose"),
                new("BestPositionOutput", "最佳位置", "Pose"),
                new("AllPositionsOutput", "全部位置", "Pose[]"),
                new("ScoresOutput", "全部分数", "Number[]"),
                new("ScalesOutput", "全部尺度", "Number[]"),
                new("ResultOutput", "OK/NG", "Result")
            ],
            VisionToolKind.CoordinateTransform =>
            [
                new("PointOutput", "世界点", "Point"),
                new("PositionOutput", "世界位姿", "Pose"),
                new("XOutput", "世界X", "Number"),
                new("YOutput", "世界Y", "Number"),
                new("AngleOutput", "世界角度", "Number"),
                new("ResultOutput", "OK/NG", "Result")
            ],
            VisionToolKind.RoiMap =>
            [
                new("RoiOutput", "映射ROI", "Roi"),
                new("ResultOutput", "OK/NG", "Result")
            ],
            VisionToolKind.FindLine =>
            [
                new("LineOutput", "直线", "Line"),
                new("MidPointOutput", "中点", "Point"),
                new("ResultOutput", "OK/NG", "Result")
            ],
            VisionToolKind.FindCircle =>
            [
                new("CircleOutput", "圆", "Circle"),
                new("CenterOutput", "圆心", "Point"),
                new("RadiusOutput", "半径", "Number"),
                new("ResultOutput", "OK/NG", "Result")
            ],
            VisionToolKind.MeasureDistance =>
            [
                new("MeasureValueOutput", "测量值", "Number"),
                new("FootPointOutput", "垂足", "Point"),
                new("DeviationOutput", "偏差", "Number"),
                new("AbsDeviationOutput", "绝对偏差", "Number"),
                new("MarginOutput", "公差余量", "Number"),
                new("NominalOutput", "名义值", "Number"),
                new("LowerLimitOutput", "下限", "Number"),
                new("UpperLimitOutput", "上限", "Number"),
                new("ResultOutput", "OK/NG", "Result")
            ],
            VisionToolKind.LineAngle =>
            [
                new("AngleOutput", "角度", "Number"),
                new("MeasureValueOutput", "测量值", "Number"),
                new("ResultOutput", "OK/NG", "Result")
            ],
            VisionToolKind.LineIntersection =>
            [
                new("PointOutput", "交点", "Point"),
                new("XOutput", "X坐标", "Number"),
                new("YOutput", "Y坐标", "Number"),
                new("ResultOutput", "OK/NG", "Result")
            ],
            VisionToolKind.FitLineFromPoints =>
            [
                new("LineOutput", "直线", "Line"),
                new("MidPointOutput", "中点", "Point"),
                new("AngleOutput", "角度", "Number"),
                new("LengthOutput", "长度", "Number"),
                new("ResultOutput", "OK/NG", "Result")
            ],
            VisionToolKind.TemplatePoint =>
            [
                new("PointOutput", "点", "Point"),
                new("XOutput", "X坐标", "Number"),
                new("YOutput", "Y坐标", "Number"),
                new("ResultOutput", "OK/NG", "Result")
            ],
            VisionToolKind.CodeRead =>
            [
                new("CodeOutput", "条码", "Text"),
                new("ResultOutput", "OK/NG", "Result")
            ],
            VisionToolKind.Ocr =>
            [
                new("TextOutput", "字符", "Text"),
                new("ResultOutput", "OK/NG", "Result")
            ],
            VisionToolKind.DefectDetect =>
            [
                new("CountOutput", "数量", "Number"),
                new("BestCenterOutput", "中心", "Point"),
                new("AllCentersOutput", "全部中心", "Point[]"),
                new("BestAreaOutput", "面积", "Number"),
                new("BestRectOutput", "外接矩形", "Roi"),
                new("BestCircleOutput", "外接圆", "Circle"),
                new("BestWidthOutput", "宽度", "Number"),
                new("BestHeightOutput", "高度", "Number"),
                new("BestAspectRatioOutput", "长宽比", "Number"),
                new("BestPerimeterOutput", "周长", "Number"),
                new("BestCircularityOutput", "圆度", "Number"),
                new("BestContourOutput", "轮廓点", "Point[]"),
                new("ResultOutput", "OK/NG", "Result")
            ],
            VisionToolKind.Judge =>
            [
                new("OverallResultOutput", "综合判定", "Result")
            ],
            VisionToolKind.Result => [],
            VisionToolKind.TcpCommunication or VisionToolKind.SerialCommunication =>
            [
                new("ResponseOutput", "响应文本", "Text"),
                new("ResponseHexOutput", "响应HEX", "Text"),
                new("ResponseBytesOutput", "字节数", "Number"),
                new("ResultOutput", "OK/NG", "Result")
            ],
            _ =>
            [
                new("ResultOutput", "输出结果", "Result")
            ]
        };
    }

    public static HashSet<string> GetDefaultOutputKeys(VisionToolKind kind, string? measurementMode = null)
    {
        var keys = kind switch
        {
            VisionToolKind.ImageProcess => new[] { "ImageOutput", "ResultOutput" },
            VisionToolKind.TemplateLocate => new[] { "PositionOutput", "OriginOutput", "ScaleOutput", "ResultOutput" },
            VisionToolKind.MultiTargetMatch => new[] { "CountOutput", "PositionOutput", "OriginOutput", "BestPositionOutput", "ScalesOutput", "ResultOutput" },
            VisionToolKind.CoordinateTransform => new[] { "PointOutput", "PositionOutput", "XOutput", "YOutput", "AngleOutput", "ResultOutput" },
            VisionToolKind.FindLine => new[] { "LineOutput", "MidPointOutput", "ResultOutput" },
            VisionToolKind.FindCircle => new[] { "CircleOutput", "CenterOutput", "ResultOutput" },
            VisionToolKind.MeasureDistance when string.Equals(NormalizeMeasurementMode(measurementMode), "PointLine", StringComparison.OrdinalIgnoreCase) =>
                new[] { "MeasureValueOutput", "FootPointOutput", "ResultOutput" },
            VisionToolKind.MeasureDistance => new[] { "MeasureValueOutput", "ResultOutput" },
            VisionToolKind.LineAngle => new[] { "AngleOutput", "MeasureValueOutput", "ResultOutput" },
            VisionToolKind.LineIntersection => new[] { "PointOutput", "ResultOutput" },
            VisionToolKind.FitLineFromPoints => new[] { "LineOutput", "MidPointOutput", "ResultOutput" },
            VisionToolKind.TemplatePoint => new[] { "PointOutput", "ResultOutput" },
            VisionToolKind.DefectDetect => new[] { "CountOutput", "BestCenterOutput", "BestAreaOutput", "BestRectOutput", "BestCircleOutput", "BestCircularityOutput", "ResultOutput" },
            _ => GetOutputPorts(kind).Select(port => port.Key).ToArray()
        };

        return keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static Dictionary<string, string> GetDefaultParameters(VisionToolKind kind)
    {
        return kind switch
        {
            VisionToolKind.ImageProcess => VisionToolDefaults.CreateImageProcessParameters(),
            VisionToolKind.TemplateLocate => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["engine"] = "OpenCv",
                ["matchMode"] = "Shape",
                ["autoLearnTemplate"] = "False",
                ["minScore"] = "0.85",
                ["angleStart"] = "-45",
                ["angleExtent"] = "90",
                ["angleStep"] = "2",
                ["cannyLow"] = "60",
                ["cannyHigh"] = "160",
                ["orbMaxFeatures"] = "600",
                ["orbMinMatches"] = "8",
                ["orbRatio"] = "0.75",
                ["shapeScoreVersion"] = "2",
                ["shapeCoverageDistance"] = "3"
            },
            VisionToolKind.MultiTargetMatch => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["engine"] = "OpenCv",
                ["matchMode"] = "Shape",
                ["multiMatchMode"] = "Shape",
                ["autoLearnTemplate"] = "False",
                ["minScore"] = "0.75",
                ["minCount"] = "1",
                ["matchCount"] = "128",
                ["angleStart"] = "-30",
                ["angleExtent"] = "60",
                ["angleStep"] = "5",
                ["cannyLow"] = "60",
                ["cannyHigh"] = "160",
                ["nmsOverlap"] = "0.35",
                ["enabledOutputs"] = "CountOutput,PositionOutput,OriginOutput,BestPositionOutput,ScalesOutput,ResultOutput"
            },
            VisionToolKind.CoordinateTransform => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["model"] = "Affine",
                ["unit"] = "mm",
                ["imageToWorldMatrix"] = "",
                ["enabledOutputs"] = "PointOutput,PositionOutput,XOutput,YOutput,AngleOutput,ResultOutput"
            },
            VisionToolKind.FindLine => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["minScore"] = "0.5",
                ["caliperCount"] = "20",
                ["edgeThreshold"] = "30",
                ["linePolarity"] = "从暗到明",
                ["resultSelection"] = "最强",
                ["caliperWidth"] = "4",
                ["extendLine"] = "False",
                ["enabledOutputs"] = "LineOutput,MidPointOutput,ResultOutput"
            },
            VisionToolKind.FindCircle => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["minScore"] = "0.5",
                ["caliperCount"] = "24",
                ["edgeThreshold"] = "30",
                ["circlePolarity"] = "从暗到明",
                ["searchDirection"] = "从内到外",
                ["resultSelection"] = "最强",
                ["caliperWidth"] = "4",
                ["searchWidth"] = "24",
                ["enabledOutputs"] = "CircleOutput,CenterOutput,ResultOutput"
            },
            VisionToolKind.MeasureDistance => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["measurementMode"] = "PointPoint",
                ["enabledOutputs"] = "MeasureValueOutput,ResultOutput"
            },
            VisionToolKind.LineAngle => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["enabledOutputs"] = "AngleOutput,MeasureValueOutput,ResultOutput"
            },
            VisionToolKind.LineIntersection => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["enabledOutputs"] = "PointOutput,ResultOutput"
            },
            VisionToolKind.FitLineFromPoints => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["enabledOutputs"] = "LineOutput,MidPointOutput,ResultOutput"
            },
            VisionToolKind.TemplatePoint => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["pointX"] = "0",
                ["pointY"] = "0",
                ["referenceX"] = "0",
                ["referenceY"] = "0",
                ["referenceAngle"] = "0",
                ["enabledOutputs"] = "PointOutput,ResultOutput"
            },
            VisionToolKind.CodeRead => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["symbology"] = "DataMatrix"
            },
            VisionToolKind.Ocr => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["charset"] = "A-Z0-9"
            },
            VisionToolKind.DefectDetect => VisionToolDefaults.CreateBlobAnalysisParameters(),
            VisionToolKind.TcpCommunication => VisionToolDefaults.CreateTcpCommunicationParameters(),
            VisionToolKind.SerialCommunication => VisionToolDefaults.CreateSerialCommunicationParameters(),
            VisionToolKind.Result => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["resultInputCount"] = DefaultResultInputCount.ToString(),
                ["input:ResultInput1:dataType"] = "enum"
            },
            _ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    public static string GetDefaultName(VisionToolKind kind)
    {
        return kind switch
        {
            VisionToolKind.ImageProcess => "图像处理",
            VisionToolKind.AcquireImage => "采图",
            VisionToolKind.TemplateLocate => "模板定位",
            VisionToolKind.MultiTargetMatch => "多目标匹配",
            VisionToolKind.CoordinateTransform => "坐标转换",
            VisionToolKind.RoiMap => "ROI映射",
            VisionToolKind.FindLine => "找线",
            VisionToolKind.FindCircle => "找圆",
            VisionToolKind.MeasureDistance => "尺寸测量",
            VisionToolKind.LineAngle => "线线角度",
            VisionToolKind.LineIntersection => "线线交点",
            VisionToolKind.FitLineFromPoints => "两点拟合线",
            VisionToolKind.TemplatePoint => "模板点查找",
            VisionToolKind.CodeRead => "条码识别",
            VisionToolKind.Ocr => "字符识别",
            VisionToolKind.DefectDetect => "斑点分析",
            VisionToolKind.Judge => "综合判定",
            VisionToolKind.Result => "结果",
            VisionToolKind.TcpCommunication => "TCP通讯",
            VisionToolKind.SerialCommunication => "串口通讯",
            _ => "视觉工具"
        };
    }

    public static IReadOnlyList<ToolboxCatalogItem> GetToolboxCategories()
    {
        return
        [
            new("00.设备与运动", "\uE8EF",
            [
                new("采集图像", "\uE722", VisionToolKind.AcquireImage),
                new("延时工具", "\uE823"),
                new("点位运动", "\uE8B9"),
                new("单轴移动", "\uE8AB")
            ]),
            new("01.图像输入输出", "\uE8A9",
            [
                new("图像显示", "\uE8A7"),
                new("数据显示", "\uE9D2"),
                new("存储图像", "\uE74E"),
                new("图像脚本", "\uE713"),
                new("图像处理", "\uE70F", VisionToolKind.ImageProcess),
                new("二值化", "\uE9D9", VisionToolKind.ImageProcess),
                new("滤波降噪", "\uE71A", VisionToolKind.ImageProcess),
                new("形态学", "\uE8EE", VisionToolKind.ImageProcess),
                new("图像增强", "\uE7B8", VisionToolKind.ImageProcess),
                new("几何变换", "\uE8B9", VisionToolKind.ImageProcess)
            ]),
            new("02.定位匹配", "\uE721",
            [
                new("创建ROI", "\uE707", VisionToolKind.RoiMap),
                new("模板匹配", "\uE8D4", VisionToolKind.TemplateLocate),
                new("多目标匹配", "\uE8B3", VisionToolKind.MultiTargetMatch),
                new("产品抓取", "\uE8A7")
            ]),
            new("03.标定与坐标系", "\uE7B7",
            [
                new("坐标转换", "\uE8B9", VisionToolKind.CoordinateTransform)
            ]),
            new("04.边缘几何", "\uE8C1",
            [
                new("直线工具", "\uE8C1", VisionToolKind.FindLine),
                new("圆形测量", "\uEA3A", VisionToolKind.FindCircle),
                new("矩形工具", "\uE8A9"),
                new("点线距离", "\uE8C1", VisionToolKind.MeasureDistance, "PointLine"),
                new("点点距离", "\uE8C1", VisionToolKind.MeasureDistance, "PointPoint"),
                new("线线距离", "\uE8C1", VisionToolKind.MeasureDistance, "LineLine"),
                new("线线角度", "\uE8C1", VisionToolKind.LineAngle),
                new("线线交点", "\uE8C1", VisionToolKind.LineIntersection),
                new("两点拟合线", "\uE8C1", VisionToolKind.FitLineFromPoints),
                new("模板点查找", "\uE721", VisionToolKind.TemplatePoint)
            ]),
            new("05.识别检测", "\uE8EF",
            [
                new("二维码", "\uE8EF", VisionToolKind.CodeRead),
                new("字符识别", "\uE8D2", VisionToolKind.Ocr),
                new("斑点分析", "\uE9D9", VisionToolKind.DefectDetect),
                new("创建点集", "\uE710")
            ]),
            new("06.逻辑输出", "\uE9D5",
            [
                new("结果工具", "\uE9D2", VisionToolKind.Result),
                new("综合判定", "\uE9D5", VisionToolKind.Judge)
            ])
        ];
    }

    public static string NormalizeMeasurementMode(string? value)
    {
        return value?.Trim() switch
        {
            "PointLine" or "point_line" or "点线距离" => "PointLine",
            "LineLine" or "line_line" or "线线距离" => "LineLine",
            _ => "PointPoint"
        };
    }

    private static int ResolveResultInputCount(IReadOnlyDictionary<string, string>? parameters)
    {
        var count = DefaultResultInputCount;
        if (parameters is not null &&
            parameters.TryGetValue("resultInputCount", out var countText) &&
            int.TryParse(countText, out var configuredCount))
        {
            count = configuredCount;
        }

        if (parameters is not null)
        {
            foreach (var key in parameters.Keys)
            {
                var index = TryGetResultInputIndex(key);
                if (index > count)
                {
                    count = index;
                }
            }
        }

        return Math.Clamp(count, 0, MaxResultInputCount);
    }

    private static string GetResultInputDataType(IReadOnlyDictionary<string, string>? parameters, int index)
    {
        if (parameters is not null &&
            parameters.TryGetValue($"input:ResultInput{index}:dataType", out var dataType) &&
            !string.IsNullOrWhiteSpace(dataType))
        {
            return VisionResultDataTypeMapper.ToVariableDataType(dataType);
        }

        return "enum";
    }

    private static int TryGetResultInputIndex(string key)
    {
        const string prefix = "input:ResultInput";
        if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var end = key.IndexOf(':', prefix.Length);
        if (end <= prefix.Length)
        {
            return 0;
        }

        return int.TryParse(key[prefix.Length..end], out var index) ? index : 0;
    }
}

public sealed record CatalogPortDefinition(string Key, string Name, string DataType);

public sealed record ToolboxCatalogItem(
    string Name,
    string Icon,
    VisionToolKind? Kind = null,
    string? MeasurementMode = null,
    IReadOnlyList<ToolboxCatalogItem>? Children = null)
{
    public ToolboxCatalogItem(string name, string icon, IReadOnlyList<ToolboxCatalogItem> children)
        : this(name, icon, null, null, children)
    {
    }
}
