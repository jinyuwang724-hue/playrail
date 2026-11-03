namespace ClawLibrary.Stage;

// 卡面标题的持久化标识和设置页文案；D / B 保留旧版本的标识。
public enum TitleStyle { D, B, Folia, Quiet, Editorial }

public static class TitleStyles
{
    public static readonly TitleStyle[] Values = { TitleStyle.D, TitleStyle.B, TitleStyle.Folia, TitleStyle.Quiet, TitleStyle.Editorial };
    public static readonly string[] Names = { "电影字幕", "极简黑体", "Folia", "留白细字", "简约衬线" };

    public static string Name(TitleStyle style)
    {
        int index = Array.IndexOf(Values, style);
        return Names[index >= 0 ? index : 0];
    }

    public static bool IsLeftAligned(TitleStyle style) => style is TitleStyle.B or TitleStyle.Folia or TitleStyle.Quiet;
}
