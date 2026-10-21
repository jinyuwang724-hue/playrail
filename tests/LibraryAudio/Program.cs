using System.Buffers.Binary;
using ClawLibrary.Data;
using ClawLibrary.Stage;

string root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
string assets = Path.Combine(root, "frontend", "library-native", "ClawLibrary", "Assets", "Audio");
string scratch = Path.Combine(root, "artifacts", "audio-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
ClawLibrary.App.CacheDir = scratch;
int passed = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); passed++; }
void Reject(Action action, string label) { try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException) { passed++; return; } throw new Exception(label); }

foreach (string file in Directory.GetFiles(Path.Combine(assets, "Sfx"), "*.wav", SearchOption.AllDirectories))
{
    byte[] original = File.ReadAllBytes(file), half = WavePcm.WithVolume(original, 50), mute = WavePcm.WithVolume(original, 0);
    Check(original.SequenceEqual(WavePcm.WithVolume(original, 100)), "Full volume must preserve samples");
    for (int i = 44; i < mute.Length; i += 2)
    {
        short before = BinaryPrimitives.ReadInt16LittleEndian(original.AsSpan(i, 2));
        short after = BinaryPrimitives.ReadInt16LittleEndian(half.AsSpan(i, 2));
        if (mute[i] != 0 || mute[i + 1] != 0 || Math.Abs(after - before / 2.0) > .5) throw new Exception("PCM gain invalid");
    }
    Check(true, "Gain is applied without clipping and zero is silent");
}
Reject(() => WavePcm.WithVolume(new byte[44], 50), "Bad WAV header accepted");
var truncated = File.ReadAllBytes(Path.Combine(assets, "Sfx", "soft", "move.wav"))[..45];
Reject(() => WavePcm.WithVolume(truncated, 50), "Truncated WAV accepted");
Check(HomeAudioPolicy.CanPlay(true, 20, true, false), "Foreground music should play");
Check(!HomeAudioPolicy.CanPlay(true, 20, false, false), "Music must pause in background");
Check(!HomeAudioPolicy.CanPlay(true, 20, true, true), "Music must pause during launch");
Check(!HomeAudioPolicy.CanPlay(false, 20, true, false), "Disabled music must stay silent");
Check(!HomeAudioPolicy.CanPlay(true, 0, true, false), "Zero volume must stay silent");

var catalog = new MusicLibrary(assets, Path.Combine(scratch, "music"));
Check(catalog.Tracks().Count == 3, "Three built-in tracks required");
Check(catalog.Resolve("custom:../../outside.wav").Id == MusicLibrary.DefaultId, "Missing/stale IDs must fall back safely");
string source = Path.Combine(scratch, "自选 曲目.WAV");
File.Copy(Path.Combine(assets, "Sfx", "soft", "confirm.wav"), source);
var imported = catalog.Import(source);
Check(File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(imported.Path)), "Imported copy differs");
Check(imported.Name == "自选 曲目" && catalog.Resolve(imported.Id).Custom, "Unicode imported title lost");
Reject(() => catalog.Remove(imported with { Path = source }), "Deletion outside managed music folder accepted");
Reject(() => catalog.Remove(catalog.Tracks()[0]), "Built-in track deletion accepted");
catalog.Remove(imported);
Check(File.Exists(source) && !File.Exists(imported.Path), "Removing music must preserve user's original");
Check(catalog.Resolve(imported.Id).Id == MusicLibrary.DefaultId, "Deleted track must fall back");
string empty = Path.Combine(scratch, "empty.mp3"); File.WriteAllBytes(empty, Array.Empty<byte>());
Reject(() => catalog.Import(empty), "Empty import accepted");
Reject(() => catalog.Import(Path.Combine(scratch, "program.exe")), "Non-audio import accepted");

File.WriteAllText(Path.Combine(scratch, "ui.ini"), "sounds=true\nsoundVolume=35\nstyleB=true\nstartTab=steam\n");
var old = UiPrefs.Load();
Check(old.Sounds && old.SoundVolume == 35 && old.StyleB && old.StartTab == "steam", "Existing prefs lost");
Check(!old.MusicEnabled && old.MusicLoop && old.MusicVolume == 20, "Upgrade must not auto-enable music");
(old with { SoundTheme = 2, MusicEnabled = true, MusicVolume = 15, MusicTrack = "custom:自选.wav", MusicLoop = false }).Save();
var restored = UiPrefs.Load();
Check(restored.SoundTheme == 2 && restored.MusicEnabled && restored.MusicVolume == 15 && !restored.MusicLoop && restored.MusicTrack == "custom:自选.wav", "Audio prefs round trip failed");
Check(restored.StyleB && restored.SoundVolume == 35 && restored.StartTab == "steam", "Saving audio prefs damaged other prefs");
Console.WriteLine($"LibraryAudio: {passed} checks passed.");

namespace ClawLibrary
{
    public static class App { public static string CacheDir { get; set; } = ""; }
    public static class Log { public static void Warn(string value) => Console.Error.WriteLine(value); }
}
