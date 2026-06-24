using System.Text.RegularExpressions;
using VisionStation.Domain;

namespace VisionStation.Application;

public static class OperatorMessageLocalizer
{
    public static string LocalizeSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return string.Empty;
        }

        var normalized = source.Trim();
        return SourceNames.TryGetValue(normalized, out var localized)
            ? localized
            : LocalizeCommonText(normalized);
    }

    public static string LocalizeMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        return LocalizeCommonText(message.Trim());
    }

    public static string LocalizeDetails(string details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return string.Empty;
        }

        return LocalizeCommonText(details.Trim());
    }

    public static string LocalizeDeviceState(DeviceConnectionState state)
    {
        return state switch
        {
            DeviceConnectionState.Connecting => "连接中",
            DeviceConnectionState.Connected => "已连接",
            DeviceConnectionState.Faulted => "故障",
            _ => "未连接"
        };
    }

    private static string LocalizeCommonText(string text)
    {
        var result = text;

        result = Regex.Replace(
            result,
            @"Open Googol card\s+(\d+)\s+with\s+GT_Open\.\s+gts\.dll=(.*?)\s+failed",
            match => $"打开固高运动控制卡 {match.Groups[1].Value}（GT_Open）失败，gts.dll：{match.Groups[2].Value}",
            RegexOptions.IgnoreCase);

        result = Regex.Replace(
            result,
            @"Open Googol card\s+(\d+)\s+with\s+GT_Open",
            match => $"打开固高运动控制卡 {match.Groups[1].Value}（GT_Open）",
            RegexOptions.IgnoreCase);

        result = Regex.Replace(
            result,
            @"Open HCB2 extended IO on card\s+(\d+)",
            match => $"打开 HCB2 扩展 IO（卡 {match.Groups[1].Value}）",
            RegexOptions.IgnoreCase);

        result = Regex.Replace(
            result,
            @"Googol return code=([-\d]+)\s*\(([^)]*)\)",
            match => $"固高返回码 {match.Groups[1].Value}（{LocalizeGoogolReturnCode(match.Groups[1].Value, match.Groups[2].Value)}）",
            RegexOptions.IgnoreCase);

        result = Regex.Replace(
            result,
            @"Googol return code=([-\d]+)",
            match => $"固高返回码 {match.Groups[1].Value}{BuildGoogolReturnCodeSuffix(match.Groups[1].Value)}",
            RegexOptions.IgnoreCase);

        result = Regex.Replace(
            result,
            @"GT_Open\(cardNo=(\d+),\s*channel=(\d+),\s*param=(\d+)\)",
            match => $"GT_Open（卡号={match.Groups[1].Value}，通道={match.Groups[2].Value}，参数={match.Groups[3].Value}）",
            RegexOptions.IgnoreCase);

        result = Regex.Replace(
            result,
            @"Device\s+(.+?)\s+is\s+faulted\.",
            match => $"设备 {LocalizeSource(match.Groups[1].Value)} 发生故障。",
            RegexOptions.IgnoreCase);

        result = Regex.Replace(
            result,
            @"Device\s+(.+?)\s+is\s+disconnected\.",
            match => $"设备 {LocalizeSource(match.Groups[1].Value)} 已断开。",
            RegexOptions.IgnoreCase);

        foreach (var replacement in Replacements)
        {
            result = result.Replace(replacement.English, replacement.Chinese, StringComparison.OrdinalIgnoreCase);
        }

        result = Regex.Replace(result, @"\s+。", "。");
        result = Regex.Replace(result, @"\s+，", "，");
        result = Regex.Replace(result, @"\s+：", "：");

        return result;
    }

    private static string BuildGoogolReturnCodeSuffix(string code)
    {
        var localized = LocalizeGoogolReturnCode(code, string.Empty);
        return string.IsNullOrWhiteSpace(localized) ? string.Empty : $"（{localized}）";
    }

    private static string LocalizeGoogolReturnCode(string code, string description)
    {
        var normalizedDescription = description.Trim();
        return code.Trim() switch
        {
            "-2" => "读数据长度错误",
            "-3" => "读数据校验错误",
            "-4" => "写数据块错误",
            "-5" => "读数据块错误",
            "-6" => "打开/关闭设备失败：未检测到轴卡、驱动不可用、卡数量不匹配或 PCI 通讯创建失败",
            "-7" => "DSP 忙",
            "-8" => "多线程资源忙或 PCI 通讯超时",
            "-16" => "当前驱动、DLL、卡型号或卡号不支持 GT_OpenDevice，可能需要改用 GT_Open",
            "1" => "命令调用无效或前置命令未完成",
            "7" => "参数错误",
            "8" => "DSP 固件不支持该命令",
            _ when !string.IsNullOrWhiteSpace(normalizedDescription) => LocalizeCommonTextWithoutReturnCode(normalizedDescription),
            _ => string.Empty
        };
    }

    private static string LocalizeCommonTextWithoutReturnCode(string text)
    {
        var result = text;
        foreach (var replacement in Replacements)
        {
            result = result.Replace(replacement.English, replacement.Chinese, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    private static readonly (string English, string Chinese)[] Replacements =
    [
        ("GoogolPulse", "固高脉冲轴卡"),
        ("GoogolBus", "固高总线轴卡"),
        ("Googol axis cards disconnected", "固高轴卡已断开"),
        ("Googol axis card is not connected. Last state:", "固高轴卡未连接，上次状态："),
        ("Googol axis emergency stop triggered", "固高轴卡急停触发"),
        ("Googol axis card", "固高轴卡"),
        ("Open Googol IO", "打开固高 IO"),
        ("Googol IO disconnected", "固高 IO 已断开"),
        ("Googol IO", "固高 IO"),
        ("Simulated PLC", "模拟 PLC"),
        ("PLC disconnected", "PLC 已断开"),
        ("Not connected", "未连接"),
        ("Disconnected", "未连接"),
        ("Connection failed:", "连接失败："),
        ("Connect failed:", "连接失败："),
        ("Startup failed:", "启动失败："),
        ("VisionStation startup completed", "VisionStation 启动完成"),
        ("Single inspection failed:", "单次检测失败："),
        ("Continuous production stopped:", "连续生产已停止："),
        ("Process flow completed", "流程执行完成"),
        ("Device command", "设备命令"),
        ("failed on", "执行失败，设备："),
        ("is missing an address", "缺少地址"),
        ("cannot load gts.dll. Check process bitness and PATH.", "无法加载 gts.dll，请检查程序位数和 PATH 环境变量。"),
        ("gts.dll raised an unmanaged exception. Check the Googol driver, card state, config file, and whether another process is using the card.", "gts.dll 发生非托管异常，请检查固高驱动、轴卡状态、配置文件，以及是否有其他程序占用轴卡。"),
        ("gts.dll raised an unmanaged exception. Check the Googol driver, card state, extension module config, and whether another process is using the card.", "gts.dll 发生非托管异常，请检查固高驱动、轴卡状态、扩展模块配置，以及是否有其他程序占用轴卡。"),
        ("gts.dll raised an unmanaged exception; verify the Googol driver/card state and close any other process using the card.", "gts.dll 发生非托管异常，请检查固高驱动和轴卡状态，并关闭其他占用轴卡的程序。"),
        ("loaded path unavailable", "已加载，但无法获取路径"),
        ("not loaded", "未加载"),
        ("read data length error", "读数据长度错误"),
        ("read data checksum error", "读数据校验错误"),
        ("write data block error", "写数据块错误"),
        ("read data block error", "读数据块错误"),
        ("open/close device error: card missing, driver unavailable, card count mismatch, or PCI communication creation failed", "打开/关闭设备失败：未检测到轴卡、驱动不可用、卡数量不匹配或 PCI 通讯创建失败"),
        ("open/close device error: card missing, driver unavailable, or AxisCard disabled", "打开/关闭设备失败：未检测到轴卡、驱动不可用或轴卡被禁用"),
        ("card missing", "未检测到轴卡"),
        ("driver unavailable", "驱动不可用"),
        ("card count mismatch", "卡数量不匹配"),
        ("PCI communication creation failed", "PCI 通讯创建失败"),
        ("AxisCard disabled", "轴卡被禁用"),
        ("DSP busy", "DSP 忙"),
        ("multithread resource busy or PCI communication timeout", "多线程资源忙或 PCI 通讯超时"),
        ("GT_OpenDevice is unavailable for the current driver, DLL, card type, or card index; falling back to GT_Open may be required", "当前驱动、DLL、卡型号或卡号不支持 GT_OpenDevice，可能需要改用 GT_Open"),
        ("invalid command call or prerequisite command not completed", "命令调用无效或前置命令未完成"),
        ("parameter error", "参数错误"),
        ("DSP firmware does not support this command", "DSP 固件不支持该命令"),
        ("see Googol GTS manual", "请查阅固高 GTS 手册"),
        ("home completed", "回原完成"),
        ("home failed. Error=", "回原失败，错误码="),
        ("axis linear interpolation completed", "轴直线插补完成"),
        ("is faulted.", "发生故障。"),
        ("is disconnected.", "已断开。"),
        ("Alarm cache load failed:", "报警缓存加载失败："),
        ("Alarm persistence failed:", "报警持久化失败："),
        ("UI exception captured. Crash file:", "已捕获界面异常，崩溃日志："),
        ("UI exception captured:", "已捕获界面异常："),
        ("Fatal exception captured. Terminating=", "已捕获致命异常，是否即将退出："),
        ("Fatal exception captured:", "已捕获致命异常："),
        ("Unobserved task exception captured. Crash file:", "已捕获未观察的后台任务异常，崩溃日志："),
        ("Background task exception:", "后台任务异常："),
        ("Crash file:", "崩溃日志："),
        ("Terminating=", "是否即将退出："),
        ("Waiting", "等待中"),
        ("Faulted", "故障"),
        ("Stopped", "停止"),
        ("Running", "运行"),
        ("Paused", "暂停"),
        ("Error", "错误"),
        ("failed:", "失败："),
        ("failed.", "失败。"),
        (" failed", "失败"),
        ("completed", "完成"),
        ("unavailable", "不可用"),
        ("missing", "缺失")
    ];

    private static readonly IReadOnlyDictionary<string, string> SourceNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Production"] = "生产",
        ["Inspection"] = "检测",
        ["Startup"] = "启动",
        ["System"] = "系统",
        ["Alarm"] = "报警",
        ["Crash"] = "崩溃保护",
        ["Task"] = "后台任务",
        ["VisionDebug"] = "视觉调试",
        ["ProcessFlow"] = "流程",
        ["Simulated PLC"] = "模拟 PLC",
        ["GoogolPulse"] = "固高脉冲轴卡",
        ["GoogolBus"] = "固高总线轴卡",
        ["Googol axis card"] = "固高轴卡",
        ["Googol IO"] = "固高 IO"
    };
}
