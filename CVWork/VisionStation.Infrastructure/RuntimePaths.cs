namespace VisionStation.Infrastructure;

public sealed class RuntimePaths
{
    public const string DefaultBaseDirectory = @"D:\CVConfig";

    public RuntimePaths()
        : this(DefaultBaseDirectory)
    {
    }

    public RuntimePaths(string baseDirectory)
    {
        BaseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? DefaultBaseDirectory
            : Path.GetFullPath(baseDirectory);
        ConfigDirectory = Path.Combine(BaseDirectory, "Config");
        RecipeDirectory = Path.Combine(BaseDirectory, "Recipes");
        CurrentRecipePath = Path.Combine(RecipeDirectory, "current.recipe");
        ImageDirectory = Path.Combine(BaseDirectory, "Images");
        ImageTraceDirectory = Path.Combine(ImageDirectory, "InspectionTraces");
        ManualImageExportDirectory = Path.Combine(ImageDirectory, "ManualExports");
        ResourceDirectory = Path.Combine(BaseDirectory, "Resources");
        TemplateResourceDirectory = Path.Combine(ResourceDirectory, "Templates");
        LogDirectory = Path.Combine(BaseDirectory, "Logs");
        CrashDirectory = Path.Combine(BaseDirectory, "Crashes");
        DataDirectory = Path.Combine(BaseDirectory, "Data");
        DatabaseDirectory = Path.Combine(DataDirectory, "Database");
        DatabasePath = Path.Combine(DatabaseDirectory, "visionstation.db");
        DeviceConfigPath = Path.Combine(ConfigDirectory, "devices.json");

        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(RecipeDirectory);
        Directory.CreateDirectory(ImageDirectory);
        Directory.CreateDirectory(ImageTraceDirectory);
        Directory.CreateDirectory(ManualImageExportDirectory);
        Directory.CreateDirectory(ResourceDirectory);
        Directory.CreateDirectory(TemplateResourceDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(CrashDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(DatabaseDirectory);
    }

    public static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "_";
        }

        var sanitized = string.Join("_", value.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "_" : sanitized;
    }

    public string BaseDirectory { get; }

    public string ConfigDirectory { get; }

    public string RecipeDirectory { get; }

    public string CurrentRecipePath { get; }

    public string ImageDirectory { get; }

    public string ImageTraceDirectory { get; }

    public string ManualImageExportDirectory { get; }

    public string ResourceDirectory { get; }

    public string TemplateResourceDirectory { get; }

    public string LogDirectory { get; }

    public string CrashDirectory { get; }

    public string DataDirectory { get; }

    public string DatabaseDirectory { get; }

    public string DatabasePath { get; }

    public string DeviceConfigPath { get; }
}
