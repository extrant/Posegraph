using System.Collections.Concurrent;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace Posegraph.Editor;

/// <summary>BodyMesh 里要读游戏的部分：角色加载着哪些模型、从游戏数据或磁盘读模型文件。</summary>
public static partial class BodyMesh
{
    private static readonly ConcurrentDictionary<(string Path, uint Attributes), Part?> Cache = new();

    /// <summary>角色现在加载着的模型：路径和启用的属性。必须在主线程调用。</summary>
    public static unsafe List<(string Path, uint Attributes)> ModelFiles(IGameObject actor)
    {
        var list = new List<(string, uint)>();
        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)actor.Address;
        if (go == null) return list;
        var draw = (CharacterBase*)go->DrawObject;
        if (draw == null) return list;
        foreach (var pointer in draw->ModelsSpan)
        {
            var model = pointer.Value;
            if (model == null || model->ModelResourceHandle == null) continue;
            // 路径是 UTF-8 字节；ToString() 会按系统代码页（中文系统是 GBK）解码，mod 路径里的中文就成了乱码、读不到文件
            var name = System.Text.Encoding.UTF8.GetString(model->ModelResourceHandle->ResourceHandle.FileName.AsSpan());
            if (string.IsNullOrEmpty(name)) continue;
            // Penumbra 换过的文件名形如 |集合|D:\mods\...\xxx.mdl
            if (name.StartsWith('|') && name.IndexOf('|', 1) is var bar and > 0) name = name[(bar + 1)..];
            if (name.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) list.Add((name, model->EnabledAttributeIndexMask));
        }
        return list;
    }

    /// <summary>读一个模型（缓存；可以在后台线程调用）。读不了时 null。</summary>
    public static Part? Load(IDataManager data, string path, uint attributes, IPluginLog? log = null)
        => Cache.GetOrAdd((path, attributes), key =>
        {
            try
            {
                // 原始字节自己解析（Lumina 的 MdlFile 读不了 7.x 的模型）；Penumbra 的 mod 文件在磁盘上，原版的在游戏数据里
                var bytes = Path.IsPathRooted(key.Path) ? File.ReadAllBytes(key.Path) : data.GetFile(key.Path)?.Data;
                return bytes is null ? null : Parse(key.Path, bytes, key.Attributes);
            }
            catch (Exception ex)
            {
                log?.Warning(ex, "Posegraph: could not read model {Path}", key.Path);
                return null;
            }
        });

}