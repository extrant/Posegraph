using System.Numerics;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Posegraph;
using Posegraph.Editor;
using Posegraph.UI;

namespace UiPreview;

/// <summary>
/// 姿势编辑器面板、编辑器教程、「支持 Posegraph」窗口的截图。
/// 编辑器面板要一份姿势：环境变量 POSEGRAPH_SKELETON（骨架 JSON：names / parents）和 POSEGRAPH_POSE（Brio 姿势文件）。
/// </summary>
public static class EditorScenes
{
    public static void Register(Dictionary<string, Action<App, string>> scenes)
    {
        scenes["support"] = (app, path) =>
        {
            var w = new SupportWindow(Services.Textures(app.Host.Renderer));
            w.IsOpen = true;
            w.OnOpen();
            app.Shot(path, () => Host.DrawWindow(w), [w.WindowName], frames: 6);
        };
        for (var page = 0; page < 7; page++)
        {
            var p = page;
            scenes[$"editor-tutorial-{p}"] = (app, path) =>
            {
                var w = new EditorTutorialWindow(app.Config, Services.PluginInterface(() => []));
                w.Show(p == 0);
                App.Set(w, "_page", p);
                app.Shot(path, () => Host.DrawWindow(w), [w.WindowName], frames: 6);
            };
        }
        scenes["editor-panel"] = (app, path) =>
        {
            var editor = MakeEditor(app);
            App.FakeActors = [(201, "Aerith Gainsborough")];
            app.Gposing = true;
            app.Main.IsOpen = true;
            app.Main.PoseEditor = new MainWindow.PoseEditorHooks(_ => Task.FromResult<string?>(null), () => { }, () => { },
                                                                 () => editor.Active, editor.DrawPanel, () => null);
            app.SelectTab(1);
            app.Shot(path, () => Host.DrawWindow(app.Main), [app.Main.WindowName], frames: 6);
            app.Gposing = false;
            App.FakeActors = null;
        };
    }

    private static PoseEditor MakeEditor(App app)
    {
        var editor = new PoseEditor(Stub.Create<IFramework>(), Stub.Create<IClientState>(), Stub.Create<IGameGui>(), Stub.Create<IKeyState>(),
                                    Stub.Create<IDataManager>(), Stub.Create<IPluginLog>(), null!, null!);
        using var sk = JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("POSEGRAPH_SKELETON")!));
        var names = sk.RootElement.GetProperty("names").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var parentIndex = sk.RootElement.GetProperty("parents").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        var parents = new Dictionary<string, string>();
        for (var i = 0; i < names.Length; i++)
            if (parentIndex[i] >= 0) parents[names[i]] = names[parentIndex[i]];
        var pose = EditPose.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("POSEGRAPH_POSE")!), parents)!;
        var nodes = PoseNodes.Build(pose);
        App.Set(editor, "_pose", pose);
        App.Set(editor, "_nodes", nodes);
        App.Set(editor, "_tips", PoseNodes.Tips(pose, nodes));
        App.Set(editor, "_selected", Environment.GetEnvironmentVariable("POSEGRAPH_SELECT") ?? "hand_r");
        App.Set(editor, "_selfParts", new List<BodyMesh.Part>());
        App.Set(editor, "_meshStatus", "身体网格：自己 7 个模型，另外 2 个角色（13 个模型），共 21.5 万个三角形");
        return editor;
    }
}
