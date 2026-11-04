// 游戏库的声音引擎：miniaudio（公有领域 / MIT-0，见 LICENSE-miniaudio）上的一层扁平 C 接口，给 C# 直接 P/Invoke。
// 一个引擎、两个分组：界面音效（预解码进内存，每次播放复制一份，可叠、可变调）和背景音乐（边读边解码，引擎内淡入淡出）。
// 没有声音在响时由调用方定期 ca_idle() 把设备停掉，不让 WASAPI 在后台一直混静音。
#define MINIAUDIO_IMPLEMENTATION
#define MA_NO_ENCODING
#define MA_NO_GENERATION
#define MA_ENABLE_ONLY_SPECIFIC_BACKENDS
#define MA_ENABLE_WASAPI
#define MA_ENABLE_WINMM
#include "miniaudio.h"
#include <math.h>

#define CA_API __declspec(dllexport)
#define CLIPS 32
#define VOICES 16
#define SAME_CLIP_LAYERS 2

typedef struct { ma_sound sound; int ok, clip; ma_uint64 seq; } voice;

static ma_mutex g_lock;
static ma_engine g_engine;
static ma_sound_group g_sfx, g_music;
static ma_sound g_clip[CLIPS];
static int g_clipOk[CLIPS];
static voice g_voice[VOICES];
static ma_sound g_track;
static int g_open, g_trackOk, g_running;
static ma_uint64 g_seq, g_plays, g_idleSince;
static ma_uint32 g_channels;

// 总线上的软限幅：0.7 以下原样，以上平滑压到 1 以内，几个声音叠在一起也不削波
static void limit(void* user, float* frames, ma_uint64 count)
{
    (void)user;
    ma_uint64 n = count * g_channels;
    for (ma_uint64 i = 0; i < n; i++)
    {
        float x = frames[i], a = fabsf(x);
        if (a > 0.7f) frames[i] = copysignf(0.7f + 0.3f * tanhf((a - 0.7f) / 0.3f), x);
    }
}

static int run(void)
{
    if (!g_running && ma_engine_start(&g_engine) == MA_SUCCESS) g_running = 1;
    g_idleSince = 0;
    return g_running;
}

CA_API int ca_open(void)
{
    if (g_open) return MA_SUCCESS;
    ma_mutex_init(&g_lock);
    ma_engine_config cfg = ma_engine_config_init();
    cfg.periodSizeInMilliseconds = 10;
    cfg.noAutoStart = MA_TRUE;
    cfg.onProcess = limit;
    ma_result r = ma_engine_init(&cfg, &g_engine);
    if (r != MA_SUCCESS) { ma_mutex_uninit(&g_lock); return r; }
    g_channels = ma_engine_get_channels(&g_engine);
    ma_sound_group_init(&g_engine, 0, NULL, &g_sfx);
    ma_sound_group_init(&g_engine, 0, NULL, &g_music);
    g_open = 1;
    return MA_SUCCESS;
}

static void drop_voice(voice* v)
{
    if (!v->ok) return;
    ma_sound_uninit(&v->sound);
    v->ok = 0;
}

CA_API void ca_close(void)
{
    if (!g_open) return;
    ma_mutex_lock(&g_lock);
    for (int i = 0; i < VOICES; i++) drop_voice(&g_voice[i]);
    for (int i = 0; i < CLIPS; i++) if (g_clipOk[i]) { ma_sound_uninit(&g_clip[i]); g_clipOk[i] = 0; }
    if (g_trackOk) { ma_sound_uninit(&g_track); g_trackOk = 0; }
    ma_sound_group_uninit(&g_sfx);
    ma_sound_group_uninit(&g_music);
    ma_engine_uninit(&g_engine);
    g_open = g_running = 0;
    ma_mutex_unlock(&g_lock);
    ma_mutex_uninit(&g_lock);
}

// ---------------- 界面音效 ----------------

/// 把一个音效文件整段解码进内存，放在 id 号位置（替换原来的）。
CA_API int ca_sfx_load(int id, const wchar_t* path)
{
    if (!g_open || id < 0 || id >= CLIPS) return MA_INVALID_ARGS;
    ma_mutex_lock(&g_lock);
    for (int i = 0; i < VOICES; i++) if (g_voice[i].ok && g_voice[i].clip == id) drop_voice(&g_voice[i]);
    if (g_clipOk[id]) { ma_sound_uninit(&g_clip[id]); g_clipOk[id] = 0; }
    ma_result r = ma_sound_init_from_file_w(&g_engine, path, MA_SOUND_FLAG_DECODE | MA_SOUND_FLAG_NO_SPATIALIZATION, &g_sfx, NULL, &g_clip[id]);
    if (r == MA_SUCCESS) g_clipOk[id] = 1;
    ma_mutex_unlock(&g_lock);
    return r;
}

/// 放一次：同一种声音最多叠两层（新的顶掉最旧的），总数满了也顶掉最旧的。
CA_API int ca_sfx_play(int id, float pitch, float gain)
{
    if (!g_open || id < 0 || id >= CLIPS || !g_clipOk[id]) return MA_INVALID_ARGS;
    ma_mutex_lock(&g_lock);
    voice* slot = NULL; voice* oldest = NULL; voice* oldestSame = NULL; int same = 0;
    for (int i = 0; i < VOICES; i++)
    {
        voice* v = &g_voice[i];
        if (v->ok && !ma_sound_is_playing(&v->sound)) drop_voice(v);
        if (!v->ok) { if (!slot) slot = v; continue; }
        if (!oldest || v->seq < oldest->seq) oldest = v;
        if (v->clip == id) { same++; if (!oldestSame || v->seq < oldestSame->seq) oldestSame = v; }
    }
    if (same >= SAME_CLIP_LAYERS) { drop_voice(oldestSame); slot = oldestSame; }
    else if (!slot) { drop_voice(oldest); slot = oldest; }
    ma_result r = ma_sound_init_copy(&g_engine, &g_clip[id], MA_SOUND_FLAG_NO_SPATIALIZATION, &g_sfx, &slot->sound);
    if (r == MA_SUCCESS)
    {
        slot->ok = 1; slot->clip = id; slot->seq = ++g_seq;
        ma_sound_set_pitch(&slot->sound, pitch);
        ma_sound_set_volume(&slot->sound, gain);
        run();
        r = ma_sound_start(&slot->sound);
        g_plays++;
    }
    ma_mutex_unlock(&g_lock);
    return r;
}

CA_API void ca_sfx_stop_all(void)
{
    if (!g_open) return;
    ma_mutex_lock(&g_lock);
    for (int i = 0; i < VOICES; i++) drop_voice(&g_voice[i]);
    ma_mutex_unlock(&g_lock);
}

CA_API void ca_sfx_volume(float volume) { if (g_open) ma_sound_group_set_volume(&g_sfx, volume); }

// ---------------- 背景音乐 ----------------

/// 打开一首（流式解码），音量从 0 开始、暂停状态；返回 miniaudio 的结果码。
CA_API int ca_music_open(const wchar_t* path, int loop)
{
    if (!g_open) return MA_INVALID_OPERATION;
    ma_mutex_lock(&g_lock);
    if (g_trackOk) { ma_sound_uninit(&g_track); g_trackOk = 0; }
    ma_result r = ma_sound_init_from_file_w(&g_engine, path, MA_SOUND_FLAG_STREAM | MA_SOUND_FLAG_NO_SPATIALIZATION | MA_SOUND_FLAG_NO_PITCH, &g_music, NULL, &g_track);
    if (r == MA_SUCCESS)
    {
        g_trackOk = 1;
        ma_sound_set_looping(&g_track, loop);
        ma_sound_set_volume(&g_track, 0);
    }
    ma_mutex_unlock(&g_lock);
    return r;
}

CA_API void ca_music_close(void)
{
    if (!g_open) return;
    ma_mutex_lock(&g_lock);
    if (g_trackOk) { ma_sound_uninit(&g_track); g_trackOk = 0; }
    ma_mutex_unlock(&g_lock);
}

CA_API void ca_music_loop(int loop) { if (g_trackOk) ma_sound_set_looping(&g_track, loop); }

/// 从当前音量渐变到 target（线性幅度 0..1）。
CA_API void ca_music_fade(float target, int ms)
{
    if (!g_trackOk) return;
    ma_sound_set_volume(&g_track, 1);
    ma_sound_set_fade_in_milliseconds(&g_track, -1, target, (ma_uint64)(ms < 0 ? 0 : ms));
}

CA_API float ca_music_level(void) { return g_trackOk ? ma_sound_get_current_fade_volume(&g_track) : 0; }

CA_API void ca_music_play(void)
{
    if (!g_trackOk) return;
    ma_mutex_lock(&g_lock);
    run();
    ma_sound_start(&g_track);
    ma_mutex_unlock(&g_lock);
}

CA_API void ca_music_pause(void) { if (g_trackOk) ma_sound_stop(&g_track); }

CA_API int ca_music_playing(void) { return g_trackOk && ma_sound_is_playing(&g_track); }

// ---------------- 设备 / 诊断 ----------------

/// 没有声音在响超过 ms 毫秒就停掉设备；返回设备是否还在运行。
CA_API int ca_idle(int ms)
{
    if (!g_open) return 0;
    ma_mutex_lock(&g_lock);
    if (g_running)
    {
        int busy = g_trackOk && ma_sound_is_playing(&g_track);
        for (int i = 0; i < VOICES && !busy; i++) busy = g_voice[i].ok && ma_sound_is_playing(&g_voice[i].sound);
        ma_uint64 now = GetTickCount64();
        if (busy) g_idleSince = 0;
        else if (!g_idleSince) g_idleSince = now;
        else if (now - g_idleSince >= (ma_uint64)ms)
        {
            for (int i = 0; i < VOICES; i++) drop_voice(&g_voice[i]);
            ma_engine_stop(&g_engine);
            g_running = 0; g_idleSince = 0;
        }
    }
    int running = g_running;
    ma_mutex_unlock(&g_lock);
    return running;
}

/// 试解码一个文件：能打开且长度大于 0 就成功，顺带给出时长（毫秒）。
CA_API int ca_probe(const wchar_t* path, ma_uint64* lengthMs)
{
    ma_decoder decoder;
    ma_decoder_config cfg = ma_decoder_config_init(ma_format_f32, 0, 0);
    ma_result r = ma_decoder_init_file_w(path, &cfg, &decoder);
    if (r != MA_SUCCESS) return r;
    ma_uint64 frames = 0;
    r = ma_decoder_get_length_in_pcm_frames(&decoder, &frames);
    if (r == MA_SUCCESS && frames == 0) r = MA_INVALID_FILE;
    if (lengthMs) *lengthMs = decoder.outputSampleRate ? frames * 1000 / decoder.outputSampleRate : 0;
    ma_decoder_uninit(&decoder);
    return r;
}

/// 诊断：后端名、设备采样率 / 声道、累计播放次数、设备是否在跑。
CA_API int ca_info(char* buffer, int capacity)
{
    if (!g_open) return snprintf(buffer, capacity, "closed");
    ma_device* d = ma_engine_get_device(&g_engine);
    return snprintf(buffer, capacity, "%s %u Hz %u ch, period %u, plays %llu, running %d",
        ma_get_backend_name(d->pContext->backend), ma_engine_get_sample_rate(&g_engine), g_channels,
        d->playback.internalPeriodSizeInFrames, (unsigned long long)g_plays, g_running);
}
