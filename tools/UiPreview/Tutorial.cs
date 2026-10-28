using System.Numerics;
using Dalamud.Bindings.ImGui;
using Posegraph.UI;

namespace UiPreview;

/// <summary>
/// 教程截图场景（tut-*），配合 --demo 使用：示例图在临时图库里，主窗口真实识别（需要本机后端在线）。
/// 按顺序运行：tut-image 识别示例图，之后的 3D / 调整 / 提示页沿用同一个结果。
/// </summary>
public static class Tutorial
{
    private static readonly Vector2 MainSize = new(460, 660);

    private static void PrepareMain(App app, int tab)
    {
        App.FakeActors = Demo.Actors;
        app.Gposing = true;
        app.Main.IsOpen = true;
        app.Main.Position = new Vector2(40, 40);
        app.Main.Size = MainSize;
        app.Main.SizeCondition = ImGuiCond.Always;
        app.SelectTab(tab);
    }

    private static void DrawMain(App app) => Host.DrawWindow(app.Main);

    private static Action<App, string> MainTab(int tab) => (app, path) =>
    {
        PrepareMain(app, tab);
        app.Shot(path, () => DrawMain(app), [app.Main.WindowName], frames: 6);
    };

    public static void Register(Dictionary<string, Action<App, string>> scenes)
    {
        scenes["tut-empty"] = (app, path) =>
        {
            PrepareMain(app, 0);
            Demo.WaitBackend(app, () => DrawMain(app));
            app.Shot(path, () => DrawMain(app), [app.Main.WindowName], frames: 6);
        };
        scenes["tut-image"] = (app, path) =>
        {
            PrepareMain(app, 0);
            Demo.Open(app, Demo.Image("lean-wall"), () => DrawMain(app));
            app.SelectTab(0);
            app.Shot(path, () => DrawMain(app), [app.Main.WindowName], frames: 6);
        };
        scenes["tut-3d"] = (app, path) =>
        {
            var view = App.Get<Posegraph.UI.Scene3D>(app.Main, "_stickView")!;   // 斜侧视角，看得出前后深度
            (view.Yaw, view.Pitch) = (-0.6f, 0.2f);
            MainTab(1)(app, path);
        };
        scenes["tut-tuning"] = MainTab(2);
        scenes["tut-hints"] = MainTab(3);
        scenes["tut-settings"] = MainTab(4);
        scenes["tut-library"] = (app, path) =>
        {
            App.FakeActors = Demo.Actors;
            app.Library.IsOpen = true;
            app.Library.Position = new Vector2(40, 40);
            app.Library.Size = new Vector2(760, 520);
            app.Library.SizeCondition = ImGuiCond.Always;
            // 列目录和缩略图都在后台：多画一会儿
            for (var i = 0; i < 40; i++)
            {
                Host.Current.Frame(() => Host.DrawWindow(app.Library));
                Thread.Sleep(100);
            }
            app.Shot(path, () => Host.DrawWindow(app.Library), [app.Library.WindowName]);
        };
        scenes["tut-split"] = (app, path) =>
        {
            PrepareMain(app, 0);
            app.Split.IsOpen = true;
            app.Config.SplitDock = true;
            void Draw()
            {
                DrawMain(app);
                Host.DrawWindow(app.Split);
            }
            for (var i = 0; i < 3; i++) Host.Current.Frame(Draw);   // 进入 GPose 时绑定会清空，之后再绑
            Demo.Bind(app.Split, 201, Demo.Image("lean-wall"));
            Demo.Bind(app.Split, 202, Demo.Image("sit-wall"));
            var status = App.Get<System.Collections.Concurrent.ConcurrentDictionary<string, (string, Vector4)>>(app.Split, "_rowStatus");
            status[$"201:{Demo.Actors[0].Name}"] = (Posegraph.Loc.T("已应用"), PosegraphTheme.Green);
            for (var i = 0; i < 60; i++)
            {
                Host.Current.Frame(Draw);
                Thread.Sleep(100);   // 缩略图后台解码（同时最多两张）
            }
            app.Shot(path, Draw, [app.Main.WindowName, app.Split.WindowName]);
            app.Split.IsOpen = false;
        };
        scenes["tut-aux"] = (app, path) =>
        {
            // 图库右键「辅助视角」子菜单的内容，画在一个像菜单的小窗口里
            var image = Demo.Image("lean-wall");
            const string name = "##auxDemo";
            void Draw()
            {
                PosegraphTheme.Push();
                ImGui.SetNextWindowPos(new Vector2(40, 40));
                ImGui.Begin(name, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoMove);
                app.Main.AuxMenu!.Draw(image, [], (_, _) => { });
                ImGui.End();
                PosegraphTheme.Pop();
            }
            app.Shot(path, Draw, [name]);
        };
        scenes["tut-changelog"] = (app, path) =>
        {
            app.Update.IsOpen = true;
            app.Shot(path, () => Host.DrawWindow(app.Update), [app.Update.WindowName]);
            app.Update.IsOpen = false;
        };
    }
}
