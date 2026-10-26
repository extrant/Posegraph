using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace Posegraph.Library;

/// <summary>纹理的缩放与 PNG 编码（缩略图、导出 .pose 的预览图、剪切板）。</summary>
internal static class TextureImages
{
    public static async Task<byte[]> EncodePngAsync(ITextureReadbackProvider readback, IDalamudTextureWrap texture,
                                                    CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var encoder = readback.GetSupportedImageEncoderInfos()
            .FirstOrDefault(c => c.MimeTypes.Contains("image/png", StringComparer.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(Loc.T("未找到 PNG 图像编码器"));
        using var stream = new MemoryStream();
        await readback.SaveToStreamAsync(texture, encoder.ContainerGuid, stream, null, true, true, ct);
        ct.ThrowIfCancellationRequested();
        return stream.ToArray();
    }

    /// <summary>
    /// 按长边不超过 maxSide 等比缩小（本来就小的不放大）。返回新纹理，source 仍归调用方释放。
    /// 注意 Dalamud 在不需要改动时可能直接返回 source 本身，调用方释放 source 前要先判断是不是同一个对象。
    /// </summary>
    public static async Task<IDalamudTextureWrap> ShrinkAsync(ITextureProvider textures, IDalamudTextureWrap source, int maxSide,
                                                              CancellationToken ct = default)
    {
        var scale = Math.Min(1f, (float)maxSide / Math.Max(source.Width, source.Height));
        var args = new TextureModificationArgs
        {
            NewWidth = Math.Max(1, (int)(source.Width * scale)),
            NewHeight = Math.Max(1, (int)(source.Height * scale)),
        };
        return await textures.CreateFromExistingTextureAsync(source, args, true, "Posegraph thumbnail", ct);
    }

    /// <summary>图片字节 → 缩小后的纹理（原图纹理用完即释放）。</summary>
    public static async Task<IDalamudTextureWrap> ThumbnailAsync(ITextureProvider textures, byte[] image, int maxSide,
                                                                 CancellationToken ct = default)
    {
        var full = await textures.CreateFromImageAsync(image, "Posegraph image", ct);
        try
        {
            var small = await ShrinkAsync(textures, full, maxSide, ct);
            if (ReferenceEquals(small, full)) full = null;   // 没有缩放，返回的就是原图纹理：交给调用方
            return small;
        }
        finally
        {
            full?.Dispose();
        }
    }

    /// <summary>图片字节 → 缩小后的 PNG（导出到 Brio 姿势库的预览图）。</summary>
    public static async Task<byte[]> ThumbnailPngAsync(ITextureProvider textures, ITextureReadbackProvider readback,
                                                       byte[] image, int maxSide, CancellationToken ct = default)
    {
        using var small = await ThumbnailAsync(textures, image, maxSide, ct);
        return await EncodePngAsync(readback, small, ct);
    }
}
