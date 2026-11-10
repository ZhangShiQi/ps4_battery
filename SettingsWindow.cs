using System.Drawing;
using System.Windows.Forms;

public partial class Program
{
    sealed class SettingsWindow : Form
    {
        readonly GradientEditor gradient = new();
        readonly CheckBox startup = new() { Text = "登录 Windows 后自动运行", AutoSize = true };
        readonly Label startupStatus = Note("");
        bool startupBaseline;
        readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        readonly Button fixedColor = ColorButton(), normalColor = ColorButton(), lowColor = ColorButton(), chargingColor = ColorButton();
        readonly CheckBox enable = new() { Text = "启用低电量提醒", AutoSize = true };
        readonly NumericUpDown lightRefresh = new() { Minimum = 1, Maximum = 60, Value = 5, Width = 100 };
        readonly CheckBox keepLight = new() { Text = "保持灯条设置（定时重新应用当前颜色）", AutoSize = true };
        readonly CheckBox criticalEnable = new() { Text = "启用电量过低的第二次提醒", AutoSize = true };
        readonly NumericUpDown low = new() { Minimum = 10, Maximum = 90, Increment = 10, Width = 100 };
        readonly NumericUpDown critical = new() { Minimum = 10, Maximum = 80, Increment = 10, Width = 100 };
        readonly Label live = new() { AutoSize = true, MaximumSize = new Size(570, 0) };
        readonly Label message = new() { AutoSize = true, ForeColor = Color.SeaGreen };
        readonly System.Windows.Forms.Timer refresh = new() { Interval = 1000 };
        readonly Func<LightSettings, bool> save;
        readonly Func<string> getStatus;
        LightSettings baseline;

        static Button ColorButton() => new() { Width = 160, Height = 34, FlatStyle = FlatStyle.Flat };
        static void SetColor(Button button, Color color)
        {
            button.Tag = color; button.BackColor = color;
            button.ForeColor = color.R * .299 + color.G * .587 + color.B * .114 < 140 ? Color.White : Color.Black;
            button.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}   选择…";
        }
        static int Value(Button b) => ((Color)b.Tag).ToArgb() & 0xffffff;
        static Label Note(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 8, 0, 14) };
        static FlowLayoutPanel Page(TabControl tabs, string title)
        {
            var page = new TabPage(title) { Padding = new Padding(20) };
            var content = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            page.Controls.Add(content); tabs.TabPages.Add(page); return content;
        }
        static void Row(FlowLayoutPanel page, string title, Control control)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 6) };
            row.Controls.Add(new Label { Text = title, Width = 180, Height = 34, TextAlign = ContentAlignment.MiddleLeft });
            row.Controls.Add(control); page.Controls.Add(row);
        }

        public SettingsWindow(Func<LightSettings, bool> save, Func<string> getStatus, Action test, Action controllerTest)
        {
            this.save = save; this.getStatus = getStatus;
            Text = "PS4 手柄 · 设置"; ClientSize = new Size(680, 720); MinimumSize = new Size(680, 580);
            StartPosition = FormStartPosition.CenterScreen; Font = new Font("Microsoft YaHei UI", 10);
            AutoScaleMode = AutoScaleMode.Dpi;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(14) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            root.Controls.Add(new Label { Text = "手柄设置", Font = new Font(Font.FontFamily, 17, FontStyle.Bold), Dock = DockStyle.Fill }, 0, 0);
            var tabs = new TabControl { Dock = DockStyle.Fill }; root.Controls.Add(tabs, 0, 1);
            var general = Page(tabs, "常规");
            general.Controls.Add(startup);
            general.Controls.Add(Note("开启后，登录当前 Windows 账户时自动启动到系统托盘。\n不会自动打开设置窗口，也不需要管理员权限。\n勾选或取消后点击“应用”保存。"));
            general.Controls.Add(startupStatus);
            general.Controls.Add(Note("如果在 Windows 的“启动应用”中禁用了本程序，请在那里重新启用。\n开启后请保留程序文件夹的位置，移动程序后需要重新设置。"));
            try { startupBaseline = Startup.Enabled(); startup.Checked = startupBaseline; startupStatus.Text = startupBaseline ? "当前已设置自动运行" : "当前未设置自动运行"; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { startup.Enabled = false; startupStatus.Text = "无法读取启动设置：" + e.Message; }
            startup.CheckedChanged += (_, _) => message.Text = "尚未应用";
            var overview = Page(tabs, "状态");
            overview.Controls.Add(Note("当前手柄")); overview.Controls.Add(live);
            var testController = new Button { Text = "打开手柄功能测试", AutoSize = true };
            testController.Click += (_, _) => controllerTest(); overview.Controls.Add(testController);
            overview.Controls.Add(Note("后台持续接收数据，界面每秒更新；断线后每 3 秒重连。\n电量按约 10% 的区间报告，例如图标 20 表示 20–29%。\n关闭设置窗口后，托盘程序仍会继续运行。"));
            var lights = Page(tabs, "灯条");
            mode.Items.AddRange(new object[] { "固定颜色", "随电量变色", "关闭灯条", "电量渐变曲线" });
            Row(lights, "灯条模式", mode); Row(lights, "固定颜色", fixedColor);
            Row(lights, "正常电量颜色", normalColor); Row(lights, "低电量颜色", lowColor); Row(lights, "充电颜色", chargingColor);
            lights.Controls.Add(keepLight);
            Row(lights, "灯条刷新间隔（秒）", lightRefresh);
            lights.Controls.Add(Note("用于恢复被重置或覆盖的灯色。如果其他软件也控制灯条，建议只保留一个控制来源。"));
            lights.Controls.Add(Note("自动变色使用“电量提醒”中的低电量阈值，即使关闭弹窗提醒也生效。\n选择后点击“应用”生效。Steam 等程序可能覆盖灯色。"));
            var curve = Page(tabs, "颜色曲线");
            curve.Controls.Add(Note("拖动节点调整电量位置，双击节点或点击“节点颜色”选色。\n0% 和 100% 为固定端点；节点之间自动渐变，最多 16 个节点。"));
            curve.Controls.Add(gradient);
            var useCurve = new Button { Text = "使用此曲线作为灯条模式", AutoSize = true };
            useCurve.Click += (_, _) => { mode.SelectedIndex = 3; message.Text = "点击应用以启用曲线"; };
            curve.Controls.Add(useCurve);
            curve.Controls.Add(Note("模拟电量仅用于预览。实际电量仍按约 10% 一档，跨档时灯色平滑过渡。\n充电时优先使用“灯条”页的充电颜色。预览不保存，离开本页自动恢复。"));
            gradient.Changed += () => message.Text = "尚未应用";
            var alerts = Page(tabs, "电量提醒");
            alerts.Controls.Add(enable); Row(alerts, "低于此电量时提醒（%）", low);
            alerts.Controls.Add(criticalEnable); Row(alerts, "再次提醒阈值（%）", critical);
            alerts.Controls.Add(Note("阈值按 10% 设置，第二次提醒阈值必须更低。\n例如低于 20%：在 0–19% 时提醒，20–29% 不提醒。\n同一轮放电每个等级提醒一次；充电或恢复到低电量阈值以上一档后重置。\n修改提醒设置会重新判断当前电量。"));
            var testButton = new Button { Text = "测试通知", AutoSize = true }; testButton.Click += (_, _) => test(); alerts.Controls.Add(testButton);
            alerts.Controls.Add(Note("Windows 勿扰模式或通知设置可能隐藏弹窗。"));
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 12, 0, 0) };
            var close = new Button { Text = "关闭", AutoSize = true }; close.Click += (_, _) => Close();
            var apply = new Button { Text = "应用", AutoSize = true }; apply.Click += (_, _) => Apply();
            footer.Controls.Add(close); footer.Controls.Add(apply); footer.Controls.Add(message); root.Controls.Add(footer, 0, 2);
            Controls.Add(root);
            foreach (var button in new[] { fixedColor, normalColor, lowColor, chargingColor })
                button.Click += (_, _) => { using var picker = new ColorDialog { FullOpen = true, Color = (Color)button.Tag }; if (picker.ShowDialog(this) == DialogResult.OK) { SetColor(button, picker.Color); message.Text = "尚未应用"; } };
            mode.SelectedIndexChanged += (_, _) => { UpdateEnabled(); message.Text = "尚未应用"; };
            enable.CheckedChanged += (_, _) => { UpdateEnabled(); message.Text = "尚未应用"; };
            criticalEnable.CheckedChanged += (_, _) => { UpdateEnabled(); message.Text = "尚未应用"; };
            low.ValueChanged += (_, _) => message.Text = "尚未应用";
            critical.ValueChanged += (_, _) => message.Text = "尚未应用";
            keepLight.CheckedChanged += (_, _) => { lightRefresh.Enabled = keepLight.Checked; message.Text = "尚未应用"; };
            lightRefresh.ValueChanged += (_, _) => message.Text = "尚未应用";
            LoadValues();
            refresh.Tick += (_, _) => { live.Text = getStatus(); gradient.Pulse(); }; refresh.Start(); live.Text = getStatus();
            FormClosed += (_, _) => { gradient.StopPreview(); refresh.Dispose(); };
        }
        void LoadValues()
        {
            baseline = lightSettings;
            keepLight.Checked = baseline.KeepLight;
            lightRefresh.Value = baseline.LightRefreshSeconds; lightRefresh.Enabled = keepLight.Checked;
            gradient.Stops = baseline.Stops;
            mode.SelectedIndex = baseline.Mode == "fixed" ? 0 : baseline.Mode == "battery" ? 1 : baseline.Mode == "gradient" ? 3 : 2;
            SetColor(fixedColor, Color.FromArgb(baseline.R, baseline.G, baseline.B));
            SetColor(normalColor, Rgb(baseline.NormalColor)); SetColor(lowColor, Rgb(baseline.LowColor)); SetColor(chargingColor, Rgb(baseline.ChargingColor));
            enable.Checked = baseline.Alerts; criticalEnable.Checked = baseline.CriticalEnabled; low.Value = baseline.Low; critical.Value = baseline.Critical;
            UpdateEnabled(); message.Text = "";
        }
        void UpdateEnabled()
        {
            fixedColor.Enabled = mode.SelectedIndex == 0;
            normalColor.Enabled = lowColor.Enabled = mode.SelectedIndex == 1;
            chargingColor.Enabled = mode.SelectedIndex == 1 || mode.SelectedIndex == 3;
            criticalEnable.Enabled = enable.Checked; critical.Enabled = enable.Checked && criticalEnable.Checked;
        }
        void Apply()
        {
            var c = (Color)fixedColor.Tag;
            var value = baseline with { Mode = new[] { "fixed", "battery", "off", "gradient" }[mode.SelectedIndex], R = c.R, G = c.G, B = c.B,
                Alerts = enable.Checked, Low = (int)low.Value, Critical = (int)critical.Value, CriticalEnabled = criticalEnable.Checked,
                NormalColor = Value(normalColor), LowColor = Value(lowColor), ChargingColor = Value(chargingColor), Stops = gradient.Stops, KeepLight = keepLight.Checked, LightRefreshSeconds = (int)lightRefresh.Value };
            if (!ValidSettings(value)) { MessageBox.Show(this, "阈值必须是 10 的倍数，且第二次提醒阈值必须低于低电量阈值。", "检查提醒范围"); return; }
            if (save(value))
            {
                gradient.StopPreview(); baseline = value;
                try
                {
                    if (startup.Enabled && startup.Checked != startupBaseline)
                    {
                        Startup.Set(startup.Checked); startupBaseline = startup.Checked;
                        startupStatus.Text = startupBaseline ? "当前已设置自动运行" : "当前未设置自动运行";
                    }
                    message.Text = "已保存并应用";
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                { message.Text = "启动设置未保存"; MessageBox.Show(this, "其他设置已保存，但自动运行设置失败：" + e.Message, "自动运行"); }
            }
        }
    }
}
