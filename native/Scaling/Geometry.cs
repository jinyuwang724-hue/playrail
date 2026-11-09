namespace ClawScaling;

public record Settings(bool Enabled = false, string Mode = "auto", string Filter = "linear", float Sharpness = 0.5f, long Target = 0);
public record Layout(int Width, int Height, int Left, int Top);

public static class Geometry
{
    public static readonly string[] Modes = ["auto", "integer", "fit", "stretch", "fill"];
    public static readonly string[] Filters = ["linear", "nearest", "fsr", "nis"];
    public static Settings Validate(Settings value)
    {
        if (!Modes.Contains(value.Mode) || !Filters.Contains(value.Filter) || !float.IsFinite(value.Sharpness)
            || value.Sharpness < 0 || value.Sharpness > 1 || value.Target < 0)
            throw new ArgumentException("缩放设置无效");
        return value;
    }
    public static Layout Calculate(int width, int height, int screenWidth, int screenHeight, string mode)
    {
        if (Math.Min(Math.Min(width, height), Math.Min(screenWidth, screenHeight)) <= 0 || !Modes.Contains(mode))
            throw new ArgumentException("画面尺寸或模式无效");
        double factor = Math.Min((double)screenWidth / width, (double)screenHeight / height);
        if (mode == "auto") factor = Math.Min(factor, 2);
        if (mode == "integer" && factor > 1) factor = Math.Floor(factor);
        if (mode == "fill") factor = Math.Max((double)screenWidth / width, (double)screenHeight / height);
        int w = mode == "stretch" ? screenWidth : Math.Max(1, (int)Math.Round(width * factor));
        int h = mode == "stretch" ? screenHeight : Math.Max(1, (int)Math.Round(height * factor));
        if (w > 16384 || h > 16384) throw new ArgumentException("缩放尺寸超过 GPU 支持范围");
        return new(w, h, (screenWidth - w) / 2, (screenHeight - h) / 2);
    }
    public static (int X, int Y) MapPoint(Layout layout, int x, int y, int width, int height) =>
        (Math.Clamp((int)((long)(x - layout.Left) * width / layout.Width), 0, width - 1),
         Math.Clamp((int)((long)(y - layout.Top) * height / layout.Height), 0, height - 1));
}
