using Dalamud.Plugin.Services;

namespace Posegraph.Library;

/// <summary>经 Dalamud 读剪切板图片并编码成 PNG（改编自 PR #1，uiharuayako）。</summary>
internal static class ClipboardImage
{
    public static async Task<byte[]?> ReadPngAsync(ITextureProvider textures, ITextureReadbackProvider readback,
                                                   CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!textures.HasClipboardImage()) return null;
        using var texture = await textures.CreateFromClipboardAsync("Posegraph clipboard", ct);
        ct.ThrowIfCancellationRequested();
        return await TextureImages.EncodePngAsync(readback, texture, ct);
    }
}
