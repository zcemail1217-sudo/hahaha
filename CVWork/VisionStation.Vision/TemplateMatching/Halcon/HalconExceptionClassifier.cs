using System.Globalization;
using HalconDotNet;

namespace VisionStation.Vision;

internal static class HalconExceptionClassifier
{
    private const int MatchingTimeoutErrorCode = 9400;
    private const int FirstLicenseErrorCode = 2003;
    private const int LastLicenseErrorCode = 2384;

    public static bool TryClassify(
        Exception exception,
        string? operation,
        out TemplateMatchingDiagnostic? diagnostic)
    {
        string code;
        int? halconErrorCode = null;

        if (exception is DllNotFoundException)
        {
            code = TemplateMatchingDiagnosticCodes.RuntimeNotFound;
        }
        else if (exception is BadImageFormatException)
        {
            code = TemplateMatchingDiagnosticCodes.RuntimeArchMismatch;
        }
        else if (exception is EntryPointNotFoundException)
        {
            code = TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch;
        }
        else if (exception is HalconException halconException)
        {
            halconErrorCode = halconException.GetErrorCode();
            code = halconErrorCode switch
            {
                MatchingTimeoutErrorCode => TemplateMatchingDiagnosticCodes.MatchTimeout,
                >= FirstLicenseErrorCode and <= LastLicenseErrorCode =>
                    TemplateMatchingDiagnosticCodes.LicenseUnavailable,
                _ => TemplateMatchingDiagnosticCodes.MatchOperatorFailed
            };
        }
        else
        {
            diagnostic = null;
            return false;
        }

        diagnostic = TemplateMatchingDiagnostics.Create(
            code,
            CreateTechnicalDetails(exception, operation, halconErrorCode));
        return true;
    }

    private static string CreateTechnicalDetails(
        Exception exception,
        string? operation,
        int? halconErrorCode)
    {
        string normalizedOperation = string.IsNullOrWhiteSpace(operation)
            ? "Unknown"
            : operation.Trim();
        string details = $"Operation={normalizedOperation}; ExceptionType={exception.GetType().Name}";

        return halconErrorCode.HasValue
            ? $"{details}; ErrorCode={halconErrorCode.Value.ToString(CultureInfo.InvariantCulture)}"
            : details;
    }
}
