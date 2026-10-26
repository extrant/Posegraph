using System.Text.Json.Nodes;

namespace Posegraph.Library;

/// <summary>
/// 存到 Brio 的姿势库（文档\Brio\Poses\Posegraph）：在 Brio 资料库里能直接看到，带预览图和标签。
/// Brio 的 .pose 用 Author / Description / Version / Tags / Base64Image 字段显示这些信息。
/// </summary>
public static class BrioPoseLibrary
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                                                "Brio", "Poses", "Posegraph");

    /// <summary>返回保存的路径（重名自动加编号）。</summary>
    public static string Save(string poseJson, string name, IEnumerable<string> tags, byte[]? previewPng, string description)
    {
        var doc = JsonNode.Parse(poseJson)!.AsObject();
        doc["Author"] = "Posegraph";
        doc["Description"] = description;
        doc["Version"] = "1";
        var tagArray = new JsonArray();
        foreach (var t in tags.Prepend("Posegraph").Distinct()) tagArray.Add(t);
        doc["Tags"] = tagArray;
        if (previewPng is not null) doc["Base64Image"] = Convert.ToBase64String(previewPng);

        Directory.CreateDirectory(Folder);
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        var path = ImageLibrary.Unique(Path.Combine(Folder, (safe.Length > 0 ? safe : "pose") + ".pose"));
        File.WriteAllText(path, doc.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return path;
    }
}
