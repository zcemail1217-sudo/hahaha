namespace VisionStation.Domain;

public static class VisionToolDefaults
{
    public static Dictionary<string, string> CreateParameters(VisionToolKind kind)
    {
        return kind switch
        {
            VisionToolKind.ImageProcess => CreateImageProcessParameters(),
            VisionToolKind.DefectDetect => CreateBlobAnalysisParameters(),
            VisionToolKind.TcpCommunication => CreateTcpCommunicationParameters(),
            VisionToolKind.SerialCommunication => CreateSerialCommunicationParameters(),
            _ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    public static Dictionary<string, string> CreateImageProcessParameters()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["operation"] = "Threshold",
            ["thresholdMode"] = "Otsu",
            ["threshold"] = "128",
            ["grayMin"] = "0",
            ["grayMax"] = "128",
            ["polarity"] = "Dark",
            ["adaptiveBlockSize"] = "41",
            ["adaptiveC"] = "5",
            ["filterType"] = "Gaussian",
            ["kernelSize"] = "3",
            ["morphType"] = "Open",
            ["iterations"] = "1",
            ["enhanceType"] = "BrightnessContrast",
            ["contrast"] = "1",
            ["brightness"] = "0",
            ["gamma"] = "1",
            ["geometryType"] = "Flip",
            ["flipMode"] = "Horizontal",
            ["angle"] = "0",
            ["scale"] = "1",
            ["enabledOutputs"] = "ImageOutput,ResultOutput"
        };
    }

    public static Dictionary<string, string> CreateBlobAnalysisParameters()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["thresholdMode"] = "Range",
            ["threshold"] = "128",
            ["grayMin"] = "0",
            ["grayMax"] = "80",
            ["polarity"] = "暗斑",
            ["minArea"] = "30",
            ["maxArea"] = "1000000",
            ["minWidth"] = "0",
            ["maxWidth"] = "1000000",
            ["minHeight"] = "0",
            ["maxHeight"] = "1000000",
            ["minCircularity"] = "0",
            ["maxCircularity"] = "1",
            ["minAspectRatio"] = "0",
            ["maxAspectRatio"] = "1000000",
            ["adaptiveBlockSize"] = "41",
            ["adaptiveC"] = "5",
            ["minCount"] = "1",
            ["maxCount"] = "1000000",
            ["maxResults"] = "128",
            ["selection"] = "Largest",
            ["morphOpen"] = "1",
            ["morphClose"] = "0",
            ["enabledOutputs"] = "CountOutput,BestCenterOutput,BestAreaOutput,BestRectOutput,BestCircleOutput,BestCircularityOutput,ResultOutput"
        };
    }

    public static Dictionary<string, string> CreateTcpCommunicationParameters()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["channelKey"] = "tcp-main",
            ["payload"] = "",
            ["payloadMode"] = "Text",
            ["waitResponse"] = "True",
            ["timeoutMs"] = "1000",
            ["expectedContains"] = "",
            ["enabledOutputs"] = "ResponseOutput,ResultOutput"
        };
    }

    public static Dictionary<string, string> CreateSerialCommunicationParameters()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["channelKey"] = "serial-main",
            ["payload"] = "",
            ["payloadMode"] = "Text",
            ["waitResponse"] = "True",
            ["timeoutMs"] = "1000",
            ["expectedContains"] = "",
            ["enabledOutputs"] = "ResponseOutput,ResultOutput"
        };
    }
}
