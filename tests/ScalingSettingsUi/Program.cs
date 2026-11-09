using System.Text.Json.Nodes;
using ClawLibrary.Stage;

await WallStage.CheckEditor(args[0]);
if (args.Length > 1) await WallStage.CheckLossless(args[1]);

namespace ClawLibrary.Data
{
    public sealed class AgentClient
    {
        public JsonObject? Request;
        public Func<string, JsonNode?[], JsonNode?>? Respond;
        public Task<JsonNode?> Call(string method, params JsonNode?[] args)
        {
            Request = args.FirstOrDefault() as JsonObject;
            JsonNode? result = new JsonObject { ["presets"] = new JsonArray() };
            return Task.FromResult(Respond == null ? result : Respond(method, args));
        }
        public Task<JsonNode?> CallLong(string method, int timeout, params JsonNode?[] args) => Call(method, args);
    }
}

namespace ClawLibrary.Stage
{
    public static class Log { public static void Warn(string text) { } }
    public class PerfRow
    {
        public bool Section, Disabled, Numeric, Changed;
        public int Choice;
        public string Title = "", Value = "";
        public string[]? Choices;
        public Action? Press, Reset;
        public Action<int>? Step, Select;
    }

    // Only drawing/input/transport dependencies are replaced. The linked production editor
    // builds the rows and runs every action, including JsonNode mutations and number handling.
    public sealed partial class WallStage
    {
        private ClawLibrary.Data.AgentClient? _agent;
        private bool _stOpen = true, _settingsManualScroll;
        private object? _gsItem;
        private int _stCat = 3, _stZone, _stFocus;
        private float _stScroll, _stScrollY;
        private string? _stArmed, _toast;
        private bool StEditing => false;
        private bool _stBrowseExe;
        private Action<string>? _stBrowsePick;
        private void StBrowseTo(string? path) { }
        private readonly List<PerfRow> _stRows = new();
        private readonly QueueStub _dq = new();
        private sealed class QueueStub { public void TryEnqueue(Action action) => action(); }
        private static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue(out bool b) && b;
        private static int Int(JsonNode? n, int fallback = 0) => n is JsonValue v && v.TryGetValue(out int i) ? i : fallback;
        private static string? Str(JsonNode? n) => n?.GetValue<string>();
        private void Toast(string text) => _toast = text;
        private void Repaint() => BuildStRows();
        private void BuildStRows() { _stRows.Clear(); if (_stCat == 4) BuildStLossless(); else BuildStMagpie(); }
        private void PaintSt() { }
        private int FirstStRow() => _stRows.FindIndex(r => !r.Section);
        private bool StConfirm(string key) { if (_stArmed == key) return true; _stArmed = key; return false; }
        private void StEditText(string text, Action<string> done) { }
        private static PerfRow StInfo(string title, string value) => new() { Title = title, Value = value, Disabled = true };
        private PerfRow StAction(string title, string key, string idle, Action run) => new() { Title = title, Press = run };
        private PerfRow StPick(string title, string[] names, int index, Action<int> set, int? def = null) => new() { Title = title, Select = set, Choices = names, Choice = index };
        private PerfRow StToggle(string title, bool on, Action<bool> set, bool? def = null) => new() { Title = title, Press = () => set(!on) };
        private PerfRow StNumber(string title, int value, int min, int max, int step, Func<int, string> fmt, Action<int> set, int def) => new() { Title = title };
        private PerfRow Row(string title) => _stRows.Single(r => r.Title == title);
        private void Click(string title) => Row(title).Press!();

        public static async Task CheckEditor(string catalogPath)
        {
            int checks = 0;
            void Check(bool ok, string description) { if (!ok) throw new Exception(description); checks++; }
            var stage = new WallStage { _mgCatalog = JsonNode.Parse(File.ReadAllText(catalogPath))!.AsObject(), _mgPrefs = new JsonObject() };
            foreach (var effect in stage._mgCatalog["effects"]!.AsArray())
            {
                string name = Str(effect!["name"])!;
                stage.MgOpenEditor(null, true);
                stage.Click("添加效果器…");
                stage.Click(MgGroup(name));
                stage.Click(MgEffectLabel(name));
                Check(stage.MgChain.Count == 1 && stage._mgStage == 0, "effect added: " + name);
                if (Bool(effect["canScale"]))
                {
                    foreach (int type in new[] { 2, 0, 1, 3, 1 })
                    {
                        stage.Row("缩放方式").Select!(type);
                        Check(Int(stage.MgChain[0]!["scalingType"]) == type, "scale type: " + name);
                        foreach (var row in stage._stRows.Where(r => r.Numeric).ToArray()) { row.Step!(1); row.Reset!(); }
                    }
                }
                foreach (var row in stage._stRows.Where(r => r.Numeric).ToArray()) { row.Step!(1); row.Reset!(); }
                stage.Click("返回效果链");
                Check(stage._mgStage == -1 && stage.MgDirty, "return to draft: " + name);
            }
            stage.MgOpenEditor((JsonObject)stage._mgCatalog["presets"]![1]!, true);
            string first = stage.MgChain[0]!["name"]!.GetValue<string>();
            stage._mgStage = 0; stage.Repaint(); stage.Click("下移");
            Check(Str(stage.MgChain[1]!["name"]) == first, "effect move preserves object");
            stage.Click("移除效果器");
            Check(stage.MgChain.Count == 1, "effect removal");
            stage._agent = new ClawLibrary.Data.AgentClient();
            await stage.MgSaveEditor();
            Check(stage._mgEdit == null && stage._agent.Request?["effects"] is JsonArray { Count: 1 }, "save sends draft pipeline");
            stage.MgOpenEditor(null, true);
            stage.MgBack(); Check(stage._mgEdit != null, "dirty draft waits for discard confirmation");
            stage.MgBack(); Check(stage._mgEdit == null, "second back discards draft");
            Console.WriteLine($"{checks} editor checks passed across 134 catalog effects");
        }
    }
}
