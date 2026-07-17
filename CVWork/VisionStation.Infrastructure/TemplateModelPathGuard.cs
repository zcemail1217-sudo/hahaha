using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VisionStation.Vision;

namespace VisionStation.Infrastructure;

internal sealed class TemplateModelPathGuard
{
    private static readonly char[] PathSeparators = ['/', '\\'];
    private static readonly Regex UnsafeWindowsSuffix = new(@"[ .]$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _rootDirectory;

    public TemplateModelPathGuard(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("Template resource root is required.", nameof(rootDirectory));
        }

        _rootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
    }

    public string RootDirectory => _rootDirectory;

    public string CreateOwnerDirectory(TemplateModelOwner owner)
    {
        var segments = GetOwnerSegments(owner);
        try
        {
            ValidateRootAncestors();
            Directory.CreateDirectory(_rootDirectory);
            ValidateRootAncestors();
            EnsureNotReparsePoint(_rootDirectory);
            var current = _rootDirectory;
            foreach (var segment in segments)
            {
                EnsureNotReparsePoint(current);
                current = Path.Combine(current, segment);
                Directory.CreateDirectory(current);
                EnsureNotReparsePoint(current);
            }

            EnsureContained(current);
            return current;
        }
        catch (TemplateModelStoreException)
        {
            throw;
        }
        catch (Exception exception) when (IsPathValidationException(exception))
        {
            throw InvalidPath("Unable to create the controlled template owner directory.", exception);
        }
    }

    public string GetOwnerDirectory(TemplateModelOwner owner, bool requireExisting)
    {
        var segments = GetOwnerSegments(owner);
        var directory = Path.Combine([_rootDirectory, .. segments]);
        EnsureContained(directory);
        ValidateExistingChain(directory);
        if (requireExisting && !Directory.Exists(directory))
        {
            throw new TemplateModelStoreException(
                TemplateMatchingDiagnosticCodes.ModelNotFound,
                "The controlled template owner directory does not exist.");
        }

        return directory;
    }

    public string GetRelativeModelPath(TemplateModelOwner owner, string generation)
    {
        ValidateGeneration(generation);
        return BuildRelativePath(owner, $"model-{generation}.shm");
    }

    public string GetRelativeMetadataPath(TemplateModelOwner owner, string generation)
    {
        ValidateGeneration(generation);
        return BuildRelativePath(owner, $"model-{generation}.json");
    }

    public string ResolveModelPath(
        TemplateModelOwner owner,
        string relativePath,
        string generation,
        bool requireExisting)
    {
        return ResolveOwnedPath(owner, relativePath, $"model-{generation}.shm", requireExisting);
    }

    public string ResolveMetadataPath(
        TemplateModelOwner owner,
        string relativePath,
        string generation,
        bool requireExisting)
    {
        return ResolveOwnedPath(owner, relativePath, $"model-{generation}.json", requireExisting);
    }

    public void ValidateStoreIssuedStagingPath(
        TemplateModelOwner owner,
        string ownerDirectory,
        string stagingPath,
        string expectedSuffix,
        bool requireExisting)
    {
        var expectedOwnerDirectory = GetOwnerDirectory(owner, requireExisting: true);
        if (!string.Equals(
                Path.GetFullPath(ownerDirectory),
                expectedOwnerDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidPath("The write session owner directory is not controlled by this store.");
        }

        var fullPath = Path.GetFullPath(stagingPath);
        EnsureContained(fullPath);
        if (!string.Equals(Path.GetDirectoryName(fullPath), expectedOwnerDirectory, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullPath).EndsWith(expectedSuffix, StringComparison.Ordinal))
        {
            throw InvalidPath("The write session staging path is outside its controlled owner directory.");
        }

        ValidateExistingChain(fullPath);
        if (requireExisting && !File.Exists(fullPath))
        {
            throw new TemplateModelStoreException(
                TemplateMatchingDiagnosticCodes.ModelNotFound,
                "The staging model file does not exist.");
        }
    }

    public void ValidateExistingFile(string path)
    {
        EnsureContained(path);
        ValidateExistingChain(path);
        if (!File.Exists(path))
        {
            throw new TemplateModelStoreException(
                TemplateMatchingDiagnosticCodes.ModelNotFound,
                "A required template generation file does not exist.");
        }
    }

    public void DeleteEmptyOwnerHierarchy(TemplateModelOwner owner)
    {
        var ownerDirectory = GetOwnerDirectory(owner, requireExisting: false);
        var flowDirectory = Path.GetDirectoryName(ownerDirectory);
        var recipeDirectory = flowDirectory is null ? null : Path.GetDirectoryName(flowDirectory);
        foreach (var directory in new[] { ownerDirectory, flowDirectory, recipeDirectory })
        {
            if (directory is null || !Directory.Exists(directory))
            {
                continue;
            }

            EnsureContained(directory);
            ValidateExistingChain(directory);
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
    }

    public static string CreateOwnerSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw InvalidPath("Template model owner identifiers cannot be empty.");
        }

        var readable = Regex.Replace(
                value.Trim().ToLowerInvariant(),
                "[^a-z0-9-]+",
                "-",
                RegexOptions.CultureInvariant)
            .Trim('-');
        if (readable.Length == 0)
        {
            readable = "item";
        }

        if (readable.Length > 48)
        {
            readable = readable[..48].TrimEnd('-');
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant()[..12];
        return $"{readable}-{hash}";
    }

    public static void ValidateGeneration(string generation)
    {
        if (string.IsNullOrWhiteSpace(generation) ||
            generation.Length > 96 ||
            generation.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw InvalidPath("Template model generation is invalid.");
        }
    }

    private string ResolveOwnedPath(
        TemplateModelOwner owner,
        string relativePath,
        string expectedFileName,
        bool requireExisting)
    {
        try
        {
            var segments = ValidateRelativeSegments(relativePath);
            var ownerSegments = GetOwnerSegments(owner);
            if (segments.Length != 4 ||
                !segments.Take(3).SequenceEqual(ownerSegments, StringComparer.OrdinalIgnoreCase) ||
                !string.Equals(segments[3], expectedFileName, StringComparison.OrdinalIgnoreCase))
            {
                throw InvalidPath("Template model path does not match its owner and generation.");
            }

            var fullPath = Path.GetFullPath(Path.Combine([_rootDirectory, .. segments]));
            EnsureContained(fullPath);
            ValidateExistingChain(fullPath);
            if (requireExisting && !File.Exists(fullPath))
            {
                throw new TemplateModelStoreException(
                    TemplateMatchingDiagnosticCodes.ModelNotFound,
                    "A required template generation file does not exist.");
            }

            return fullPath;
        }
        catch (TemplateModelStoreException)
        {
            throw;
        }
        catch (Exception exception) when (IsPathValidationException(exception))
        {
            throw InvalidPath("Template model path validation failed.", exception);
        }
    }

    private string BuildRelativePath(TemplateModelOwner owner, string fileName)
    {
        var segments = GetOwnerSegments(owner);
        return string.Join('/', [.. segments, fileName]);
    }

    private static string[] ValidateRelativeSegments(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            relativePath.IndexOf('\0') >= 0 ||
            relativePath.Contains(':') ||
            relativePath.StartsWith("\\\\", StringComparison.Ordinal) ||
            relativePath.StartsWith('/') ||
            relativePath.StartsWith('\\') ||
            Path.IsPathRooted(relativePath) ||
            Path.IsPathFullyQualified(relativePath))
        {
            throw InvalidPath("Only controlled relative template model paths are allowed.");
        }

        var segments = relativePath.Split(PathSeparators, StringSplitOptions.None);
        if (segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".." ||
                UnsafeWindowsSuffix.IsMatch(segment) ||
                char.IsWhiteSpace(segment[^1])))
        {
            throw InvalidPath("Template model path contains an unsafe segment.");
        }

        return segments;
    }

    private string[] GetOwnerSegments(TemplateModelOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return
        [
            CreateOwnerSegment(owner.RecipeId),
            CreateOwnerSegment(owner.FlowId),
            CreateOwnerSegment(owner.ToolId)
        ];
    }

    private void EnsureContained(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(_rootDirectory, fullPath);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.OrdinalIgnoreCase) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidPath("Template model path escapes the controlled resource root.");
        }
    }

    private void ValidateExistingChain(string path)
    {
        ValidateRootAncestors();
        if (!Directory.Exists(_rootDirectory))
        {
            return;
        }

        EnsureNotReparsePoint(_rootDirectory);
        var relative = Path.GetRelativePath(_rootDirectory, Path.GetFullPath(path));
        if (relative == ".")
        {
            return;
        }

        var current = _rootDirectory;
        foreach (var segment in relative.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                break;
            }

            EnsureNotReparsePoint(current);
        }
    }

    private void ValidateRootAncestors()
    {
        DirectoryInfo? current = new(_rootDirectory);
        while (current is not null)
        {
            if (current.Exists)
            {
                EnsureNotReparsePoint(current.FullName);
            }

            current = current.Parent;
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw InvalidPath("Template model paths cannot traverse a reparse point.");
        }
    }

    private static bool IsPathValidationException(Exception exception)
    {
        return exception is ArgumentException or NotSupportedException or PathTooLongException;
    }

    private static TemplateModelStoreException InvalidPath(string detail, Exception? innerException = null)
    {
        return new TemplateModelStoreException(
            TemplateMatchingDiagnosticCodes.ModelPathInvalid,
            detail,
            innerException);
    }
}
