// 线性图标：和 Game Bar 小组件同一套 24 × 24 描边图标（1.5 粗、圆头圆角），代替 Segoe Fluent Icons 的字形。
// 键还是原来的字形码，调用处只把「排字再画」换成 Draw；没收录的字形返回 false，调用处照旧用图标字体画。
using System.Collections.Generic;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Svg;
using Windows.Foundation;
using Windows.UI;

namespace ClawLibrary.Stage;

public static class LineIcons
{
    private static readonly Dictionary<string, string> Paths = new()
    {
        [""] = "M10.5,4 A6.5,6.5 0 1 0 10.5,17 A6.5,6.5 0 1 0 10.5,4 Z M15.5 15.5 20 20",                    // 搜索
        [""] = "M6.5 6.5l11 11M17.5 6.5l-11 11",                                                              // 关闭
        [""] = "M12 5v14M5 12h14",                                                                            // 添加
        [""] = "M4 7h9M17 7h3M4 17h3M11 17h9 M13,7 A2,2 0 1 0 17,7 A2,2 0 1 0 13,7 Z M7,17 A2,2 0 1 0 11,17 A2,2 0 1 0 7,17 Z", // 设置
        [""] = "M4 5h16l-6 7v6l-4 2v-8z", // 筛选
        [""] = "M5 5h14v15H5zM5 9h14M8 3v4M16 3v4M8 12h.01M12 12h.01M16 12h.01M8 16h.01M12 16h.01M16 16h.01", // 日历
        [""] = "M3 5h18v12H3zM12 17v4M8 21h8M10 8l5 3-5 3z", // 放映
        [""] = "M5 3h14v18H5zM7 5h10v11H7zM7 13l3-3 4 4 3-2M8 19h8", // 相片
        [""] = "M13 2.5 4.5 14H11l-1 7.5L18.5 10H12z",                                                        // 闪电（性能、充电）
        [""] = "M12 3v8 M6.3 6.8a8 8 0 1 0 11.4 0",                                                           // 电源 / 关机
        [""] = "M3.5 7a2 2 0 0 1 2-2h4l2 2.5h7a2 2 0 0 1 2 2V17a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2z",           // 文件夹
        [""] = "M3.5,12 A8.5,8.5 0 1 0 20.5,12 A8.5,8.5 0 1 0 3.5,12 Z M3.5 12h17 M12 3.5c2.4 2.6 3.4 5.4 3.4 8.5s-1 5.9-3.4 8.5c-2.4-2.6-3.4-5.4-3.4-8.5s1-5.9 3.4-8.5z", // 地球（浏览器）
        [""] = "M4 8V6a2 2 0 0 1 2-2h2M16 4h2a2 2 0 0 1 2 2v2M20 16v2a2 2 0 0 1-2 2h-2M8 20H6a2 2 0 0 1-2-2v-2 M9,12 A3,3 0 1 0 15,12 A3,3 0 1 0 9,12 Z", // 截图
        [""] = "M20 14.5A8 8 0 1 1 9.5 4a6.5 6.5 0 0 0 10.5 10.5z",                                           // 睡眠
        [""] = "M20 12a8 8 0 1 1-2.3-5.7 M20 4v4h-4",                                                         // 重启
        ["❄"] = "M12 3v18M4.2 7.5l15.6 9M4.2 16.5l15.6-9M9.8 4.3 12 6l2.2-1.7M9.8 19.7 12 18l2.2 1.7",            // 休眠
        [""] = "M8,8 A4,4 0 1 0 16,8 A4,4 0 1 0 8,8 Z M4.5 20a7.5 7.5 0 0 1 15 0",                           // 联系人
        [""] = "M9.5 6l6 6-6 6",                                                                              // ›
        [""] = "M5,4 H8 A1,1 0 0 1 9,5 V19 A1,1 0 0 1 8,20 H5 A1,1 0 0 1 4,19 V5 A1,1 0 0 1 5,4 Z M11,4 H14 A1,1 0 0 1 15,5 V19 A1,1 0 0 1 14,20 H11 A1,1 0 0 1 10,19 V5 A1,1 0 0 1 11,4 Z M16.6 5.4l2.8-.8 3.1 14.4-2.8.8z", // 游戏库
        [""] = "M7.5 7h9a4.5 4.5 0 0 1 4.4 5.4l-.9 4.6a2.5 2.5 0 0 1-4.3 1.2L13.6 16h-3.2l-2.1 2.2A2.5 2.5 0 0 1 4 17l-.9-4.6A4.5 4.5 0 0 1 7.5 7z M7.5 10v3.5M5.75 11.75h3.5 M15.5 10.5h.01M17.5 12.5h.01", // 手柄
        [""] = "M5,4 H19 A2,2 0 0 1 21,6 V14 A2,2 0 0 1 19,16 H5 A2,2 0 0 1 3,14 V6 A2,2 0 0 1 5,4 Z M8 20h8M12 16v4", // 显示器
        [""] = "M8,6 H16 A2,2 0 0 1 18,8 V16 A2,2 0 0 1 16,18 H8 A2,2 0 0 1 6,16 V8 A2,2 0 0 1 8,6 Z M9 3v3M15 3v3M9 18v3M15 18v3M3 9h3M3 15h3M18 9h3M18 15h3", // 系统（芯片）
        [""] ="M5,9 H15 A2,2 0 0 1 17,11 V18 A2,2 0 0 1 15,20 H5 A2,2 0 0 1 3,18 V11 A2,2 0 0 1 5,9 Z M7,9 V6 A2,2 0 0 1 9,4 H19 A2,2 0 0 1 21,6 V13 A2,2 0 0 1 19,15 H17", // 窗口（任务切换）
        [""] = "M12,10.4 A1.6,1.6 0 1 0 12,13.6 A1.6,1.6 0 1 0 12,10.4 Z M12 10.4c-.5-3.4.6-6.4 3-6.4 2.1 0 2.4 3.2-1.8 6.6 M13.6 12c3.4-.5 6.4.6 6.4 3 0 2.1-3.2 2.4-6.6-1.8 M12 13.6c.5 3.4-.6 6.4-3 6.4-2.1 0-2.4-3.2 1.8-6.6 M10.4 12c-3.4.5-6.4-.6-6.4-3 0-2.1 3.2-2.4 6.6 1.8", // 风扇
        [""] = "M20 12a8 8 0 0 1-14.5 4.6 M4 12a8 8 0 0 1 14.5-4.6 M18.5 3.5v4h-4 M5.5 20.5v-4h4", // 刷新率
        [""] = "M8,3.5 H16 A1.5,1.5 0 0 1 17.5,5 V19 A1.5,1.5 0 0 1 16,20.5 H8 A1.5,1.5 0 0 1 6.5,19 V5 A1.5,1.5 0 0 1 8,3.5 Z M3.5 9v6 M20.5 9v6", // 震动
        [""] = "M2.5 9a14 14 0 0 1 19 0 M5.5 12.5a9.5 9.5 0 0 1 13 0 M8.6 16a5 5 0 0 1 6.8 0 M12 19.5h.01", // Wi-Fi
        [""] = "M6.5 7.5l11 9-5.5 4.5V3l5.5 4.5-11 9", // 蓝牙
        [""] = "M4,6 H20 A2,2 0 0 1 22,8 V16 A2,2 0 0 1 20,18 H4 A2,2 0 0 1 2,16 V8 A2,2 0 0 1 4,6 Z M6 10h.01M10 10h.01M14 10h.01M18 10h.01M8 14.2h8", // 键盘
    };

    /// <summary>快捷面板用到、图标字体里没有对应字形的几枚（按名字取）。</summary>
    static LineIcons()
    {
        Paths["qs.sun"] = "M12,8 A4,4 0 1 0 12,16 A4,4 0 1 0 12,8 Z M12 2.5v2M12 19.5v2M2.5 12h2M19.5 12h2M5.3 5.3l1.4 1.4M17.3 17.3l1.4 1.4M5.3 18.7l1.4-1.4M17.3 6.7l1.4-1.4";
        Paths["qs.vol"] = "M4 9.5h3.5L12 5v14l-4.5-4.5H4z M15.5 8.8a4.6 4.6 0 0 1 0 6.4 M18.2 6.2a8.3 8.3 0 0 1 0 11.6";
        Paths["qs.mute"] = "M4 9.5h3.5L12 5v14l-4.5-4.5H4z M16 9.5l5 5M21 9.5l-5 5";
        Paths["qs.osd"] = "M5,5 H19 A2,2 0 0 1 21,7 V17 A2,2 0 0 1 19,19 H5 A2,2 0 0 1 3,17 V7 A2,2 0 0 1 5,5 Z M6.5 13h2.5l2-4.5 2.5 7 1.8-3.5h2.2";
        Paths["qs.fps"] = "M12,5 A8,8 0 1 0 12,21 A8,8 0 1 0 12,5 Z M12 13l3.2-3.2 M10 2.5h4";
        Paths["qs.magpie"] = "M4 9V4h5 M20 9V4h-5 M4 15v5h5 M20 15v5h-5 M9 12h6";
        Paths["qs.duck"] = "M8.5 11.2a4.5 4.5 0 1 1 8.4 2.2l3.1.8-2.9 1.6C16 18.6 13.8 20 11 20c-3.9 0-6.5-2.3-6.5-5.2 0-1.8 1.4-3.1 4-3.6 M12.3 8.8h.01";
        Paths["qs.pencil"] = "M4 20h4L19 9l-4-4L4 16z M14 6l4 4";
        Paths["qs.left"] = "M14.5 6l-6 6 6 6";
        Paths["qs.exit"] = "M14 4h4a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-4 M10 8l-4 4 4 4 M6 12h10";
        // 关机（实心，画的时候 fill: true），和电源触发图标区分
        Paths["qs.powerSolid"] = "M12 2a1.6 1.6 0 0 1 1.6 1.6v7.2a1.6 1.6 0 0 1-3.2 0V3.6A1.6 1.6 0 0 1 12 2z M6.2 5.6a1.5 1.5 0 0 1 .2 2.1A7 7 0 1 0 17.6 7.7a1.5 1.5 0 1 1 2.3-1.9A10 10 0 1 1 4.1 5.8a1.5 1.5 0 0 1 2.1-.2z";
        // 快捷面板（QAM 定稿 2026-11-05）的一套：照设计稿 docs/qam-final-20261105.html 原样
        Paths["qam.bolt"] = "M13 3L5 13.5h6L10 21l8-10.5h-6z";
        Paths["qam.sun"] = "M12 8.5a3.5 3.5 0 1 0 0 7a3.5 3.5 0 1 0 0-7z M12 3v1.5 M12 19.5V21 M3 12h1.5 M19.5 12H21 M5.6 5.6l1.1 1.1 M17.3 17.3l1.1 1.1 M5.6 18.4l1.1-1.1 M17.3 6.7l1.1-1.1";
        Paths["qam.vol"] = "M4 10h3l5-4v12l-5-4H4z M16 9.5a3.5 3.5 0 0 1 0 5 M18.5 7a7 7 0 0 1 0 10";
        Paths["qam.mute"] = "M4 10h3l5-4v12l-5-4H4z M16 9.5l5 5 M21 9.5l-5 5";
        Paths["qam.shot"] = "M4 8V5.5A1.5 1.5 0 0 1 5.5 4H8 M16 4h2.5A1.5 1.5 0 0 1 20 5.5V8 M20 16v2.5a1.5 1.5 0 0 1-1.5 1.5H16 M8 20H5.5A1.5 1.5 0 0 1 4 18.5V16 M12 9.5a2.5 2.5 0 1 0 0 5a2.5 2.5 0 1 0 0-5z";
        Paths["qam.win"] = "M4 9h11v11H4z M9 4h11v11";
        Paths["qam.kbd"] = "M3 6.5h18v11H3z M7 10.5h.01 M11 10.5h.01 M15 10.5h.01 M8 14h8";
        Paths["qam.desk"] = "M3 5h18v11H3z M9 20h6 M12 16v4";
        Paths["qam.sleep"] = "M19 14.5A7.5 7.5 0 1 1 9.5 5a6 6 0 0 0 9.5 9.5z";
        Paths["qam.restart"] = "M4 12a8 8 0 1 0 2.4-5.7 M4 4v4.5h4.5";
        Paths["qam.off"] = "M12 3v8 M6.3 6.8a8 8 0 1 0 11.4 0";
        Paths["qam.wifi"] = "M2.5 9a14 14 0 0 1 19 0 M5.5 12.5a9.5 9.5 0 0 1 13 0 M8.7 16a5 5 0 0 1 6.6 0 M12 19.5h.01";
        Paths["qam.bt"] = "M7 7l10 10-5 4V3l5 4L7 17";
        Paths["qam.pad"] = "M7 9h3 M8.5 7.5v3 M15 8.5h.01 M17 10.5h.01 M6.5 5h11a4 4 0 0 1 4 4.2l-.6 6.3a2.8 2.8 0 0 1-5 1.4L14.3 15H9.7l-1.6 1.9a2.8 2.8 0 0 1-5-1.4l-.6-6.3A4 4 0 0 1 6.5 5z";
        Paths["qam.mouse"] = "M12 3a6 6 0 0 1 6 6v6a6 6 0 0 1-12 0V9a6 6 0 0 1 6-6z M12 7v3";
        Paths["qam.air"] = "M10.5 4.5c0-1 .7-2 1.5-2s1.5 1 1.5 2v5l7 4v2l-7-2v4l2 1.5v1.5L12 20l-3.5 1v-1.5l2-1.5v-4l-7 2v-2l7-4z";
        Paths["qam.lib"] = "M5 4h4v16H5z M11 4h4v16h-4z M17 5l3 .6-2.6 14.4-3-.6";
        Paths["qam.quit"] = "M7 7l10 10 M17 7L7 17";
        Paths["qam.l"] = "M15 5l-7 7 7 7";
        Paths["qam.r"] = "M9 5l7 7-7 7";
        Paths["qs.mode"] ="M5,4 H19 A2,2 0 0 1 21,6 V14 A2,2 0 0 1 19,16 H5 A2,2 0 0 1 3,14 V6 A2,2 0 0 1 5,4 Z M8 20h8M12 16v4";
    }

    private static readonly Dictionary<(string, bool, float), CanvasSvgDocument> Docs = new();
    private static CanvasDevice? _device;

    public static bool Has(string glyph) => Paths.ContainsKey(glyph);

    /// <summary>
    /// 把图标画在以 (cx, cy) 为中心、边长 size × 1.15 的方框里（和同字号的图标字体墨迹差不多大）。
    /// fill：实心（电池里的小闪电太小，描边看不清）。stroke：24 格里的线宽。
    /// </summary>
    public static bool Draw(CanvasDrawingSession ds, string glyph, float cx, float cy, float size, Color c, bool fill = false, float stroke = 1.5f)
    {
        if (!Paths.TryGetValue(glyph, out var d)) return false;
        if (!ReferenceEquals(_device, ds.Device)) { Docs.Clear(); _device = ds.Device; }
        if (!Docs.TryGetValue((glyph, fill, stroke), out var doc))
        {
            string sw = stroke.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            string paint = fill ? "fill='#fff' stroke='none'" : $"fill='none' stroke='#fff' stroke-width='{sw}' stroke-linecap='round' stroke-linejoin='round'";
            doc = CanvasSvgDocument.LoadFromXml(ds.Device,
                $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' {paint}><path d='{d}'/></svg>");
            Docs[(glyph, fill, stroke)] = doc;
        }
        doc.Root.SetColorAttribute(fill ? "fill" : "stroke", c);
        float box = size * 1.15f;
        ds.DrawSvg(doc, new Size(box, box), cx - box / 2, cy - box / 2);
        return true;
    }
}
