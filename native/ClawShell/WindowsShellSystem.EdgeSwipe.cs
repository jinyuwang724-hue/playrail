using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClawShell
{
    public sealed partial class WindowsShellSystem
    {
        private const string EdgeSwipeSession = @"Software\ClawToolbox\EdgeSwipeSession";
        private const string EdgeSwipePolicy = @"SOFTWARE\Policies\Microsoft\Windows\EdgeUI";
        private const string EdgeSwipeBackup = @"SOFTWARE\ClawToolbox\GameModeEdgeSwipe";
        private const string EdgeSwipeValue = "AllowEdgeSwipe";

        public void ReconcileEdgeSwipe(bool game)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(EdgeSwipeSession, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryOptions.Volatile))
                {
                    int desired = game ? 1 : 0;
                    if (Equals(key.GetValue("Game"), desired)) return;
                    key.SetValue("Game", desired, RegistryValueKind.DWord);
                    try { RunEdgeSwipeTask(game); }
                    catch { key.DeleteValue("Game", false); throw; }
                }
            }
            catch (Exception ex) { Log("edge swipe task: " + ex.Message); }
        }

        private static void RunEdgeSwipeTask(bool game)
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            dynamic folder = null, task = null, running = null;
            try
            {
                service.Connect();
                folder = service.GetFolder(@"\ClawToolbox");
                task = folder.GetTask("GameModeEdgeSwipe");
                running = task.Run(game ? "on" : "off");
            }
            finally
            {
                if (running != null) Marshal.FinalReleaseComObject(running);
                if (task != null) Marshal.FinalReleaseComObject(task);
                if (folder != null) Marshal.FinalReleaseComObject(folder);
                Marshal.FinalReleaseComObject(service);
            }
        }

        public static bool HasEdgeSwipeSession()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(EdgeSwipeSession))
                return key != null && Equals(key.GetValue("Game"), 1);
        }

        // Runs only as the task's fixed executable in Program Files. Arguments select a mode,
        // with no shell interpretation or user-supplied executable, registry path or command.
        internal static int ApplyEdgeSwipe(string[] args)
        {
            if (args.Length > 2) return 87;
            string mode = args.Length == 2 ? args[1] : "";
            if (mode != "on" && mode != "off" && mode != "" && mode != "$" + "(Arg0)") return 87;
            try
            {
                bool disable = mode == "on" || (mode != "off" && HasEdgeSwipeSession());
                using (var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var backup = disable ? hive.CreateSubKey(EdgeSwipeBackup) : hive.OpenSubKey(EdgeSwipeBackup, true))
                {
                    if (backup == null) return 0;
                    int saved = (int)backup.GetValue("Saved", 0);
                    if (!disable && saved == 0) return 0;
                    using (var policy = hive.CreateSubKey(EdgeSwipePolicy))
                    {
                        if (disable)
                        {
                            if (saved == 0)
                            {
                                object original = policy.GetValue(EdgeSwipeValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                                if (original != null) backup.SetValue("Original", original, policy.GetValueKind(EdgeSwipeValue));
                                backup.SetValue("Saved", original == null ? 1 : 2, RegistryValueKind.DWord);
                                backup.Flush();
                            }
                            policy.SetValue(EdgeSwipeValue, 0, RegistryValueKind.DWord);
                            policy.Flush();
                        }
                        else
                        {
                            if (saved == 1) policy.DeleteValue(EdgeSwipeValue, false);
                            else policy.SetValue(EdgeSwipeValue, backup.GetValue("Original", null, RegistryValueOptions.DoNotExpandEnvironmentNames),
                                backup.GetValueKind("Original"));
                            policy.Flush();
                            backup.DeleteValue("Saved", false);
                            backup.Flush();
                            backup.DeleteValue("Original", false);
                        }
                    }
                }
                return 0;
            }
            catch (Exception ex) { return ex.HResult; }
        }
    }
}
