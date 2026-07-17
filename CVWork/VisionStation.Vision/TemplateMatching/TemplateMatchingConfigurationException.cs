namespace VisionStation.Vision;

public sealed class TemplateMatchingConfigurationException : Exception
{
    public TemplateMatchingConfigurationException(string code)
        : this(TemplateMatchingDiagnostics.Create(code))
    {
    }

    public TemplateMatchingConfigurationException(string code, string message)
        : this(code, message, null)
    {
    }

    public TemplateMatchingConfigurationException(
        string code,
        string message,
        string? technicalDetails)
        : base(message)
    {
        Code = code;
        FailureStage = TemplateMatchingDiagnostics.Create(code).FailureStage;
        TechnicalDetails = technicalDetails;
    }

    public TemplateMatchingConfigurationException(TemplateMatchingDiagnostic diagnostic)
        : this(
            diagnostic.Code,
            diagnostic.UserMessage,
            diagnostic.TechnicalDetails)
    {
    }

    public string Code { get; }

    public string FailureStage { get; }

    public string? TechnicalDetails { get; }
}
