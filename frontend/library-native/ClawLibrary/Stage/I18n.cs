// 界面语言：代码里的文字都是中文原文，英文界面在排版时（TextKit.Layout）按对照表 Assets\Lang\en.tsv 换掉。
// 对照表一行一条「中文<Tab>英文」；带 {0} {1} 的是模板，中文里的占位对应英文里同号的占位。
// 查不到的原样显示（游戏名、后台传来的文字）。
using System.Text.RegularExpressions;

namespace ClawLibrary.Stage;

public static class I18n
{
    /// <summary>当前是不是英文界面；启动时按 ui.ini 的 lang（空 = 跟系统）定，切换语言后重启生效。</summary>
    public static bool En { get; private set; }

    private static Dictionary<string, string>? _exact;
    private static List<(Regex Pattern, string Template)>? _patterns;
    private static readonly Dictionary<string, string> Memo = new();
    private static readonly object Gate = new();

    /// <summary>启动时的语言：已画好的界面是这个语言；引导里换了语言要和它比，决定要不要重启。</summary>
    public static bool BootEn { get; private set; }
    private static bool _inited;

    public static void Init(string lang)
    {
        En = lang == "en-US" || (lang.Length == 0 && !System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
        if (!_inited) { BootEn = En; _inited = true; }
        lock (Gate) Memo.Clear();
    }

    private static void Load()
    {
        _exact = new Dictionary<string, string>(StringComparer.Ordinal);
        _patterns = new List<(Regex, string)>();
        try
        {
            foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Assets", "Lang", "en.tsv")))
            {
                int tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                string zh = line[..tab].Replace("\\n", "\n"), en = line[(tab + 1)..].Replace("\\n", "\n");
                if (!zh.Contains("{0}")) { _exact.TryAdd(zh, en); continue; }
                // 模板：占位变成懒惰捕获，整句锚定
                string rx = "^" + Regex.Replace(Regex.Escape(zh), @"\\\{(\d+)\}", m => $"(?<p{m.Groups[1].Value}>.+?)") + "$";
                _patterns.Add((new Regex(rx, RegexOptions.Singleline | RegexOptions.CultureInvariant), en));
            }
        }
        catch (Exception ex) { Log.Warn($"i18n: {ex.Message}"); }
    }

    private static bool HasCjk(string s)
    {
        foreach (char c in s) if (c >= '　' && c <= '鿿' || c >= '＀' && c <= '￯') return true;
        return false;
    }

    /// <summary>英文界面时把一段中文界面文字换成英文；中文界面、没有中文、查不到时原样返回。</summary>
    public static string T(string s)
    {
        if (!En || string.IsNullOrEmpty(s) || !HasCjk(s)) return s;
        lock (Gate)
        {
            if (Memo.TryGetValue(s, out var hit)) return hit;
            if (_exact == null) Load();
            string r = Translate(s, 0);
            if (Memo.Count > 4000) Memo.Clear();
            Memo[s] = r;
            return r;
        }
    }

    private static string Translate(string s, int depth)
    {
        if (_exact!.TryGetValue(s, out var en)) return en;
        if (depth < 3)
        {
            foreach (var (pattern, template) in _patterns!)
            {
                var m = pattern.Match(s);
                if (!m.Success) continue;
                return Regex.Replace(template, @"\{(\d+)\}", t =>
                {
                    var g = m.Groups["p" + t.Groups[1].Value];
                    return g.Success ? Translate(g.Value, depth + 1) : t.Value;
                });
            }
            // 拼起来的「A · B」：分段查
            if (s.Contains(" · "))
            {
                var parts = s.Split(" · ");
                if (parts.Length > 1) return string.Join(" · ", parts.Select(p => Translate(p, depth + 1)));
            }
            // 前后空白、「：」结尾这类拼接残片
            string trimmed = s.Trim();
            if (trimmed.Length != s.Length && _exact.TryGetValue(trimmed, out var t2)) return s.Replace(trimmed, t2);
        }
        return s;
    }
}
