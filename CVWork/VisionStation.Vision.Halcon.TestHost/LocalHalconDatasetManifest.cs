using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionStation.Vision.Halcon.TestHost;

public enum LocalHalconDatasetLabel
{
    PositiveFront,
    Back,
    Similar,
    Partial,
    Boundary,
    Polarity
}

public sealed record LocalHalconDatasetRoi(
    double X,
    double Y,
    double Width,
    double Height);

public sealed record LocalHalconDatasetTemplate(
    string RelativeImagePath,
    string ImagePath,
    LocalHalconDatasetRoi Roi);

public sealed record LocalHalconDatasetCase(
    string Id,
    string RelativeImagePath,
    string ImagePath,
    LocalHalconDatasetLabel Label)
{
    public string CanonicalLabel => Label switch
    {
        LocalHalconDatasetLabel.PositiveFront => "positive/front",
        LocalHalconDatasetLabel.Back => "back",
        LocalHalconDatasetLabel.Similar => "similar",
        LocalHalconDatasetLabel.Partial => "partial",
        LocalHalconDatasetLabel.Boundary => "boundary",
        LocalHalconDatasetLabel.Polarity => "polarity",
        _ => throw new InvalidOperationException($"Unknown local HALCON dataset label '{Label}'.")
    };
}

public sealed class LocalHalconDatasetManifest
{
    public const string ManifestFileName = "halcon-dataset.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private LocalHalconDatasetManifest(
        int schemaVersion,
        string datasetRoot,
        LocalHalconDatasetTemplate template,
        IReadOnlyList<LocalHalconDatasetCase> cases)
    {
        SchemaVersion = schemaVersion;
        DatasetRoot = datasetRoot;
        Template = template;
        Cases = cases;
    }

    public int SchemaVersion { get; }

    public string DatasetRoot { get; }

    public LocalHalconDatasetTemplate Template { get; }

    public IReadOnlyList<LocalHalconDatasetCase> Cases { get; }

    public static LocalHalconDatasetManifest Load(string datasetRoot)
    {
        string fullRoot = NormalizeDatasetRoot(datasetRoot);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException(
                $"HALCON dataset directory does not exist: '{fullRoot}'.");
        }

        string manifestPath = Path.Combine(fullRoot, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                $"HALCON dataset manifest '{ManifestFileName}' does not exist in '{fullRoot}'.",
                manifestPath);
        }

        LocalHalconDatasetManifest manifest = Parse(
            fullRoot,
            File.ReadAllText(manifestPath));
        EnsureImageExists(manifest, manifest.Template.RelativeImagePath, manifest.Template.ImagePath);
        foreach (LocalHalconDatasetCase datasetCase in manifest.Cases)
        {
            EnsureImageExists(manifest, datasetCase.RelativeImagePath, datasetCase.ImagePath);
        }

        return manifest;
    }

    public static LocalHalconDatasetManifest Parse(string datasetRoot, string json)
    {
        string fullRoot = NormalizeDatasetRoot(datasetRoot);
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                });
            RejectDuplicateJsonProperties(document.RootElement, "$", new HashSet<string>(StringComparer.Ordinal));
            ManifestJson? source = document.RootElement.Deserialize<ManifestJson>(SerializerOptions);
            if (source is null)
            {
                throw Invalid("Manifest root must be a JSON object.");
            }

            return CreateValidated(fullRoot, source);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "HALCON dataset manifest contains invalid JSON or unknown fields.",
                exception);
        }
    }

    private static LocalHalconDatasetManifest CreateValidated(
        string datasetRoot,
        ManifestJson source)
    {
        if (source.SchemaVersion != 1)
        {
            throw Invalid("Property 'schemaVersion' must be the integer 1.");
        }

        if (source.Template is null)
        {
            throw Invalid("Property 'template' is required.");
        }

        ResolvedImagePath templateImage = ResolveImagePath(
            datasetRoot,
            source.Template.Image,
            "template.image");
        LocalHalconDatasetRoi templateRoi = ValidateRoi(source.Template.Roi);
        var template = new LocalHalconDatasetTemplate(
            templateImage.RelativePath,
            templateImage.FullPath,
            templateRoi);

        if (source.Cases is null || source.Cases.Count == 0)
        {
            throw Invalid("Property 'cases' must contain at least one positive and one negative case.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var imagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cases = new List<LocalHalconDatasetCase>(source.Cases.Count);
        foreach (CaseJson? sourceCase in source.Cases)
        {
            if (sourceCase is null)
            {
                throw Invalid("Property 'cases' cannot contain null items.");
            }

            string id = ValidateCaseId(sourceCase.Id);
            if (!ids.Add(id))
            {
                throw Invalid($"Duplicate HALCON dataset case id '{id}'.");
            }

            ResolvedImagePath image = ResolveImagePath(
                datasetRoot,
                sourceCase.Image,
                $"cases['{id}'].image");
            if (string.Equals(
                    image.FullPath,
                    templateImage.FullPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid(
                    $"HALCON dataset case '{id}' must not reuse the learning template image.");
            }

            if (!imagePaths.Add(image.FullPath))
            {
                throw Invalid($"Duplicate HALCON dataset case image '{image.RelativePath}'.");
            }

            LocalHalconDatasetLabel label = ParseLabel(sourceCase.Label, id);
            cases.Add(new LocalHalconDatasetCase(
                id,
                image.RelativePath,
                image.FullPath,
                label));
        }

        if (!cases.Any(item => item.Label == LocalHalconDatasetLabel.PositiveFront))
        {
            throw Invalid("HALCON dataset must contain at least one 'positive/front' case.");
        }

        if (!cases.Any(item => item.Label != LocalHalconDatasetLabel.PositiveFront))
        {
            throw Invalid("HALCON dataset must contain at least one negative case.");
        }

        return new LocalHalconDatasetManifest(
            1,
            datasetRoot,
            template,
            cases.ToArray());
    }

    private static LocalHalconDatasetRoi ValidateRoi(RoiJson? source)
    {
        if (source?.X is null ||
            source.Y is null ||
            source.Width is null ||
            source.Height is null ||
            !double.IsFinite(source.X.Value) ||
            !double.IsFinite(source.Y.Value) ||
            !double.IsFinite(source.Width.Value) ||
            !double.IsFinite(source.Height.Value) ||
            source.X.Value < 0 ||
            source.Y.Value < 0 ||
            source.Width.Value <= 0 ||
            source.Height.Value <= 0)
        {
            throw Invalid(
                "Property 'template.roi' requires finite x/y >= 0 and finite width/height > 0.");
        }

        return new LocalHalconDatasetRoi(
            source.X.Value,
            source.Y.Value,
            source.Width.Value,
            source.Height.Value);
    }

    private static string ValidateCaseId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Length > 128 ||
            value.Any(char.IsControl))
        {
            throw Invalid(
                "Every HALCON dataset case requires a trimmed id of at most 128 characters.");
        }

        return value;
    }

    private static LocalHalconDatasetLabel ParseLabel(string? value, string caseId)
    {
        return value switch
        {
            "positive/front" => LocalHalconDatasetLabel.PositiveFront,
            "back" => LocalHalconDatasetLabel.Back,
            "similar" => LocalHalconDatasetLabel.Similar,
            "partial" => LocalHalconDatasetLabel.Partial,
            "boundary" => LocalHalconDatasetLabel.Boundary,
            "polarity" => LocalHalconDatasetLabel.Polarity,
            _ => throw Invalid(
                $"HALCON dataset case '{caseId}' has unknown label '{value ?? "<null>"}'.")
        };
    }

    private static ResolvedImagePath ResolveImagePath(
        string datasetRoot,
        string? value,
        string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw Invalid($"Property '{propertyName}' must be a non-empty relative image path.");
        }

        string portablePath = value.Replace('\\', '/');
        if (Path.IsPathRooted(value) ||
            portablePath.StartsWith("/", StringComparison.Ordinal) ||
            IsDriveQualified(portablePath))
        {
            throw Invalid($"Property '{propertyName}' must be relative to the dataset root.");
        }

        string[] segments = portablePath.Split('/');
        if (segments.Length == 0 ||
            segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) ||
                string.Equals(segment, ".", StringComparison.Ordinal) ||
                string.Equals(segment, "..", StringComparison.Ordinal) ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw Invalid($"Property '{propertyName}' contains an unsafe image path segment.");
        }

        string normalizedRelativePath = string.Join('/', segments);
        string fullPath = Path.GetFullPath(Path.Combine(
            datasetRoot,
            normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsInsideDatasetRoot(datasetRoot, fullPath))
        {
            throw Invalid($"Property '{propertyName}' escapes the dataset root.");
        }

        return new ResolvedImagePath(normalizedRelativePath, fullPath);
    }

    private static bool IsDriveQualified(string value)
    {
        return value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':';
    }

    private static string NormalizeDatasetRoot(string datasetRoot)
    {
        if (string.IsNullOrWhiteSpace(datasetRoot))
        {
            throw new ArgumentException("HALCON dataset root cannot be empty.", nameof(datasetRoot));
        }

        return Path.GetFullPath(datasetRoot);
    }

    private static bool IsInsideDatasetRoot(string datasetRoot, string candidate)
    {
        string requiredPrefix = datasetRoot.TrimEnd(
                                    Path.DirectorySeparatorChar,
                                    Path.AltDirectorySeparatorChar) +
                                Path.DirectorySeparatorChar;
        return candidate.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureImageExists(
        LocalHalconDatasetManifest manifest,
        string relativePath,
        string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"HALCON dataset image '{relativePath}' does not exist.",
                fullPath);
        }

        string current = manifest.DatasetRoot;
        foreach (string segment in relativePath.Split('/'))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                continue;
            }

            FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null ||
                !IsInsideDatasetRoot(manifest.DatasetRoot, Path.GetFullPath(target.FullName)))
            {
                throw Invalid(
                    $"HALCON dataset image '{relativePath}' escapes the dataset root through a link.");
            }
        }
    }

    private static void RejectDuplicateJsonProperties(
        JsonElement element,
        string path,
        HashSet<string> propertyNames)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                propertyNames.Clear();
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!propertyNames.Add(property.Name))
                    {
                        throw Invalid($"Duplicate JSON property '{property.Name}' at '{path}'.");
                    }

                    RejectDuplicateJsonProperties(
                        property.Value,
                        $"{path}.{property.Name}",
                        new HashSet<string>(StringComparer.Ordinal));
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    RejectDuplicateJsonProperties(
                        item,
                        $"{path}[{index}]",
                        new HashSet<string>(StringComparer.Ordinal));
                    index++;
                }

                break;
        }
    }

    private static InvalidDataException Invalid(string message)
    {
        return new InvalidDataException(message);
    }

    private sealed record ResolvedImagePath(string RelativePath, string FullPath);

    private sealed class ManifestJson
    {
        [JsonPropertyName("schemaVersion")]
        public int? SchemaVersion { get; init; }

        [JsonPropertyName("template")]
        public TemplateJson? Template { get; init; }

        [JsonPropertyName("cases")]
        public List<CaseJson?>? Cases { get; init; }
    }

    private sealed class TemplateJson
    {
        [JsonPropertyName("image")]
        public string? Image { get; init; }

        [JsonPropertyName("roi")]
        public RoiJson? Roi { get; init; }
    }

    private sealed class RoiJson
    {
        [JsonPropertyName("x")]
        public double? X { get; init; }

        [JsonPropertyName("y")]
        public double? Y { get; init; }

        [JsonPropertyName("width")]
        public double? Width { get; init; }

        [JsonPropertyName("height")]
        public double? Height { get; init; }
    }

    private sealed class CaseJson
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("image")]
        public string? Image { get; init; }

        [JsonPropertyName("label")]
        public string? Label { get; init; }
    }
}
