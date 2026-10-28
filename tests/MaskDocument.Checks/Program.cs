using System.Numerics;
using Posegraph.Backend;
using Posegraph.UI;

// 合并模式涂人（MaskDocument）：画笔 / 橡皮擦 / 魔棒、两层涂抹合起来的范围、加删图层、交给识别的遮罩、存取、撤销。
var checks = 0;
Run("brush assigns pixels to the layer; eraser only clears that layer", BrushAndEraser);
Run("box covers visible + hidden paint, padded, ignoring stray specks", BoxFromBothPlanes);
Run("removing a layer renumbers the people after it", RemoveLayer);
Run("masks for recognition keep only chosen people, renumbered in order", MasksForChosen);
Run("save / load round trip; size mismatch is rejected", SaveLoad);
Run("regenerating visible paint maps session people to layers and keeps hidden paint", ReplaceVisible);
Run("undo / redo restore both planes", UndoRedo);
Run("magic wand selects the connected area of similar color", Wand);
Console.WriteLine($"{checks} mask document checks passed.");

void Run(string name, Action check)
{
    check();
    checks++;
    Console.WriteLine($"PASS {name}");
}

static void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static MaskDocument Doc(int w = 100, int h = 80, int count = 2) => new(w, h, new byte[w * h * 3], count);

static void BrushAndEraser()
{
    var doc = Doc();
    doc.Stroke(new Vector2(10, 10), new Vector2(30, 10), 3, 0, erase: false);
    Check(doc.Visible[10 * 100 + 20] == 1, "brush paints layer 1");
    doc.Stroke(new Vector2(20, 10), new Vector2(20, 10), 3, 1, erase: false);
    Check(doc.Visible[10 * 100 + 20] == 2, "brush takes pixels from other layers");
    doc.Stroke(new Vector2(10, 10), new Vector2(30, 10), 5, 0, erase: true);
    Check(doc.Visible[10 * 100 + 20] == 2, "eraser leaves other layers alone");
    Check(doc.Visible[10 * 100 + 12] == 0, "eraser clears its own layer");
    doc.Stroke(new Vector2(50, 50), new Vector2(50, 50), 4, 0, erase: false, hidden: true);
    Check(doc.Hidden[50 * 100 + 50] == 1 && doc.Visible[50 * 100 + 50] == 0, "hidden brush paints only the hidden plane");
}

static void BoxFromBothPlanes()
{
    var doc = Doc();
    doc.Stroke(new Vector2(20, 10), new Vector2(40, 30), 5, 0, erase: false);
    var visibleOnly = doc.Box(0, [1000, 800])!;
    doc.Stroke(new Vector2(30, 30), new Vector2(30, 75), 5, 0, erase: false, hidden: true);
    var both = doc.Box(0, [1000, 800])!;
    Check(both[3] > visibleOnly[3] + 300, $"hidden paint extends the box down ({visibleOnly[3]} → {both[3]})");
    Check(both[1] < 10 * 10 && both[0] < 15 * 10, "box is padded beyond the painted area");
    doc.Stroke(new Vector2(95, 2), new Vector2(95, 2), 0.6f, 0, erase: false);   // 一个零碎的点
    Check(doc.Box(0, [1000, 800])![2] < 900, "a stray speck does not stretch the box");
    Check(doc.Box(1, [1000, 800]) is null, "an unpainted layer has no box");
}

static void RemoveLayer()
{
    var doc = Doc(count: 3);
    doc.Stroke(new Vector2(10, 10), new Vector2(10, 10), 2, 0, false);
    doc.Stroke(new Vector2(30, 10), new Vector2(30, 10), 2, 1, false);
    doc.Stroke(new Vector2(50, 10), new Vector2(50, 10), 2, 2, false, hidden: true);
    doc.RemoveLayer(1);
    Check(doc.Count == 2, "count drops");
    Check(doc.Visible[10 * 100 + 10] == 1, "earlier layer unchanged");
    Check(doc.Visible[10 * 100 + 30] == 0, "removed layer cleared");
    Check(doc.Hidden[10 * 100 + 50] == 2, "later layer moves up");
    Check(!doc.CanUndo, "undo history cleared");
}

static void MasksForChosen()
{
    var doc = Doc(count: 3);
    doc.Stroke(new Vector2(10, 10), new Vector2(10, 10), 2, 0, false);
    doc.Stroke(new Vector2(30, 10), new Vector2(30, 10), 2, 1, false);
    doc.Stroke(new Vector2(50, 10), new Vector2(50, 10), 2, 2, false);
    doc.Stroke(new Vector2(70, 10), new Vector2(70, 10), 2, 2, false, hidden: true);
    var data = doc.ToData([2, 0]);
    Check(data.Count == 2 && data.Width == 100, "two people sent");
    Check(data.Labels[10 * 100 + 50] == 1 && data.Labels[10 * 100 + 10] == 2, "renumbered in chosen order");
    Check(data.Labels[10 * 100 + 30] == 0, "unchosen people are not sent");
    Check(data.Labels[10 * 100 + 70] == 0, "hidden paint is not part of the mask");
}

static void SaveLoad()
{
    var doc = Doc();
    doc.Stroke(new Vector2(10, 10), new Vector2(60, 40), 4, 1, false);
    doc.Stroke(new Vector2(20, 60), new Vector2(20, 70), 4, 0, false, hidden: true);
    var rgb = new byte[100 * 80 * 3];
    var back = MaskDocument.Load(doc.Save(), 100, 80, rgb)!;
    Check(back.Count == 2 && back.Visible.SequenceEqual(doc.Visible) && back.Hidden.SequenceEqual(doc.Hidden), "round trip");
    Check(MaskDocument.Load(doc.Save(), 120, 80, rgb) is null, "different photo size rejected");
    Check(MaskDocument.Load([1, 2, 3], 100, 80, rgb) is null, "garbage rejected");
}

static void ReplaceVisible()
{
    var doc = Doc(count: 3);
    doc.Stroke(new Vector2(70, 70), new Vector2(70, 70), 3, 1, false, hidden: true);
    var labels = new byte[100 * 80];
    labels[5] = 1;   // 会话里第 0 个人
    labels[6] = 2;   // 会话里第 1 个人
    doc.ReplaceVisible(labels, [2, 0]);
    Check(doc.Visible[5] == 3 && doc.Visible[6] == 1, "session people mapped to their layers");
    Check(doc.Hidden[70 * 100 + 70] == 2, "hidden paint kept");
    doc.Undo();
    Check(doc.Visible[5] == 0, "can be undone");
}

static void UndoRedo()
{
    var doc = Doc();
    doc.BeginStep();
    doc.Stroke(new Vector2(10, 10), new Vector2(10, 10), 2, 0, false);
    doc.BeginStep();
    doc.Stroke(new Vector2(40, 40), new Vector2(40, 40), 2, 1, false, hidden: true);
    doc.Undo();
    Check(doc.Hidden[40 * 100 + 40] == 0 && doc.Visible[10 * 100 + 10] == 1, "undo one step");
    doc.Redo();
    Check(doc.Hidden[40 * 100 + 40] == 2, "redo");
}

static void Wand()
{
    const int w = 20, h = 10;
    var rgb = new byte[w * h * 3];
    for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var bright = x < 8 || x >= 12 ? (byte)200 : (byte)20;   // 中间一条暗色竖带隔开左右两块亮区
            rgb[(y * w + x) * 3] = rgb[(y * w + x) * 3 + 1] = rgb[(y * w + x) * 3 + 2] = bright;
        }
    var doc = new MaskDocument(w, h, rgb, 2);
    var picked = doc.Wand(2, 2, 30);
    Check(picked.Count == 8 * h, $"left bright area only ({picked.Count})");
    doc.Apply(picked, 1, remove: false);
    Check(doc.Visible[2 * w + 2] == 2 && doc.Visible[2 * w + 15] == 0, "applied to the layer");
    doc.Apply(doc.Wand(2, 2, 30), 1, remove: true);
    Check(doc.Visible[2 * w + 2] == 0, "right click removes it");
}
