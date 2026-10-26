using Dalamud.Plugin.Services;
using Posegraph.Library;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

// Deterministic provider checks: no game, desktop clipboard or temporary files.
var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
var checks = new (string Name, Func<Task> Run)[]
{
    ("No image returns null without creating a texture", async () =>
    {
        var textures = new Textures { HasImage = false };
        var readback = new Readback(png);
        Assert(await ClipboardImage.ReadPngAsync(textures, readback) is null);
        Assert(textures.Created == 0 && readback.Encoded == 0);
    }),
    ("PNG stays in memory and temporary texture is disposed", async () =>
    {
        var textures = new Textures();
        var readback = new Readback(png);
        var result = await ClipboardImage.ReadPngAsync(textures, readback);
        Assert(result is not null && result.SequenceEqual(png));
        Assert(textures.Texture.DisposeCount == 1 && readback.Encoded == 1);
    }),
    ("Pre-cancelled read never touches the clipboard", async () =>
    {
        var textures = new Textures();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Throws<OperationCanceledException>(() => ClipboardImage.ReadPngAsync(textures, new Readback(png), cts.Token));
        Assert(textures.Checked == 0 && textures.Created == 0);
    }),
    ("Late texture after cancellation is disposed without encoding", async () =>
    {
        var gate = new TaskCompletionSource<Texture>(TaskCreationOptions.RunContinuationsAsynchronously);
        var textures = new Textures { Create = _ => gate.Task };
        var readback = new Readback(png);
        using var cts = new CancellationTokenSource();
        var pending = ClipboardImage.ReadPngAsync(textures, readback, cts.Token);
        cts.Cancel();
        gate.SetResult(textures.Texture); // Simulate a provider that ignores cancellation.
        await Throws<OperationCanceledException>(() => pending);
        Assert(textures.Texture.DisposeCount == 1 && readback.Encoded == 0);
    }),
    ("Cancellation during encoding does not publish bytes", async () =>
    {
        var textures = new Textures();
        using var cts = new CancellationTokenSource();
        var readback = new Readback(png) { BeforeWrite = cts.Cancel };
        await Throws<OperationCanceledException>(() => ClipboardImage.ReadPngAsync(textures, readback, cts.Token));
        Assert(textures.Texture.DisposeCount == 1 && readback.Encoded == 1);
    }),
    ("Encoding error disposes the clipboard texture", async () =>
    {
        var textures = new Textures();
        var readback = new Readback(png) { BeforeWrite = () => throw new IOException("encoding failed") };
        await Throws<IOException>(() => ClipboardImage.ReadPngAsync(textures, readback));
        Assert(textures.Texture.DisposeCount == 1);
    }),
    ("Missing PNG encoder disposes the acquired texture", async () =>
    {
        var textures = new Textures();
        await Throws<InvalidOperationException>(() => ClipboardImage.ReadPngAsync(textures, new Readback(png) { HasPng = false }));
        Assert(textures.Created == 1 && textures.Texture.DisposeCount == 1);
    }),
};
foreach (var (name, run) in checks)
{
    await run().WaitAsync(TimeSpan.FromSeconds(5));
    Console.WriteLine($"PASS {name}");
}
Console.WriteLine($"{checks.Length} clipboard checks passed (production source linked).");

static void Assert(bool condition)
{
    if (!condition) throw new InvalidOperationException("Check failed");
}
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}");
}

namespace Posegraph
{
    internal static class Loc { public static string T(string text) => text; }
}
namespace Dalamud.Plugin.Services
{
    // Minimal compile-time service contract. Actual Dalamud API compatibility is
    // checked separately by building Posegraph.csproj against its installed SDK.
    internal interface ITextureProvider
    {
        bool HasClipboardImage();
        Task<IDalamudTextureWrap> CreateFromClipboardAsync(string debugName, CancellationToken cancellationToken);
        Task<IDalamudTextureWrap> CreateFromExistingTextureAsync(IDalamudTextureWrap source, TextureModificationArgs args,
            bool leaveWrapOpen, string debugName, CancellationToken cancellationToken);
        Task<IDalamudTextureWrap> CreateFromImageAsync(ReadOnlyMemory<byte> bytes, string debugName, CancellationToken cancellationToken);
    }
    internal interface ITextureReadbackProvider
    {
        IEnumerable<Encoder> GetSupportedImageEncoderInfos();
        Task SaveToStreamAsync(IDalamudTextureWrap wrap, Guid containerGuid, Stream stream,
            IReadOnlyDictionary<string, object>? props, bool leaveWrapOpen, bool leaveStreamOpen, CancellationToken cancellationToken);
    }
    internal sealed record Encoder(Guid ContainerGuid, string[] MimeTypes);
    internal sealed class Texture : IDalamudTextureWrap
    {
        public int Width => 1;
        public int Height => 1;
        public int DisposeCount;
        public void Dispose() => DisposeCount++;
    }
    internal sealed class Textures : ITextureProvider
    {
        public bool HasImage = true;
        public int Checked, Created;
        public Texture Texture = new();
        public Func<CancellationToken, Task<Texture>>? Create;
        public bool HasClipboardImage() { Checked++; return HasImage; }
        public async Task<IDalamudTextureWrap> CreateFromClipboardAsync(string debugName, CancellationToken cancellationToken)
        {
            Created++;
            return await (Create?.Invoke(cancellationToken) ?? Task.FromResult(Texture));
        }
        public Task<IDalamudTextureWrap> CreateFromExistingTextureAsync(IDalamudTextureWrap source, TextureModificationArgs args,
            bool leaveWrapOpen, string debugName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IDalamudTextureWrap> CreateFromImageAsync(ReadOnlyMemory<byte> bytes, string debugName, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
    internal sealed class Readback(byte[] png) : ITextureReadbackProvider
    {
        private static readonly Guid Png = new("1b7cfaf4-713f-473c-bbcd-6137425faeaf");
        public bool HasPng = true;
        public int Encoded;
        public Action? BeforeWrite;
        public IEnumerable<Encoder> GetSupportedImageEncoderInfos() => HasPng ? [new(Png, ["IMAGE/PNG"])] : [];
        public Task SaveToStreamAsync(IDalamudTextureWrap wrap, Guid containerGuid, Stream stream,
            IReadOnlyDictionary<string, object>? props, bool leaveWrapOpen, bool leaveStreamOpen, CancellationToken cancellationToken)
        {
            if (containerGuid != Png || props is not null || !leaveWrapOpen || !leaveStreamOpen || ((Texture)wrap).DisposeCount != 0)
                throw new InvalidOperationException("Incorrect encoder ownership/options");
            Encoded++;
            BeforeWrite?.Invoke();
            stream.Write(png); // Intentionally ignores cancellation; caller must reject late completion.
            return Task.CompletedTask;
        }
    }
}
namespace Dalamud.Interface.Textures
{
    internal struct TextureModificationArgs { public int NewWidth, NewHeight; }
}
namespace Dalamud.Interface.Textures.TextureWraps
{
    internal interface IDalamudTextureWrap : IDisposable { int Width { get; } int Height { get; } }
}
