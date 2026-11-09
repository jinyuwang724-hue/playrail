namespace ClawLibrary
{
    internal static class Log { public static void Warn(string value) { } public static void Info(string value) { } }
}
namespace ClawLibrary.Stage
{
    internal sealed class UiPrefs
    {
        public bool FseSteam => false;
        public static UiPrefs Load() => new();
    }
}
