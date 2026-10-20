// 界面音效：移动、确认、返回各一声很短的音。不带音频文件：启动时按音量合成成 WAV 放在内存里，
// winmm 的 PlaySound 异步播放（新的一声会打断上一声，连按也不会堆起来）。
using System.Runtime.InteropServices;

namespace ClawLibrary.Stage;

public enum Sfx { Move, Confirm, Back }

public static class SoundFx
{
    [DllImport("winmm.dll")] private static extern bool PlaySound(byte[]? sound, nint module, uint flags);
    private const uint SND_ASYNC = 0x1, SND_NODEFAULT = 0x2, SND_MEMORY = 0x4;

    private static byte[]?[] _clips = new byte[3][];
    private static int _volume = -1;
    public static bool Enabled;

    public static void SetVolume(int volume)
    {
        volume = Math.Clamp(volume, 0, 100);
        if (volume == _volume) return;
        _volume = volume;
        double gain = volume / 100.0 * 0.35;
        _clips = new[]
        {
            Tone(new[] { 1320.0 }, 22, gain * 0.7),          // 移动：一下很轻的「嗒」
            Tone(new[] { 880.0, 1320.0 }, 70, gain),          // 确认：两个音往上
            Tone(new[] { 990.0, 660.0 }, 60, gain * 0.85),    // 返回：两个音往下
        };
    }

    public static void Play(Sfx kind)
    {
        if (!Enabled || _volume <= 0) return;
        var clip = _clips[(int)kind];
        if (clip == null) return;
        try { PlaySound(clip, 0, SND_ASYNC | SND_NODEFAULT | SND_MEMORY); } catch { }
    }

    /// <summary>16 位单声道 44.1 kHz：几个音依次各占一段，起落各 4 ms 的淡入淡出免得「咔」一声。</summary>
    private static byte[] Tone(double[] freqs, int ms, double gain)
    {
        const int rate = 44100;
        int total = rate * ms / 1000, per = total / freqs.Length, fade = rate * 4 / 1000;
        var data = new short[total];
        for (int i = 0; i < total; i++)
        {
            int part = Math.Min(i / per, freqs.Length - 1), j = i - part * per;
            double env = Math.Min(1, Math.Min(j / (double)fade, (per - j) / (double)fade)) * Math.Exp(-3.0 * i / total);
            data[i] = (short)(Math.Sin(2 * Math.PI * freqs[part] * i / rate) * env * gain * short.MaxValue);
        }
        using var ms_ = new MemoryStream();
        using var w = new BinaryWriter(ms_);
        int bytes = data.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes);
        foreach (var s in data) w.Write(s);
        w.Flush();
        return ms_.ToArray();
    }
}
