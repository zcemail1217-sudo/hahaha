namespace VisionStation.Vision;

public static class TemplateMatchingDiagnosticCodes
{
    public const string ConfigUnknownEngine = "CONFIG_UNKNOWN_ENGINE";
    public const string ConfigServiceRequired = "CONFIG_SERVICE_REQUIRED";
    public const string ConfigUnsupportedMode = "CONFIG_UNSUPPORTED_MODE";
    public const string ConfigInvalidParameter = "CONFIG_INVALID_PARAMETER";
    public const string RuntimeNotFound = "RUNTIME_NOT_FOUND";
    public const string RuntimeArchMismatch = "RUNTIME_ARCH_MISMATCH";
    public const string RuntimeVersionMismatch = "RUNTIME_VERSION_MISMATCH";
    public const string LicenseUnavailable = "LICENSE_UNAVAILABLE";
    public const string ModelPathInvalid = "MODEL_PATH_INVALID";
    public const string ModelNotFound = "MODEL_NOT_FOUND";
    public const string ModelChecksumMismatch = "MODEL_CHECKSUM_MISMATCH";
    public const string ModelMetadataInvalid = "MODEL_METADATA_INVALID";
    public const string ModelVersionMismatch = "MODEL_VERSION_MISMATCH";
    public const string ModelRelearnRequired = "MODEL_RELEARN_REQUIRED";
    public const string ModelLoadFailed = "MODEL_LOAD_FAILED";
    public const string ModelTemplateIncomplete = "MODEL_TEMPLATE_INCOMPLETE";
    public const string ModelContrastWeak = "MODEL_CONTRAST_WEAK";
    public const string ModelInternalFeaturesWeak = "MODEL_INTERNAL_FEATURES_WEAK";
    public const string MatchInvalidPose = "MATCH_INVALID_POSE";
    public const string MatchIncompleteAtBoundary = "MATCH_INCOMPLETE_AT_BOUNDARY";
    public const string MatchPolarityMismatch = "MATCH_POLARITY_MISMATCH";
    public const string MatchOuterContourWeak = "MATCH_OUTER_CONTOUR_WEAK";
    public const string MatchInnerFeaturesWeak = "MATCH_INNER_FEATURES_WEAK";
    public const string MatchDuplicateOverlap = "MATCH_DUPLICATE_OVERLAP";
    public const string MatchTimeout = "MATCH_TIMEOUT";
    public const string MatchCandidateLimitReached = "MATCH_CANDIDATE_LIMIT_REACHED";
    public const string MatchOperatorFailed = "MATCH_OPERATOR_FAILED";
}

public static class TemplateMatchingFailureStages
{
    public const string Configuration = "Configuration";
    public const string Runtime = "Runtime";
    public const string Model = "Model";
    public const string Match = "Match";
}

public sealed record TemplateMatchingDiagnostic(
    string Code,
    string UserMessage,
    string FailureStage,
    string? TechnicalDetails = null);

public static class TemplateMatchingDiagnostics
{
    public static TemplateMatchingDiagnostic Create(string code, string? technicalDetails = null)
    {
        return new TemplateMatchingDiagnostic(
            code,
            GetUserMessage(code),
            GetFailureStage(code),
            technicalDetails);
    }

    private static string GetUserMessage(string code)
    {
        return code switch
        {
            TemplateMatchingDiagnosticCodes.ConfigUnknownEngine => "模板匹配引擎配置无效。",
            TemplateMatchingDiagnosticCodes.ConfigServiceRequired => "当前匹配引擎需要通过模板匹配服务运行。",
            TemplateMatchingDiagnosticCodes.ConfigUnsupportedMode => "当前匹配引擎不支持所选匹配模式。",
            TemplateMatchingDiagnosticCodes.ConfigInvalidParameter => "模板匹配参数无效，请检查配方设置。",
            TemplateMatchingDiagnosticCodes.RuntimeNotFound => "未找到模板匹配运行时。",
            TemplateMatchingDiagnosticCodes.RuntimeArchMismatch => "模板匹配运行时架构不匹配。",
            TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch => "模板匹配运行时版本不匹配。",
            TemplateMatchingDiagnosticCodes.LicenseUnavailable => "模板匹配运行许可不可用。",
            TemplateMatchingDiagnosticCodes.ModelPathInvalid => "模板模型路径无效。",
            TemplateMatchingDiagnosticCodes.ModelNotFound => "未找到模板模型。",
            TemplateMatchingDiagnosticCodes.ModelChecksumMismatch => "模板模型完整性校验失败。",
            TemplateMatchingDiagnosticCodes.ModelMetadataInvalid => "模板模型元数据无效。",
            TemplateMatchingDiagnosticCodes.ModelVersionMismatch => "模板模型版本不匹配。",
            TemplateMatchingDiagnosticCodes.ModelRelearnRequired => "模板参数已变化，请重新学习模型。",
            TemplateMatchingDiagnosticCodes.ModelLoadFailed => "模板模型加载失败。",
            TemplateMatchingDiagnosticCodes.ModelTemplateIncomplete => "模板区域不完整，无法学习模型。",
            TemplateMatchingDiagnosticCodes.ModelContrastWeak => "模板前景与背景对比不足。",
            TemplateMatchingDiagnosticCodes.ModelInternalFeaturesWeak => "模板内部特征不足。",
            TemplateMatchingDiagnosticCodes.MatchInvalidPose => "匹配位姿无效。",
            TemplateMatchingDiagnosticCodes.MatchIncompleteAtBoundary => "候选目标在图像边界处不完整。",
            TemplateMatchingDiagnosticCodes.MatchPolarityMismatch => "候选目标明暗极性不匹配。",
            TemplateMatchingDiagnosticCodes.MatchOuterContourWeak => "候选目标外轮廓覆盖不足。",
            TemplateMatchingDiagnosticCodes.MatchInnerFeaturesWeak => "候选目标内部特征覆盖不足。",
            TemplateMatchingDiagnosticCodes.MatchDuplicateOverlap => "候选目标与已有结果重复。",
            TemplateMatchingDiagnosticCodes.MatchTimeout => "模板匹配搜索超时。",
            TemplateMatchingDiagnosticCodes.MatchCandidateLimitReached => "候选数量达到安全上限，无法确认结果。",
            TemplateMatchingDiagnosticCodes.MatchOperatorFailed => "模板匹配算子执行失败。",
            _ => "模板匹配失败。"
        };
    }

    private static string GetFailureStage(string code)
    {
        if (code.StartsWith("CONFIG_", StringComparison.Ordinal))
        {
            return TemplateMatchingFailureStages.Configuration;
        }

        if (code.StartsWith("RUNTIME_", StringComparison.Ordinal) ||
            string.Equals(code, TemplateMatchingDiagnosticCodes.LicenseUnavailable, StringComparison.Ordinal))
        {
            return TemplateMatchingFailureStages.Runtime;
        }

        if (code.StartsWith("MODEL_", StringComparison.Ordinal))
        {
            return TemplateMatchingFailureStages.Model;
        }

        return TemplateMatchingFailureStages.Match;
    }
}
