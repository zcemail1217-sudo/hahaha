using VisionStation.Vision.Halcon.TestHost;
using Xunit;

namespace VisionStation.Vision.Halcon.Tests;

public sealed class LocalHalconDatasetManifestTests
{
    [Fact]
    public void Parse_MapsEveryAllowedLabelAndResolvesImagesInsideDatasetRoot()
    {
        string datasetRoot = CreateDatasetRoot();

        LocalHalconDatasetManifest manifest = LocalHalconDatasetManifest.Parse(
            datasetRoot,
            CreateManifestJson(
                """
                { "id": "positive-canonical", "image": "images/front-01.bmp", "label": "positive/front" },
                { "id": "back-01", "image": "images/back-01.jpeg", "label": "back" },
                { "id": "similar-01", "image": "images/similar-01.tif", "label": "similar" },
                { "id": "partial-01", "image": "images/partial-01.tiff", "label": "partial" },
                { "id": "boundary-01", "image": "images/boundary-01.bmp", "label": "boundary" },
                { "id": "polarity-01", "image": "images/polarity-01.bmp", "label": "polarity" }
                """));

        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal(Path.GetFullPath(datasetRoot), manifest.DatasetRoot);
        Assert.Equal("template/reference.bmp", manifest.Template.RelativeImagePath);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(datasetRoot), "template", "reference.bmp"),
            manifest.Template.ImagePath);
        Assert.Equal(new LocalHalconDatasetRoi(10, 20, 120, 240), manifest.Template.Roi);
        Assert.Equal(6, manifest.Cases.Count);
        Assert.Equal(LocalHalconDatasetLabel.PositiveFront, manifest.Cases[0].Label);
        Assert.Equal(
            [
                "positive/front",
                "back",
                "similar",
                "partial",
                "boundary",
                "polarity"
            ],
            manifest.Cases.Select(item => item.CanonicalLabel));
        Assert.Equal(
            Path.Combine(Path.GetFullPath(datasetRoot), "images", "polarity-01.bmp"),
            manifest.Cases[^1].ImagePath);
    }

    [Fact]
    public void Parse_RejectsUnknownFieldsAtEveryManifestLevel()
    {
        string datasetRoot = CreateDatasetRoot();
        string json = """
                      {
                        "schemaVersion": 1,
                        "template": {
                          "image": "template/reference.bmp",
                          "roi": { "x": 10, "y": 20, "width": 120, "height": 240 },
                          "unexpected": true
                        },
                        "cases": [
                          {
                            "id": "front-01",
                            "image": "images/front-01.bmp",
                            "label": "positive/front",
                            "threshold": 0.5
                          }
                        ],
                        "owner": "not-allowed"
                      }
                      """;

        InvalidDataException failure = Assert.Throws<InvalidDataException>(
            () => LocalHalconDatasetManifest.Parse(datasetRoot, json));

        Assert.Contains("unknown", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsDuplicateJsonProperties()
    {
        string datasetRoot = CreateDatasetRoot();
        string json = """
                      {
                        "schemaVersion": 1,
                        "schemaVersion": 1,
                        "template": {
                          "image": "template/reference.bmp",
                          "roi": { "x": 10, "y": 20, "width": 120, "height": 240 }
                        },
                        "cases": [
                          { "id": "front-01", "image": "images/front-01.bmp", "label": "positive/front" }
                        ]
                      }
                      """;

        InvalidDataException failure = Assert.Throws<InvalidDataException>(
            () => LocalHalconDatasetManifest.Parse(datasetRoot, json));

        Assert.Contains("duplicate", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("Positive/Front")]
    [InlineData("positive")]
    [InlineData("front")]
    [InlineData("")]
    [InlineData("negative")]
    public void Parse_RejectsUnknownOrNonCanonicalCaseLabels(string label)
    {
        string datasetRoot = CreateDatasetRoot();
        string json = CreateManifestJson(
            $$"""
              { "id": "case-01", "image": "images/case-01.bmp", "label": "{{label}}" }
              """);

        InvalidDataException failure = Assert.Throws<InvalidDataException>(
            () => LocalHalconDatasetManifest.Parse(datasetRoot, json));

        Assert.Contains("label", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../outside.bmp")]
    [InlineData("images/../../outside.bmp")]
    [InlineData("/absolute.bmp")]
    [InlineData("C:/absolute.bmp")]
    [InlineData("images/")]
    public void Parse_RejectsImagePathsThatAreNotSafeRelativeFiles(string image)
    {
        string datasetRoot = CreateDatasetRoot();
        string json = CreateManifestJson(
            $$"""
              { "id": "front-01", "image": "{{image}}", "label": "positive/front" }
              """);

        InvalidDataException failure = Assert.Throws<InvalidDataException>(
            () => LocalHalconDatasetManifest.Parse(datasetRoot, json));

        Assert.Contains("image", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsTemplateImageThatEscapesDatasetRoot()
    {
        string datasetRoot = CreateDatasetRoot();
        string json = CreateManifestJson(
            """
            { "id": "front-01", "image": "images/front-01.bmp", "label": "positive/front" }
            """).Replace(
                "template/reference.bmp",
                "../reference.bmp",
                StringComparison.Ordinal);

        InvalidDataException failure = Assert.Throws<InvalidDataException>(
            () => LocalHalconDatasetManifest.Parse(datasetRoot, json));

        Assert.Contains("template.image", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsDuplicateCaseIds()
    {
        string datasetRoot = CreateDatasetRoot();
        string json = CreateManifestJson(
            """
            { "id": "same", "image": "images/front-01.bmp", "label": "positive/front" },
            { "id": "same", "image": "images/back-01.bmp", "label": "back" }
            """);

        InvalidDataException failure = Assert.Throws<InvalidDataException>(
            () => LocalHalconDatasetManifest.Parse(datasetRoot, json));

        Assert.Contains("duplicate", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("id", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsDuplicateNormalizedCaseImagePaths()
    {
        string datasetRoot = CreateDatasetRoot();
        string json = CreateManifestJson(
            """
            { "id": "front-01", "image": "images/front.bmp", "label": "positive/front" },
            { "id": "front-02", "image": "images\\front.bmp", "label": "positive/front" }
            """);

        InvalidDataException failure = Assert.Throws<InvalidDataException>(
            () => LocalHalconDatasetManifest.Parse(datasetRoot, json));

        Assert.Contains("duplicate", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("image", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsCaseImageThatReusesTheLearningTemplate()
    {
        string datasetRoot = CreateDatasetRoot();
        string json = CreateManifestJson(
            """
            { "id": "front-template", "image": "template/reference.bmp", "label": "positive/front" },
            { "id": "back-01", "image": "images/back.bmp", "label": "back" }
            """);

        InvalidDataException failure = Assert.Throws<InvalidDataException>(
            () => LocalHalconDatasetManifest.Parse(datasetRoot, json));

        Assert.Contains("case", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("template", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-1, 20, 120, 240)]
    [InlineData(10, -1, 120, 240)]
    [InlineData(10, 20, 0, 240)]
    [InlineData(10, 20, 120, 0)]
    public void Parse_RejectsInvalidTemplateRoi(
        double x,
        double y,
        double width,
        double height)
    {
        string datasetRoot = CreateDatasetRoot();
        string json = CreateManifestJson(
            """
            { "id": "front-01", "image": "images/front.bmp", "label": "positive/front" }
            """)
            .Replace("\"x\": 10", $"\"x\": {x}", StringComparison.Ordinal)
            .Replace("\"y\": 20", $"\"y\": {y}", StringComparison.Ordinal)
            .Replace("\"width\": 120", $"\"width\": {width}", StringComparison.Ordinal)
            .Replace("\"height\": 240", $"\"height\": {height}", StringComparison.Ordinal);

        InvalidDataException failure = Assert.Throws<InvalidDataException>(
            () => LocalHalconDatasetManifest.Parse(datasetRoot, json));

        Assert.Contains("roi", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RequiresAtLeastOnePositiveFrontAndOneNegativeCase()
    {
        string datasetRoot = CreateDatasetRoot();
        string onlyPositive = CreateManifestJson(
            """
            { "id": "front-01", "image": "images/front.bmp", "label": "positive/front" }
            """);
        string onlyNegative = CreateManifestJson(
            """
            { "id": "back-01", "image": "images/back.bmp", "label": "back" }
            """);

        InvalidDataException positiveFailure = Assert.Throws<InvalidDataException>(
            () => LocalHalconDatasetManifest.Parse(datasetRoot, onlyPositive));
        InvalidDataException negativeFailure = Assert.Throws<InvalidDataException>(
            () => LocalHalconDatasetManifest.Parse(datasetRoot, onlyNegative));

        Assert.Contains("negative", positiveFailure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("positive/front", negativeFailure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_RequiresDatasetDirectoryAndManifestFile()
    {
        string missingRoot = Path.Combine(
            Path.GetTempPath(),
            "VisionStation-Manifest-Load-Tests",
            Guid.NewGuid().ToString("N"));
        string emptyRoot = CreateOwnedDirectory();
        try
        {
            Assert.Throws<DirectoryNotFoundException>(
                () => LocalHalconDatasetManifest.Load(missingRoot));
            Assert.Throws<FileNotFoundException>(
                () => LocalHalconDatasetManifest.Load(emptyRoot));
        }
        finally
        {
            DeleteOwnedDirectory(emptyRoot);
        }
    }

    [Fact]
    public void Load_RequiresEveryReferencedImageToExist()
    {
        string datasetRoot = CreateOwnedDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(datasetRoot, LocalHalconDatasetManifest.ManifestFileName),
                CreateManifestJson(
                    """
                    { "id": "front-01", "image": "images/front.bmp", "label": "positive/front" },
                    { "id": "back-01", "image": "images/back.bmp", "label": "back" }
                    """));

            FileNotFoundException failure = Assert.Throws<FileNotFoundException>(
                () => LocalHalconDatasetManifest.Load(datasetRoot));

            Assert.Contains("template/reference.bmp", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteOwnedDirectory(datasetRoot);
        }
    }

    [Fact]
    public void Load_ReturnsManifestWhenAllReferencedFilesExist()
    {
        string datasetRoot = CreateOwnedDirectory();
        try
        {
            string json = CreateManifestJson(
                """
                { "id": "front-01", "image": "images/front.bmp", "label": "positive/front" },
                { "id": "back-01", "image": "images/back.bmp", "label": "back" }
                """);
            File.WriteAllText(
                Path.Combine(datasetRoot, LocalHalconDatasetManifest.ManifestFileName),
                json);
            CreateEmptyFile(datasetRoot, "template/reference.bmp");
            CreateEmptyFile(datasetRoot, "images/front.bmp");
            CreateEmptyFile(datasetRoot, "images/back.bmp");

            LocalHalconDatasetManifest manifest = LocalHalconDatasetManifest.Load(datasetRoot);

            Assert.Equal(2, manifest.Cases.Count);
            Assert.All(
                manifest.Cases.Prepend(
                    new LocalHalconDatasetCase(
                        "template",
                        manifest.Template.RelativeImagePath,
                        manifest.Template.ImagePath,
                        LocalHalconDatasetLabel.PositiveFront)),
                item => Assert.True(File.Exists(item.ImagePath), item.RelativeImagePath));
        }
        finally
        {
            DeleteOwnedDirectory(datasetRoot);
        }
    }

    private static string CreateDatasetRoot()
    {
        return Path.Combine(Path.GetTempPath(), "VisionStation-Manifest-Pure-Tests");
    }

    private static string CreateOwnedDirectory()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "VisionStation-Manifest-Load-Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void CreateEmptyFile(string datasetRoot, string relativePath)
    {
        string path = Path.Combine(
            datasetRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
    }

    private static void DeleteOwnedDirectory(string path)
    {
        string ownedRoot = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "VisionStation-Manifest-Load-Tests"));
        string fullPath = Path.GetFullPath(path);
        string requiredPrefix = ownedRoot.TrimEnd(Path.DirectorySeparatorChar) +
                                Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to delete non-owned test path '{fullPath}'.");
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }

    private static string CreateManifestJson(string cases)
    {
        return $$"""
                 {
                   "schemaVersion": 1,
                   "template": {
                     "image": "template/reference.bmp",
                     "roi": { "x": 10, "y": 20, "width": 120, "height": 240 }
                   },
                   "cases": [
                     {{cases}}
                   ]
                 }
                 """;
    }
}

[Collection(LocalHalconDatasetCollection.Name)]
public sealed class LocalHalconDatasetFactAttributeTests
{
    private const string DatasetEnvironmentVariable = "VISIONSTATION_HALCON_DATASET";

    [Fact]
    public void OnlyAnUnsetDatasetEnvironmentVariableSkipsTheAcceptanceTest()
    {
        string? originalValue = Environment.GetEnvironmentVariable(DatasetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(DatasetEnvironmentVariable, null);
            Assert.NotNull(new LocalHalconDatasetFactAttribute().Skip);

            Environment.SetEnvironmentVariable(DatasetEnvironmentVariable, "   ");
            Assert.Null(new LocalHalconDatasetFactAttribute().Skip);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DatasetEnvironmentVariable, originalValue);
        }
    }
}
