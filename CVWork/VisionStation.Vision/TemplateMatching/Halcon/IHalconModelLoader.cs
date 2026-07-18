namespace VisionStation.Vision;

/// <summary>
/// Owns one loaded HALCON model resource. The concrete implementation keeps HALCON types inside
/// the HALCON adapter; callers can obtain this handle only while holding an operation lease.
/// </summary>
internal interface IHalconModelHandle : IDisposable
{
}

/// <summary>
/// Loads one validated immutable model generation for the cache.
/// </summary>
internal interface IHalconModelLoader
{
    Task<IHalconModelHandle> LoadAsync(
        HalconTemplateModelCacheKey key,
        ResolvedTemplateModel resolvedModel,
        CancellationToken cancellationToken);
}
