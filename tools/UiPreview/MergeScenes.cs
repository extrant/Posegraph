using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace UiPreview;

/// <summary>
/// 合并模式（涂人）的离线检查（不包含在 all 里，需要后端）：主窗口打开一张多人图 → 涂人 → 识别 → 重新打开这张图，
/// 看多人结果是否从识别缓存恢复。环境变量：
/// MERGE_IMAGE 多人图；MERGE_PORT 后端端口（默认 42430）；MERGE_DETECTOR 自动初稿的检测模型（auto / photo / anime）；
/// PAINT_FROM：draft（自动初稿，默认）或一个标签图文件（标签图尺寸、每像素一个字节 0 / k + 1，模拟用户粗涂的「看得见」，人数取最大标签）；
/// PAINT_STROKES：再涂几笔「层,擦除 0/1,被挡住 0/1,x1,y1,x2,y2,半径;…」（标签图像素）。
/// 输出 &lt;名字&gt;-paint.png（涂好）、-after.png（识别后）、-reopen.png（重新打开后）。
/// </summary>
public static class MergeScenes
{
    public static void Register(Dictionary<string, Action<App, string>> scenes)
    {
        RegisterUiCheck(scenes);
        scenes["bench-paint"] = (app, path) =>
        {
            var image = Environment.GetEnvironmentVariable("MERGE_IMAGE") ?? throw new InvalidOperationException("没有设置 MERGE_IMAGE");
            app.Config.BackendPort = int.Parse(Environment.GetEnvironmentVariable("MERGE_PORT") ?? "42430");
            app.Config.MergeDetector = Environment.GetEnvironmentVariable("MERGE_DETECTOR") ?? "auto";
            App.FakeActors = Demo.Actors;
            app.Gposing = true;
            app.Main.IsOpen = true;
            app.Main.Position = new Vector2(40, 40);
            app.Main.Size = new Vector2(520, 720);
            app.Main.SizeCondition = ImGuiCond.Always;
            app.Config.SplitMerge = true;
            app.Config.SplitDock = true;
            app.Split.IsOpen = true;
            app.Masks.Position = new Vector2(40, 40);   // 画布 1600×1000；截涂人窗口时它盖在主窗口上面
            app.Masks.Size = new Vector2(1000, 900);
            app.Masks.PositionCondition = ImGuiCond.Always;
            app.Masks.SizeCondition = ImGuiCond.Always;
            app.SelectTab(0);
            void Draw()
            {
                Host.DrawWindow(app.Main);
                Host.DrawWindow(app.Split);
                Host.DrawWindow(app.Masks);
            }
            string[] main = [app.Main.WindowName, app.Split.WindowName];

            Demo.Open(app, image, Draw);
            var from = Environment.GetEnvironmentVariable("PAINT_FROM") ?? "draft";
            if (from == "draft")
            {
                app.Main.AutoDraft();
                Until(Draw, () => !app.Main.Drafting && app.Main.Paint is not null, 180, "自动初稿");
            }
            else
            {
                app.Main.StartPainting();
                Until(Draw, () => app.Main.Paint is not null, 60, "打开涂人窗口");
                var doc = app.Main.Paint!;
                var labels = File.ReadAllBytes(from);
                if (labels.Length != doc.Visible.Length) throw new InvalidOperationException($"标签图大小不对：{labels.Length} ≠ {doc.Visible.Length}");
                while (doc.Count < labels.Max()) app.Main.AddPerson();
                doc.BeginStep();
                labels.CopyTo(doc.Visible, 0);
                doc.Stroke(Vector2.Zero, Vector2.Zero, 0.1f, 0, false);   // 让版本号变一下（重建叠加层）
            }
            var paint = app.Main.Paint!;
            foreach (var s in (Environment.GetEnvironmentVariable("PAINT_STROKES") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var v = s.Split(',').Select(float.Parse).ToArray();
                paint.BeginStep();
                paint.Stroke(new Vector2(v[3], v[4]), new Vector2(v[5], v[6]), v[7], (int)v[0], v[1] > 0, v[2] > 0);
            }
            var actors = new Queue<int>(Demo.Actors.Select(a => a.Index));
            foreach (var p in app.Main.People)
                if (actors.Count > 0) app.Main.SetPersonActor(p, actors.Dequeue());
            app.Masks.IsOpen = true;
            for (var i = 0; i < 8; i++) Host.Current.Frame(Draw);   // 等纹理建好
            app.Shot(path.Replace(".png", "-paint.png"), Draw, [app.Masks.WindowName], frames: 4);
            Console.WriteLine("涂好：" + string.Join("  ", Enumerable.Range(0, paint.Count).Select(k =>
                $"{k + 1}:[{string.Join(",", paint.Box(k, app.Main.SessionImageSize!)?.Select(x => x.ToString("0")) ?? [])}]")));

            app.Main.RecognizePeople();
            Until(Draw, () => !app.Main.Recognizing && app.Main.MergeSessionActive && !app.Main.PeopleChanged, 180, "识别");
            WaitRetarget(app, Draw);
            app.Masks.IsOpen = false;   // 截主窗口和角色列表
            app.Shot(path.Replace(".png", "-after.png"), Draw, main, frames: 4);
            Console.WriteLine("识别后：" + App.Get<string>(app.Main, "_statusText"));

            // 重新打开这张图：涂抹从图库读回来，多人结果从识别缓存恢复（不跑模型）
            Demo.Open(app, image, Draw);
            Until(Draw, () => app.Main.MergeSessionActive && !app.Main.Recognizing, 60, "从缓存恢复");
            WaitRetarget(app, Draw);
            app.Shot(path.Replace(".png", "-reopen.png"), Draw, main, frames: 4);
            Console.WriteLine("重新打开后：" + App.Get<string>(app.Main, "_statusText") + "  "
                              + string.Join("  ", app.Main.People.Select((p, i) => $"{i + 1}:human={p.Human?.ToString() ?? "-"}")));
        };
    }

    /// <summary>
    /// 合并模式的界面检查（需要后端，图片要涂过人并识别过）：打开 MERGE_IMAGE 从识别缓存恢复多人，
    /// 截 3D 页（洋葱皮、平移后）、调整页（整体旋转 / 位移）、面部表情和涂人窗口（主题）。
    /// 输出 &lt;名字&gt;-3d.png、-tuning.png、-face.png、-paint.png。
    /// </summary>
    public static void RegisterUiCheck(Dictionary<string, Action<App, string>> scenes)
    {
        // 灯光窗口：套上「环形光」（不进游戏：灯不会真的生成，只看界面），展开逐盏调整
        scenes["lighting"] = (app, path) =>
        {
            app.Gposing = true;
            var rig = Posegraph.Lighting.LightRigs.Builtin[0];
            typeof(Posegraph.Lighting.LightingService).GetProperty("Active")!.SetValue(app.Lighting, rig);
            typeof(Posegraph.Lighting.LightingService).GetProperty("Lights")!.SetValue(app.Lighting, rig.Lights.ToList());
            // 假的人和镜头：脸在 (0, 1.55, 0) 朝 +z，镜头在前方偏右
            var svc = typeof(Posegraph.Lighting.LightingService);
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var face = new Vector3(0, 1.55f, 0);
            svc.GetField("_subject", flags)!.SetValue(app.Lighting, (Posegraph.Lighting.Subject?)new Posegraph.Lighting.Subject(face, Vector3.UnitZ, Vector3.UnitY, 1f));
            List<(Vector3, Vector3)> body =
            [
                (new(0, 0.95f, 0), new(0, 1.25f, 0)), (new(0, 1.25f, 0), new(0, 1.45f, 0)), (new(0, 1.45f, 0), new(0, 1.6f, 0)),
                (new(0, 1.4f, 0), new(0.2f, 1.38f, 0)), (new(0.2f, 1.38f, 0), new(0.25f, 1.1f, 0.05f)), (new(0.25f, 1.1f, 0.05f), new(0.2f, 0.9f, 0.2f)),
                (new(0, 1.4f, 0), new(-0.2f, 1.38f, 0)), (new(-0.2f, 1.38f, 0), new(-0.25f, 1.1f, 0)), (new(-0.25f, 1.1f, 0), new(-0.27f, 0.85f, 0.02f)),
                (new(0, 0.95f, 0), new(0.1f, 0.9f, 0)), (new(0.1f, 0.9f, 0), new(0.12f, 0.48f, 0.02f)), (new(0.12f, 0.48f, 0.02f), new(0.12f, 0.05f, 0)),
                (new(0, 0.95f, 0), new(-0.1f, 0.9f, 0)), (new(-0.1f, 0.9f, 0), new(-0.12f, 0.48f, 0.02f)), (new(-0.12f, 0.48f, 0.02f), new(-0.12f, 0.05f, 0)),
            ];
            svc.GetField("_body", flags)!.SetValue(app.Lighting, body);
            var cam = new Vector3(0.6f, 1.6f, 2.4f);
            svc.GetProperty("CameraPose")!.SetValue(app.Lighting, (Posegraph.CameraPose?)new Posegraph.CameraPose(cam, Vector3.Normalize(face - cam), Vector3.UnitY, 0.8f, 16 / 9f));
            app.Lighting.SelectedLight = 0;
            app.LightingWindow.IsOpen = true;
            app.LightingWindow.Position = new Vector2(40, 40);
            app.LightingWindow.Size = new Vector2(540, 1000);
            app.LightingWindow.SizeCondition = ImGuiCond.Always;
            void Draw() => Host.DrawWindow(app.LightingWindow);
            app.Shot(path, Draw, [app.LightingWindow.WindowName], frames: 4);
            // 逐盏调整（折叠头默认收起）：单独开一个窗口画那一段
            var drawLights = typeof(Posegraph.UI.LightingWindow).GetMethod("DrawLights",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            void DrawDetail()
            {
                Posegraph.UI.PosegraphTheme.Push();
                ImGui.SetNextWindowPos(new Vector2(40, 40), ImGuiCond.Always);
                ImGui.SetNextWindowSize(new Vector2(460, 900), ImGuiCond.Always);
                ImGui.Begin("lighting-detail");
                ImGui.SetNextItemOpen(true);
                drawLights.Invoke(app.LightingWindow, null);
                ImGui.End();
                Posegraph.UI.PosegraphTheme.Pop();
            }
            app.Shot(path.Replace(".png", "-detail.png"), DrawDetail, ["lighting-detail"], frames: 4);
        };

        // 3D 骨骼页（新的透视 3D 视图）：假骨架，选中左肘显示旋转环
        scenes["stick-rings"] = (app, path) =>
        {
            Posegraph.Backend.StickJoint J(string name, float x, float y, float z, string? parent) => new(name, [x, y, z], parent);
            Posegraph.Backend.StickJoint[] joints =
            [
                J("j_kosi", 0, 1.0f, 0, null), J("j_sebo_b", 0, 1.25f, 0, "j_kosi"), J("j_sebo_c", 0, 1.4f, 0, "j_sebo_b"),
                J("j_kubi", 0, 1.52f, 0, "j_sebo_c"), J("j_kao", 0, 1.62f, 0.02f, "j_kubi"),
                J("j_ude_a_l", 0.18f, 1.42f, 0, "j_sebo_c"), J("j_ude_b_l", 0.25f, 1.17f, 0.05f, "j_ude_a_l"), J("j_te_l", 0.22f, 0.95f, 0.2f, "j_ude_b_l"),
                J("j_ude_a_r", -0.18f, 1.42f, 0, "j_sebo_c"), J("j_ude_b_r", -0.25f, 1.17f, 0, "j_ude_a_r"), J("j_te_r", -0.27f, 0.93f, 0.02f, "j_ude_b_r"),
                J("j_asi_a_l", 0.1f, 0.93f, 0, "j_kosi"), J("j_asi_c_l", 0.12f, 0.5f, 0.03f, "j_asi_a_l"), J("j_asi_d_l", 0.12f, 0.08f, 0, "j_asi_c_l"),
                J("j_asi_a_r", -0.1f, 0.93f, 0, "j_kosi"), J("j_asi_c_r", -0.12f, 0.5f, 0.03f, "j_asi_a_r"), J("j_asi_d_r", -0.12f, 0.08f, 0, "j_asi_c_r"),
            ];
            var result = new Posegraph.Backend.RetargetResult("", [], new(), default, null, new Posegraph.Backend.Stick(joints, []), 0, null);
            App.Set(app.Main, "_result", result);
            App.Set(app.Main, "_selected", "j_ude_b_l");
            var view = App.Get<Posegraph.UI.Scene3D>(app.Main, "_stickView")!;
            (view.Yaw, view.Pitch) = (-0.5f, 0.2f);
            app.Main.IsOpen = true;
            app.Main.Position = new Vector2(40, 40);
            app.SelectTab(1);
            app.Shot(path, () => Host.DrawWindow(app.Main), [app.Main.WindowName], frames: 4);
        };

        // 构图窗口（不在 GPose：焦距那段显示提示）+ 构图框叠层（在 GPose：竖照片框、三分线、框外压暗）
        scenes["composition"] = (app, path) =>
        {
            app.Gposing = false;
            app.Config.FrameShow = true;
            app.Config.FrameGuides = 1;
            app.Composition.IsOpen = true;
            app.Composition.Position = new Vector2(40, 40);
            void Draw() => Host.DrawWindow(app.Composition);
            app.Shot(path, Draw, [app.Composition.WindowName], frames: 4);
            app.Gposing = true;
            app.Shot(path.Replace(".png", "-overlay.png"), app.Composition.DrawOverlay, [], frames: 2);
            app.Config.FrameAspect = "21:9";
            app.Config.FrameGuides = 3;
            app.Shot(path.Replace(".png", "-overlay-wide.png"), app.Composition.DrawOverlay, [], frames: 2);
            app.Gposing = false;
        };

        scenes["check-merge-ui"] = (app, path) =>
        {
            var image = Environment.GetEnvironmentVariable("MERGE_IMAGE") ?? throw new InvalidOperationException("没有设置 MERGE_IMAGE");
            app.Config.BackendPort = int.Parse(Environment.GetEnvironmentVariable("MERGE_PORT") ?? "42430");
            App.FakeActors = Demo.Actors;
            app.Gposing = true;
            app.Main.IsOpen = true;
            app.Main.Position = new Vector2(40, 40);
            app.Main.Size = new Vector2(520, 900);
            app.Main.SizeCondition = ImGuiCond.Always;
            app.Config.SplitMerge = true;
            app.Config.SplitDock = true;
            app.Config.ShowOnion = true;
            app.Split.IsOpen = true;
            app.Masks.Position = new Vector2(40, 40);
            app.Masks.Size = new Vector2(900, 700);
            app.Masks.PositionCondition = ImGuiCond.Always;
            app.Masks.SizeCondition = ImGuiCond.Always;
            void Draw()
            {
                Host.DrawWindow(app.Main);
                Host.DrawWindow(app.Split);
                Host.DrawWindow(app.Masks);
            }
            string[] main = [app.Main.WindowName, app.Split.WindowName];

            Demo.Open(app, image, Draw);
            Until(Draw, () => app.Main.MergeSessionActive && !app.Main.Recognizing, 60, "从缓存恢复");
            WaitRetarget(app, Draw);
            app.SelectTab(1);
            for (var i = 0; i < 4; i++) Host.Current.Frame(Draw);
            WaitRetarget(app, Draw);
            var onion = App.Get<Posegraph.Backend.RetargetResult?>(app.Main, "_result")?.Onion;
            Console.WriteLine("洋葱皮：" + (onion is null ? "无" : string.Join("  ", onion.Select(o => $"人{o.Person}:{o.Joints.Length}关节"))));
            app.Shot(path.Replace(".png", "-3d.png"), Draw, main, frames: 4);

            app.SelectTab(2);
            var p = App.Get<Posegraph.Backend.RetargetParams>(app.Main, "_params");
            p.RootRotation[1] = 30;
            p.RootOffset[1] = 0.2f;
            app.Shot(path.Replace(".png", "-tuning.png"), Draw, main, frames: 4);

            // 面部表情：单独开一个窗口画调整页里的那一段（折叠头默认收起）
            var drawFace = typeof(Posegraph.UI.MainWindow).GetMethod("DrawFaceSection",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            void DrawFace()
            {
                ImGui.SetNextWindowPos(new Vector2(40, 40), ImGuiCond.Always);
                ImGui.SetNextWindowSize(new Vector2(420, 420), ImGuiCond.Always);
                ImGui.Begin("face-check");
                ImGui.SetNextItemOpen(true);
                drawFace.Invoke(app.Main, null);
                ImGui.End();
            }
            var until = DateTime.UtcNow.AddSeconds(30);
            while (App.Get<Posegraph.Backend.FaceResult?>(app.Main, "_face") is null && App.Get<string?>(app.Main, "_faceError") is null
                   && DateTime.UtcNow < until)
            {
                Host.Current.Frame(DrawFace);
                Thread.Sleep(50);
            }
            var face = App.Get<Posegraph.Backend.FaceResult?>(app.Main, "_face");
            Console.WriteLine($"面部：{(face is null ? App.Get<string?>(app.Main, "_faceError") : $"置信度 {face.Confidence:0.00} used={face.Used} {face.Reason}")}");
            app.Shot(path.Replace(".png", "-face.png"), DrawFace, ["face-check"], frames: 4);

            app.Main.StartPainting();
            Until(Draw, () => app.Main.Paint is not null, 60, "打开涂人窗口");
            app.Masks.IsOpen = true;
            for (var i = 0; i < 8; i++) Host.Current.Frame(Draw);
            app.Shot(path.Replace(".png", "-paint.png"), Draw, [app.Masks.WindowName], frames: 4);
        };
    }

    private static void WaitRetarget(App app, Action draw)
    {
        try
        {
            Until(draw, () => !App.Get<bool>(app.Main, "_rtBusy") && App.Get<object?>(app.Main, "_result") is not null, 60, "重定向");
        }
        catch (TimeoutException ex)
        {
            Console.WriteLine($"{ex.Message}；主窗口状态：{App.Get<string>(app.Main, "_statusText")}");
        }
    }

    private static void Until(Action draw, Func<bool> done, int seconds, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"等待{what}超时");
            Host.Current.Frame(draw);
            Thread.Sleep(50);
        }
    }
}
