namespace ClawLibrary.Engine;

/// <summary>屏保的空闲门槛和轮换规则；不依赖窗口、计时器或合成视觉层。</summary>
public static class ScreensaverPolicy
{
    public static bool CanStart(bool enabled, bool foreground, bool blocked, long nowMs, long lastInputMs, int idleSeconds) =>
        enabled && foreground && !blocked && nowMs - lastInputMs >= Math.Max(1, idleSeconds) * 1000L;

    /// <summary>优先换一款游戏，而不是换到同款游戏在无限墙上的另一张封面。</summary>
    public static int PickGame(IReadOnlyList<string> ids, string? previousId, Random random)
    {
        if (ids.Count == 0) return -1;
        var alternatives = new List<int>();
        for (int i = 0; i < ids.Count; i++)
            if (!string.Equals(ids[i], previousId, StringComparison.Ordinal)) alternatives.Add(i);
        return alternatives.Count > 0 ? alternatives[random.Next(alternatives.Count)] : random.Next(ids.Count);
    }
}
