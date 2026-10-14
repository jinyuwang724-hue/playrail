// --export-web=<文件>：把原生版此刻用的游戏库（同样的顺序、上次游玩、封面套件）写成网页版的 window.__LIBRARY__，
// 给对比截图用（tools/web-preview.mjs 加载它，封面也从同一个套件目录取）。
using System.Text;
using System.Text.Json;

namespace ClawLibrary.Data;

public static class WebExport
{
    public static void Write(Library lib, string path)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartArray();
            foreach (var g in lib.Games)
            {
                w.WriteStartObject();
                w.WriteString("id", g.Id);
                w.WriteString("title", g.Title);
                w.WriteString("kicker", g.Kicker ?? "");
                w.WriteString("main", g.Main ?? g.Title);
                w.WriteString("tail", g.Tail ?? "");
                w.WriteString("sub", g.Subtitle);
                w.WriteString("platform", g.Platform ?? "other");
                w.WriteBoolean("gal", g.Genre == "Galgame");
                if (g.Series != null) w.WriteString("series", g.Series); else w.WriteNull("series");
                if (g.LastPlayed is long l) w.WriteNumber("last", l); else w.WriteNull("last");
                w.WriteStartObject("art");
                w.WriteString("base", $"/art/{g.Id}/");
                if (g.OwnArt is { } a)
                {
                    foreach (var k in "pvnhsx") if (a.Has(k)) w.WriteBoolean(k.ToString(), true);
                    if (a.Fx is double fx) w.WriteNumber("fx", fx);
                    if (a.Ha is double ha) w.WriteNumber("ha", ha);
                    if (a.C != null) w.WriteString("c", a.C);
                }
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        File.WriteAllText(path, "window.__LIBRARY__ = " + Encoding.UTF8.GetString(ms.ToArray()) + ";\n", new UTF8Encoding(false));
        Log.Info($"web export: {path} ({lib.Games.Count} games)");
    }
}
