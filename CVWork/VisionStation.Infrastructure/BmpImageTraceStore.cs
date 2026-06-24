using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using VisionStation.Domain;

namespace VisionStation.Infrastructure;

public sealed class BmpImageTraceStore : IImageTraceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly RuntimePaths _paths;

    public BmpImageTraceStore(RuntimePaths paths)
    {
        _paths = paths;
    }

    public async Task<ImageTracePaths> SaveAsync(
        Recipe recipe,
        ImageFrame originalFrame,
        ImageFrame resultFrame,
        InspectionResult result,
        CancellationToken cancellationToken = default)
    {
        var folder = Path.Combine(
            _paths.ImageTraceDirectory,
            RuntimePaths.SanitizePathSegment(result.Outcome.ToString()),
            result.Timestamp.ToString("yyyyMMdd"),
            RuntimePaths.SanitizePathSegment(recipe.Id),
            RuntimePaths.SanitizePathSegment(result.BatchId),
            RuntimePaths.SanitizePathSegment(result.Id));
        Directory.CreateDirectory(folder);

        var extension = ResolveImageExtension(recipe.TracePolicy.ImageFormat);
        var originalPath = Path.Combine(folder, $"original{extension}");
        var resultPath = Path.Combine(folder, $"result{extension}");
        var metadataPath = Path.Combine(folder, "result.json");

        await WriteImageAsync(originalPath, originalFrame, extension, cancellationToken);
        await WriteImageAsync(resultPath, resultFrame, extension, cancellationToken);

        var enrichedResult = result with
        {
            OriginalImagePath = originalPath,
            ResultImagePath = resultPath
        };
        await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(enrichedResult, JsonOptions), cancellationToken);

        return new ImageTracePaths(originalPath, resultPath, metadataPath);
    }

    private static string ResolveImageExtension(string? imageFormat)
    {
        return imageFormat?.Trim().ToLowerInvariant() switch
        {
            "png" => ".png",
            _ => ".bmp"
        };
    }

    private static Task WriteImageAsync(
        string path,
        ImageFrame frame,
        string extension,
        CancellationToken cancellationToken)
    {
        return string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
            ? WritePngAsync(path, frame, cancellationToken)
            : WriteBmpAsync(path, frame, cancellationToken);
    }

    private static async Task WriteBmpAsync(string path, ImageFrame frame, CancellationToken cancellationToken)
    {
        var header = new byte[54];
        var targetStride = frame.Width * 4;
        var pixelBytes = targetStride * frame.Height;
        var fileSize = header.Length + pixelBytes;

        header[0] = (byte)'B';
        header[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(2), fileSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(18), frame.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(22), frame.Height);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(28), 32);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(34), pixelBytes);

        await using var stream = File.Create(path);
        await stream.WriteAsync(header, cancellationToken);

        var row = new byte[targetStride];
        for (var y = frame.Height - 1; y >= 0; y--)
        {
            CopyRowAsBgra32(frame, y, row);
            await stream.WriteAsync(row, cancellationToken);
        }
    }

    private static async Task WritePngAsync(string path, ImageFrame frame, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(path);
        await stream.WriteAsync(PngSignature, cancellationToken);

        var colorType = frame.Format == PixelFormatKind.Gray8 ? (byte)0 : (byte)6;
        var bytesPerPixel = colorType == 0 ? 1 : 4;
        var rawStride = frame.Width * bytesPerPixel + 1;
        var raw = new byte[rawStride * frame.Height];
        for (var y = 0; y < frame.Height; y++)
        {
            var rowOffset = y * rawStride;
            raw[rowOffset] = 0;
            CopyRowForPng(frame, y, raw.AsSpan(rowOffset + 1, frame.Width * bytesPerPixel), colorType);
        }

        await WritePngChunkAsync(stream, "IHDR", CreatePngHeader(frame.Width, frame.Height, colorType), cancellationToken);
        await WritePngChunkAsync(stream, "IDAT", CompressPngData(raw), cancellationToken);
        await WritePngChunkAsync(stream, "IEND", Array.Empty<byte>(), cancellationToken);
    }

    private static void CopyRowForPng(ImageFrame frame, int y, Span<byte> target, byte colorType)
    {
        var sourceOffset = y * frame.Stride;
        if (colorType == 0)
        {
            if (frame.Format == PixelFormatKind.Gray8)
            {
                frame.Pixels.AsSpan(sourceOffset, frame.Width).CopyTo(target);
                return;
            }

            for (var x = 0; x < frame.Width; x++)
            {
                var sourcePixel = sourceOffset + x * (frame.Format == PixelFormatKind.Bgr24 ? 3 : 4);
                var blue = frame.Pixels[sourcePixel];
                var green = frame.Pixels[sourcePixel + 1];
                var red = frame.Pixels[sourcePixel + 2];
                target[x] = (byte)((red * 299 + green * 587 + blue * 114) / 1000);
            }

            return;
        }

        switch (frame.Format)
        {
            case PixelFormatKind.Gray8:
                for (var x = 0; x < frame.Width; x++)
                {
                    var value = frame.Pixels[sourceOffset + x];
                    var targetOffset = x * 4;
                    target[targetOffset] = value;
                    target[targetOffset + 1] = value;
                    target[targetOffset + 2] = value;
                    target[targetOffset + 3] = 255;
                }

                break;
            case PixelFormatKind.Bgr24:
                for (var x = 0; x < frame.Width; x++)
                {
                    var sourcePixel = sourceOffset + x * 3;
                    var targetOffset = x * 4;
                    target[targetOffset] = frame.Pixels[sourcePixel + 2];
                    target[targetOffset + 1] = frame.Pixels[sourcePixel + 1];
                    target[targetOffset + 2] = frame.Pixels[sourcePixel];
                    target[targetOffset + 3] = 255;
                }

                break;
            default:
                for (var x = 0; x < frame.Width; x++)
                {
                    var sourcePixel = sourceOffset + x * 4;
                    var targetOffset = x * 4;
                    target[targetOffset] = frame.Pixels[sourcePixel + 2];
                    target[targetOffset + 1] = frame.Pixels[sourcePixel + 1];
                    target[targetOffset + 2] = frame.Pixels[sourcePixel];
                    target[targetOffset + 3] = frame.Pixels[sourcePixel + 3];
                }

                break;
        }
    }

    private static byte[] CreatePngHeader(int width, int height, byte colorType)
    {
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = colorType;
        return header;
    }

    private static byte[] CompressPngData(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        return output.ToArray();
    }

    private static async Task WritePngChunkAsync(
        Stream stream,
        string type,
        byte[] data,
        CancellationToken cancellationToken)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        await stream.WriteAsync(length, cancellationToken);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        await stream.WriteAsync(typeBytes, cancellationToken);
        await stream.WriteAsync(data, cancellationToken);

        var crc = Crc32(typeBytes, data);
        var checksum = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc);
        await stream.WriteAsync(checksum, cancellationToken);
    }

    private static uint Crc32(byte[] typeBytes, byte[] data)
    {
        var crc = 0xffffffffu;
        crc = UpdateCrc32(crc, typeBytes);
        crc = UpdateCrc32(crc, data);
        return crc ^ 0xffffffffu;
    }

    private static uint UpdateCrc32(uint crc, byte[] data)
    {
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 1 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
            }
        }

        return crc;
    }

    private static void CopyRowAsBgra32(ImageFrame frame, int y, byte[] target)
    {
        Array.Clear(target);
        var sourceOffset = y * frame.Stride;
        switch (frame.Format)
        {
            case PixelFormatKind.Gray8:
                for (var x = 0; x < frame.Width; x++)
                {
                    var value = frame.Pixels[sourceOffset + x];
                    var targetOffset = x * 4;
                    target[targetOffset] = value;
                    target[targetOffset + 1] = value;
                    target[targetOffset + 2] = value;
                    target[targetOffset + 3] = 255;
                }

                break;
            case PixelFormatKind.Bgr24:
                for (var x = 0; x < frame.Width; x++)
                {
                    var sourcePixel = sourceOffset + x * 3;
                    var targetOffset = x * 4;
                    target[targetOffset] = frame.Pixels[sourcePixel];
                    target[targetOffset + 1] = frame.Pixels[sourcePixel + 1];
                    target[targetOffset + 2] = frame.Pixels[sourcePixel + 2];
                    target[targetOffset + 3] = 255;
                }

                break;
            default:
                Buffer.BlockCopy(frame.Pixels, sourceOffset, target, 0, Math.Min(target.Length, frame.Stride));
                break;
        }
    }

    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
}
