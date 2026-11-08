using Microsoft.Win32;

public partial class Program
{
    static class Startup
    {
        const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "PS4BatteryTray";
        public static string Command => "\"" + Path.Combine(AppContext.BaseDirectory, "PS4Battery.exe") + "\"";
        public static bool Enabled()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunPath);
            return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
        public static void Set(bool enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunPath, true);
            if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
            else key.DeleteValue(ValueName, false);
            if (Enabled() != enabled) throw new IOException("Windows 未保存启动设置。");
        }
        public static void Test()
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunPath, true);
            var before = key.GetValue(ValueName);
            var kind = before == null ? RegistryValueKind.String : key.GetValueKind(ValueName);
            try
            {
                Set(true);
                if (!Enabled() || !Equals(key.GetValue(ValueName), Command)) throw new Exception("Enable startup failed");
                Set(false);
                if (Enabled() || key.GetValue(ValueName) != null) throw new Exception("Disable startup failed");
            }
            finally
            {
                if (before == null) key.DeleteValue(ValueName, false);
                else key.SetValue(ValueName, before, kind);
            }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-test-result.txt"), "PASS: enabled, quoted executable path, disabled, original registry value restored");
        }
    }
}
