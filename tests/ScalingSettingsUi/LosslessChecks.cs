using System.Text.Json.Nodes;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    public static async Task CheckLossless(string fixture)
    {
        int checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        var response = JsonNode.Parse(File.ReadAllText(fixture))!.AsObject();
        var stage = new WallStage { _stCat = 4 };
        stage.LsReadResponse(response, true); stage.Repaint();
        Check(!stage.LsDirty && stage.Row("保存并应用").Disabled, "loaded values are clean");
        Check(!stage._stRows.Any(r => r.Title.Contains("EXE") || r.Title.Contains("检测")), "setup disappears after detection");
        Check(stage.Row("本机状态").Value.Contains("后台运行中"), "compact running status");
        var values = stage._lsEditor!["profiles"]![0]!["values"]!.AsObject();
        foreach (string type in new[] { "Off", "LS1", "FSR", "NIS", "SGSR", "BicubicCAS", "Anime4K", "XBR", "SharpBilinear", "Integer", "Nearest" })
        {
            values["ScalingType"] = type; stage.Repaint();
            Check(stage._stRows.Any(r => r.Title == "LS1 锐化强度") == (type == "LS1"), "LS1 controls are conditional");
            Check(stage._stRows.Any(r => r.Title == "FSR 模式") == (type == "FSR"), "FSR controls are conditional");
            foreach (var row in stage._stRows.Where(r => r.Numeric).ToArray()) { row.Step!(1); row.Reset!(); }
        }
        values["FrameGeneration"] = "LSFG3"; values["LSFG3Mode1"] = "FIXED"; stage.Repaint();
        stage.Row("帧生成倍率").Step!(1);
        Check(values["LSFG3Multiplier"]!.GetValue<double>() == 2.1, "fractional multiplier increment");
        Check(!stage._stRows.Any(r => r.Title == "目标帧率"), "fixed mode hides adaptive target");
        stage.Row("帧生成模式").Select!(1);
        Check(stage._stRows.Any(r => r.Title == "目标帧率") && !stage._stRows.Any(r => r.Title == "帧生成倍率"), "adaptive target replaces multiplier");
        stage.Row("裁剪输入画面").Press!();
        Check(stage._stRows.Count(r => r.Title.EndsWith("裁剪（像素）")) == 4, "crop controls expand");
        stage.Row("显示帧率").Press!();
        Check(stage.LsDirty && !stage.Row("保存并应用").Disabled, "edits enable apply");
        Check(stage.Row("首选显卡").Choices![0] == "自动", "GPU picker uses device names");

        stage.LsReadResponse(response, true);
        values = stage._lsEditor!["profiles"]![0]!["values"]!.AsObject();
        values["LSFG3Multiplier"] = 3.5;
        stage._lsEditor["globalValues"]!["Hotkey"] = "Ctrl+Shift+F8";
        var request = stage.LsSaveRequest();
        Check(request["profiles"]!.AsArray().Count == 1 && request["profiles"]![0]!["values"]!.AsObject().Count == 1, "only changed native fields are sent");
        Check(request["globalValues"]!.AsObject().Count == 1 && request["remove"]!.AsArray().Count == 0, "global patch and unchanged profiles");
        stage.LsBrowseProfile(stage._lsEditor["profiles"]![0]!.AsObject());
        stage._stBrowsePick!("C:/Games/TestGame.exe");
        request = stage.LsSaveRequest();
        Check(request["profiles"]![1]!["index"] == null && Str(request["profiles"]![1]!["values"]!["Path"]) == "C:/Games/TestGame.exe", "new profile keeps null identity and game path");
        stage.Repaint(); stage.Click("删除当前配置"); stage.Click("删除当前配置");
        Check(stage._lsEditor["profiles"]!.AsArray().Count == 1, "draft profile deletion");
        stage._agent = new ClawLibrary.Data.AgentClient { Respond = (method, args) =>
        {
            if (method == "save_lossless_settings") throw new IOException("设置已在别处修改");
            return response.DeepClone();
        } };
        await stage.LsSave();
        Check(stage.LsDirty && values["LSFG3Multiplier"]!.GetValue<double>() == 3.5, "failed save retains draft");
        stage._agent.Respond = (method, args) => response.DeepClone();
        await stage.LsSave();
        Check(!stage.LsDirty && stage._lsError == null, "successful save accepts authoritative readback");
        stage._lsConfig!["configured"] = false; stage.Repaint();
        Check(stage._stRows.Any(r => r.Title == "选择小黄鸭 EXE…") && !stage._stRows.Any(r => r.Title == "保存并应用"), "missing installation returns setup");
        Console.WriteLine($"{checks} Lossless Scaling editor checks passed");
    }
}
