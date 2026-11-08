using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public partial class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--startup-test")) { Startup.Test(); return 0; }
        if (args.Contains("--self-test"))
        {
            SelfTest();
            AlertPolicy.Test();
            TestLights();
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "test-result.txt"), "PASS: protocol parsing, alerts, light modes, BT/USB LED packets, output CRC, settings roundtrip");
            return 0;
        }
        if (args.Contains("--once")) return ConsoleMain(args).GetAwaiter().GetResult();
        using var singleton = new Mutex(true, @"Local\PS4BatteryTray", out bool created);
        if (!created) return 0;
        ApplicationConfiguration.Initialize();
        Application.Run(new BatteryTray(args.Contains("--smoke-test"), args.Contains("--settings")));
        return 0;
    }

    // Keep warning state across reconnects. Re-arm after charging or recovery to >=30%.
    sealed class AlertPolicy
    {
        int warned;
        public bool Update(int level, bool charging, int low = 20, int critical = 10, bool criticalEnabled = true)
        {
            if (charging || level * 10 >= low + 10) { warned = 0; return false; }
            if (level < 0 || level * 10 >= low) return false;
            int severity = criticalEnabled && level * 10 < critical ? 2 : 1;
            if (severity <= warned) return false;
            warned = severity;
            return true;
        }
        public void Reset() => warned = 0;
        public static void Test()
        {
            var p = new AlertPolicy();
            if (p.Update(2, false) || !p.Update(1, false) || p.Update(1, false) ||
                p.Update(-1, false) || p.Update(1, false) || !p.Update(0, false) ||
                p.Update(0, false) || p.Update(1, true) || !p.Update(1, false))
                throw new Exception("Alert policy failure");
            p.Update(3, false);
            if (!p.Update(1, false)) throw new Exception("Alert rearm failure");
        }
    }

    sealed class BatteryTray : ApplicationContext
    {
        readonly NotifyIcon tray;
        readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
        readonly CancellationTokenSource stop = new();
        readonly AlertPolicy alerts = new();
        readonly ToolStripMenuItem status = new("等待手柄连接…") { Enabled = false };
        readonly ToolStripMenuItem updated = new("尚未收到数据") { Enabled = false };
        readonly ToolStripMenuItem notifications = new("低电量提醒（低于 20%）") { Checked = true, CheckOnClick = true };
        readonly ToolStripMenuItem fixedLight = new("固定颜色…");
        readonly ToolStripMenuItem batteryLight = new("随电量变色");
        readonly ToolStripMenuItem gradientLight = new("电量渐变曲线");
        readonly ToolStripMenuItem offLight = new("关闭灯条");
        readonly ToolStripMenuItem lightInfo = new("灯条：等待连接") { Enabled = false };
        readonly Task worker;
        Icon currentIcon;
        string issue = "正在查找 PS4 手柄…";
        string iconKey = "";
        int ticks;

        SettingsWindow settingsWindow;
        public BatteryTray(bool smoke, bool showSettings = false)
        {
            lightSettings = LoadLights();
            var menu = new ContextMenuStrip();
            menu.Items.Add("PS4 手柄电量").Enabled = false;
            menu.Items.Add(status);
            menu.Items.Add(updated);
            menu.Items.Add(new ToolStripSeparator());
            var lightMenu = new ToolStripMenuItem("灯条模式");
            lightMenu.DropDownItems.AddRange(new ToolStripItem[] { fixedLight, batteryLight, gradientLight, offLight });
            fixedLight.Click += (_, _) =>
            {
                var s = lightSettings;
                using var picker = new ColorDialog { FullOpen = true, Color = Color.FromArgb(s.R, s.G, s.B) };
                if (picker.ShowDialog() == DialogResult.OK)
                    SetLight(s with { Mode = "fixed", R = picker.Color.R, G = picker.Color.G, B = picker.Color.B });
            };
            batteryLight.Click += (_, _) => SetLight(lightSettings with { Mode = "battery" });
            gradientLight.Click += (_, _) => SetLight(lightSettings with { Mode = "gradient" });
            offLight.Click += (_, _) => SetLight(lightSettings with { Mode = "off" });
            menu.Items.Add(lightMenu);
            menu.Items.Add(lightInfo);
            CheckLightMenu();
            menu.Items.Add(notifications);
            notifications.Click += (_, _) => SetLight(lightSettings with { Alerts = notifications.Checked });
            menu.Items.Add("设置…", null, (_, _) => OpenSettings());
            menu.Items.Add("测试提醒", null, (_, _) => Notify("提醒测试", "低电量时将在这里提醒你。Windows 勿扰模式可能隐藏通知。"));
            menu.Items.Add("查看状态", null, (_, _) => ShowStatus());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (_, _) => ExitThread());
            currentIcon = MakeIcon("?", Color.DimGray);
            tray = new NotifyIcon { Icon = currentIcon, Text = "PS4：正在查找手柄", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += (_, _) => OpenSettings();
            timer.Tick += (_, _) =>
            {
                Refresh();
                if (smoke && ++ticks >= 8)
                {
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-result.txt"),
                        $"Tray visible: {tray.Visible}\nTooltip: {tray.Text}\nStatus: {status.Text}\n{updated.Text}\nWorker: {worker.Status}\n{lightInfo.Text}\nLight mode: {lightSettings.Mode}");
                    ExitThread();
                }
            };
            worker = Task.Run(Watch);
            timer.Start();
            if (showSettings) OpenSettings();
        }

        async Task Watch()
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var device = Find().FirstOrDefault();
                    if (device == null) issue = "未连接 / 无法访问手柄；请按 PS 键连接，或检查 HidHide。";
                    else { issue = "正在等待电量报告…"; await Monitor(device, false, stop.Token); }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception e) { issue = e.Message; }
                finally { latest = null; lightStatus = "灯条：等待连接"; }
                try { await Task.Delay(3000, stop.Token); }
                catch (OperationCanceledException) { break; }
            }
        }

        void Refresh()
        {
            lightInfo.Text = lightStatus;
            var r = Volatile.Read(ref latest);
            bool fresh = r != null && (DateTime.UtcNow - r.Received).TotalSeconds <= 5;
            string label = fresh && r.Level >= 0 ? (r.Level * 10).ToString() : "?";
            Color color = !fresh || r.Level < 0 ? Color.DimGray : r.Charging ? Color.RoyalBlue : r.Level * 10 < lightSettings.Low ? Color.Firebrick : Color.SeaGreen;
            string key = label + color.ToArgb();
            if (key != iconKey)
            {
                var next = MakeIcon(label, color);
                tray.Icon = next;
                currentIcon.Dispose(); currentIcon = next; iconKey = key;
            }
            status.Text = fresh ? r.Text : issue;
            updated.Text = fresh ? $"最近收到数据：{r.Received.ToLocalTime():HH:mm:ss}" : "暂无有效实时电量";
            tray.Text = fresh ? "PS4 " + r.Text : "PS4：未连接或电量不可用";
            if (fresh)
            {
                bool warn = notifications.Checked && alerts.Update(r.Level, r.Charging, lightSettings.Low, lightSettings.Critical, lightSettings.CriticalEnabled);
                if (warn && notifications.Checked)
                    Notify(lightSettings.CriticalEnabled && r.Level * 10 < lightSettings.Critical ? "PS4 手柄电量即将耗尽" : "PS4 手柄电量低", r.Text + "\n请及时充电。");
            }
        }

        bool SetLight(LightSettings value)
        {
            try
            {
                File.WriteAllText(SettingsPath + ".tmp", System.Text.Json.JsonSerializer.Serialize(value));
                File.Move(SettingsPath + ".tmp", SettingsPath, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { MessageBox.Show("无法保存设置：" + e.Message, "设置"); return false; }
            if (value.Alerts != lightSettings.Alerts || value.Low != lightSettings.Low || value.Critical != lightSettings.Critical || value.CriticalEnabled != lightSettings.CriticalEnabled) alerts.Reset();
            Volatile.Write(ref lightSettings, value);
            CheckLightMenu();
            return true;
        }

        void CheckLightMenu()
        {
            notifications.Checked = lightSettings.Alerts;
            notifications.Text = $"低电量提醒（低于 {lightSettings.Low}%）";
            fixedLight.Checked = lightSettings.Mode == "fixed";
            batteryLight.Checked = lightSettings.Mode == "battery";
            gradientLight.Checked = lightSettings.Mode == "gradient";
            offLight.Checked = lightSettings.Mode == "off";
        }

        void OpenSettings()
        {
            if (settingsWindow == null || settingsWindow.IsDisposed)
                settingsWindow = new SettingsWindow(SetLight, () => status.Text + "\n" + updated.Text + "\n" + lightStatus,
                    () => Notify("提醒测试", "低电量提醒可以正常发送。Windows 勿扰模式可能隐藏弹窗。"));
            settingsWindow.Show();
            if (settingsWindow.WindowState == FormWindowState.Minimized) settingsWindow.WindowState = FormWindowState.Normal;
            settingsWindow.Activate();
        }

        void Notify(string title, string text) => tray.ShowBalloonTip(8000, title, text, ToolTipIcon.Warning);
        void ShowStatus() => MessageBox.Show(status.Text + "\n" + updated.Text +
            $"\n\n图标数字为电量区间下限，例如 20 表示 20–29%。\n持续读取，每秒更新界面；断线后每 3 秒重试。\n低电量阈值：{lightSettings.Low}%。更多选项请打开设置。",
            "PS4 手柄电量", MessageBoxButtons.OK, MessageBoxIcon.Information);

        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
        static Icon MakeIcon(string label, Color color)
        {
            using var bitmap = new Bitmap(32, 32);
            using var g = Graphics.FromImage(bitmap);
            g.Clear(color);
            using var font = new Font("Segoe UI", label.Length > 2 ? 12 : 16, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(label, font, Brushes.White, new RectangleF(0, 0, 32, 32), format);
            IntPtr handle = bitmap.GetHicon();
            try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
            finally { DestroyIcon(handle); }
        }

        protected override void ExitThreadCore()
        {
            settingsWindow?.Close(); timer.Stop(); timer.Dispose(); stop.Cancel();
            tray.Visible = false; tray.ContextMenuStrip.Dispose(); tray.Dispose(); currentIcon.Dispose();
            base.ExitThreadCore();
        }
    }
}
