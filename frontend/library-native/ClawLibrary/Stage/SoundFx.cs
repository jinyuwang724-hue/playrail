// 界面音效：照 Steam 大屏幕的那一套事件（导航、到头、确认、返回、进出详情、启动、页签、侧边菜单、弹层、开关、滑块、
// 提示、打字、开机……），声音是 tools/Compose-UiSounds.mjs 合成的原创短音，三套风格。
// 播放走自己的小混音器（waveOut，12 ms 一块、四块轮转）：几个声音可以叠着响，不像 PlaySound 那样新的掐掉旧的；
// 每个声音可以变调（打字、连续导航轻微错开音高），没有声音时不写缓冲，后台线程只等事件。
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace ClawLibrary.Stage;

public enum Sfx
{
    None, Nav, Bump, Activate, Back, DetailIn, DetailOut, Launch, Tab, MenuIn, MenuOut, ModalShow, ModalHide,
    ToggleOn, ToggleOff, SliderUp, SliderDown, Toast, Positive, Error, Typing, Startup, Shutter,
}

public static class SoundFx
{
    public static readonly string[] ThemeNames = { "轻触", "清透", "数码" };
    private static readonly string[] Themes = { "soft", "crystal", "digital" };
    /// <summary>Sfx 顺序对应的文件名（Sfx.None 不对应文件）。</summary>
    private static readonly string[] Files =
    {
        "", "nav", "bump", "activate", "back", "detail_in", "detail_out", "launch", "tab", "menu_in", "menu_out", "modal_show", "modal_hide",
        "toggle_on", "toggle_off", "slider_up", "slider_down", "toast", "positive", "error", "typing", "startup", "shutter",
    };
    private const int Rate = 44100, BlockSamples = 528, Blocks = 4, MaxVoices = 12;

    private static float[]?[] _clips = new float[Files.Length][];
    private static int _volume = -1, _theme;
    private static volatile bool _active = true;
    private static long _lastNav;
    private static bool _navFlip;
    public static bool Enabled;
    /// <summary>写出去的块数（测试用：确认混音器真的在出声）。</summary>
    public static long Blocks_;

    private sealed class Voice { public float[] Data = Array.Empty<float>(); public double Pos; public float Rate = 1, Gain = 1; }
    private static readonly List<Voice> _voices = new();
    private static readonly object _gate = new();
    private static float _master;
    private static Thread? _thread;
    private static AutoResetEvent? _wake;
    private static volatile bool _stop;
    private static readonly Random _rng = new();

    public static void SetVolume(int volume) => Configure(_theme, volume);

    public static void Configure(int theme, int volume)
    {
        theme = Math.Clamp(theme, 0, Themes.Length - 1); volume = Math.Clamp(volume, 0, 100);
        // 音量是响度刻度（平方），40 % 听起来是一半左右，0 静音
        lock (_gate) _master = (float)Math.Pow(volume / 100.0, 2) * 1.6f;
        if (_theme == theme && _volume >= 0) { _volume = volume; return; }
        _theme = theme; _volume = volume;
        var clips = new float[Files.Length][];
        for (int i = 1; i < Files.Length; i++)
            try { clips[i] = Load(Path.Combine(AppContext.BaseDirectory, "Assets", "Audio", "Sfx", Themes[theme], Files[i] + ".wav")); }
            catch (Exception ex) { Log.Warn($"sound load {Files[i]}: {ex.Message}"); }
        _clips = clips;
    }

    /// <summary>16 位 PCM WAV → 单声道浮点（多声道取平均；采样率按 44.1 kHz 播放，生成器就是这个采样率）。</summary>
    private static float[] Load(string path)
    {
        var b = File.ReadAllBytes(path);
        int channels = 1, offset = 12;
        while (offset + 8 <= b.Length)
        {
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset + 4));
            int start = offset + 8;
            if (b.AsSpan(offset, 4).SequenceEqual("fmt "u8)) channels = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(start + 2));
            if (b.AsSpan(offset, 4).SequenceEqual("data"u8))
            {
                int frames = Math.Min(size, b.Length - start) / 2 / channels;
                var data = new float[frames];
                for (int i = 0; i < frames; i++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++) sum += BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(start + (i * channels + c) * 2)) / 32768f;
                    data[i] = sum / channels;
                }
                return data;
            }
            offset = start + size + (size & 1);
        }
        throw new InvalidDataException("没有 data 块");
    }

    /// <summary>游戏库不在前台：正在响的声音立刻停掉，之后不再出声。</summary>
    public static void SetActive(bool active)
    {
        _active = active;
        if (!active) lock (_gate) _voices.Clear();
    }

    /// <param name="pitch">变调（1 = 原调；播放速度跟着变）。</param>
    public static void Play(Sfx kind, bool preview = false, float pitch = 1)
    {
        if (kind == Sfx.None || (!Enabled && !preview) || !_active || _volume <= 0) return;
        if (kind == Sfx.Nav)
        {
            // 连按 / 摇杆连发：30 ms 内只响一次；相邻两下音高错开一点点，听着不像机关枪
            long now = Environment.TickCount64;
            if (now - _lastNav < 30) return;
            _lastNav = now;
            _navFlip = !_navFlip;
            pitch *= _navFlip ? 1f : 1.035f;
        }
        if (kind == Sfx.Typing) pitch *= 0.94f + (float)_rng.NextDouble() * 0.12f;
        var clip = _clips[(int)kind];
        if (clip == null) return;
        lock (_gate)
        {
            // 同一种声音最多叠两层（连发时旧的让位），总数也有上限
            int same = 0;
            for (int i = _voices.Count - 1; i >= 0; i--) if (_voices[i].Data == clip && ++same >= 2) _voices.RemoveAt(i);
            if (_voices.Count >= MaxVoices) _voices.RemoveAt(0);
            _voices.Add(new Voice { Data = clip, Rate = pitch });
        }
        EnsureThread();
        _wake!.Set();
    }

    // ---------------- 混音线程（waveOut） ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat { public ushort Tag, Channels; public uint SamplesPerSec, AvgBytesPerSec; public ushort BlockAlign, BitsPerSample, Size; }

    // WAVEHDR（x64）：lpData 0、dwBufferLength 8、dwFlags 24，共 48 字节；直接按偏移读写，不走封送
    private const int HdrSize = 48, HdrBufferLength = 8, HdrFlags = 24;

    [DllImport("winmm.dll")] private static extern int waveOutOpen(out nint handle, uint device, ref WaveFormat format, nint callback, nint instance, uint flags);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(nint handle, nint header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(nint handle, nint header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(nint handle, nint header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutReset(nint handle);
    [DllImport("winmm.dll")] private static extern int waveOutClose(nint handle);

    private const uint WaveMapper = 0xFFFFFFFF, CallbackEvent = 0x50000, WhdrInQueue = 0x10;

    private static void EnsureThread()
    {
        if (_thread != null) return;
        _wake = new AutoResetEvent(false);
        _stop = false;
        _thread = new Thread(MixLoop) { IsBackground = true, Name = "ui-sound", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private static void MixLoop()
    {
        nint dev = 0;
        var headers = new nint[Blocks];
        const int hdrSize = HdrSize;
        try
        {
            var fmt = new WaveFormat { Tag = 1, Channels = 1, SamplesPerSec = Rate, AvgBytesPerSec = Rate * 2, BlockAlign = 2, BitsPerSample = 16 };
            int r = waveOutOpen(out dev, WaveMapper, ref fmt, _wake!.SafeWaitHandle.DangerousGetHandle(), 0, CallbackEvent);
            if (r != 0) { Log.Warn($"ui sound: waveOutOpen {r}"); return; }
            Log.Info("ui sound: mixer open");
            for (int i = 0; i < Blocks; i++)
            {
                headers[i] = Marshal.AllocHGlobal(hdrSize);
                for (int o = 0; o < hdrSize; o += 8) Marshal.WriteInt64(headers[i], o, 0);
                Marshal.WriteIntPtr(headers[i], 0, Marshal.AllocHGlobal(BlockSamples * 2));
                Marshal.WriteInt32(headers[i], HdrBufferLength, BlockSamples * 2);
                waveOutPrepareHeader(dev, headers[i], (uint)hdrSize);
            }
            var mix = new float[BlockSamples];
            var pcm = new short[BlockSamples];
            while (!_stop)
            {
                _wake!.WaitOne(500);
                for (int i = 0; i < Blocks; i++)
                {
                    if (((uint)Marshal.ReadInt32(headers[i], HdrFlags) & WhdrInQueue) != 0) continue;
                    if (!Render(mix)) break;
                    for (int s = 0; s < BlockSamples; s++)
                    {
                        // 软限幅：几个声音叠在一起也不会削波
                        float v = mix[s];
                        v = v / (1 + Math.Abs(v) * 0.35f);
                        pcm[s] = (short)Math.Clamp(v * 32767f, -32767f, 32767f);
                    }
                    Marshal.Copy(pcm, 0, Marshal.ReadIntPtr(headers[i], 0), BlockSamples);
                    int w = waveOutWrite(dev, headers[i], (uint)hdrSize);
                    if (w != 0) Log.Warn($"ui sound: waveOutWrite {w}");
                    Blocks_++;
                }
            }
        }
        catch (Exception ex) { Log.Warn("ui sound mixer: " + ex.Message); }
        finally
        {
            if (dev != 0)
            {
                waveOutReset(dev);
                foreach (var hp in headers)
                {
                    if (hp == 0) continue;
                    waveOutUnprepareHeader(dev, hp, (uint)hdrSize);
                    Marshal.FreeHGlobal(Marshal.ReadIntPtr(hp, 0));
                    Marshal.FreeHGlobal(hp);
                }
                waveOutClose(dev);
            }
        }
    }

    /// <summary>把正在响的声音混进一块；没有声音时返回 false（不写缓冲，设备自己停下）。</summary>
    private static bool Render(float[] mix)
    {
        lock (_gate)
        {
            if (_voices.Count == 0 || !_active) { _voices.Clear(); return false; }
            Array.Clear(mix);
            for (int vi = _voices.Count - 1; vi >= 0; vi--)
            {
                var v = _voices[vi];
                var d = v.Data;
                double pos = v.Pos;
                float g = v.Gain * _master;
                for (int s = 0; s < mix.Length; s++)
                {
                    int i = (int)pos;
                    if (i + 1 >= d.Length) { pos = d.Length; break; }
                    float f = (float)(pos - i);
                    mix[s] += (d[i] + (d[i + 1] - d[i]) * f) * g;
                    pos += v.Rate;
                }
                v.Pos = pos;
                if (pos >= d.Length - 1) _voices.RemoveAt(vi);
            }
            return true;
        }
    }

    public static void Dispose()
    {
        Enabled = false;
        lock (_gate) _voices.Clear();
        _stop = true;
        _wake?.Set();
        _thread?.Join(500);
        _thread = null;
        _volume = -1;
    }
}
