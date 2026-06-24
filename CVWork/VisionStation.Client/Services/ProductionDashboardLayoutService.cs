using System.IO;
using System.Text.Json;
using VisionStation.Infrastructure;

namespace VisionStation.Client.Services;

public sealed class ProductionDashboardLayoutService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly RuntimePaths _paths;

    public ProductionDashboardLayoutService(RuntimePaths paths)
    {
        _paths = paths;
    }

    public event EventHandler? LayoutChanged;

    private string LayoutPath => Path.Combine(_paths.ConfigDirectory, "production-dashboard-layout.json");

    public ProductionDashboardRecipeLayout LoadRecipeLayout(string recipeId)
    {
        if (string.IsNullOrWhiteSpace(recipeId))
        {
            return new ProductionDashboardRecipeLayout();
        }

        var layout = LoadLayoutFile();
        return layout.Recipes.TryGetValue(recipeId.Trim(), out var recipeLayout)
            ? recipeLayout.Clone()
            : new ProductionDashboardRecipeLayout();
    }

    public void SaveRecipeLayout(string recipeId, ProductionDashboardRecipeLayout recipeLayout)
    {
        if (string.IsNullOrWhiteSpace(recipeId))
        {
            return;
        }

        var layout = LoadLayoutFile();
        layout.Recipes[recipeId.Trim()] = recipeLayout.Clone();

        Directory.CreateDirectory(_paths.ConfigDirectory);
        File.WriteAllText(LayoutPath, JsonSerializer.Serialize(layout, JsonOptions));
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private ProductionDashboardLayoutFile LoadLayoutFile()
    {
        if (!File.Exists(LayoutPath))
        {
            return new ProductionDashboardLayoutFile();
        }

        try
        {
            using var stream = File.OpenRead(LayoutPath);
            return JsonSerializer.Deserialize<ProductionDashboardLayoutFile>(stream, JsonOptions)
                   ?? new ProductionDashboardLayoutFile();
        }
        catch
        {
            return new ProductionDashboardLayoutFile();
        }
    }
}

public sealed class ProductionDashboardLayoutFile
{
    public int Version { get; set; } = 1;

    public Dictionary<string, ProductionDashboardRecipeLayout> Recipes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ProductionDashboardRecipeLayout
{
    public List<ProductionDashboardPaneLayout> Panes { get; set; } = [];

    public ProductionDashboardRecipeLayout Clone()
    {
        return new ProductionDashboardRecipeLayout
        {
            Panes = Panes
                .Select(pane => new ProductionDashboardPaneLayout { FlowId = pane.FlowId })
                .ToList()
        };
    }
}

public sealed class ProductionDashboardPaneLayout
{
    public string FlowId { get; set; } = string.Empty;
}
