using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Numerics;

public partial class Program
{
    sealed class ControllerTestWindow : Form
    {
        readonly TestCanvas canvas = new() { Dock = DockStyle.Fill };
        readonly System.Windows.Forms.Timer refresh = new() { Interval = 33 };
        public ControllerTestWindow()
        {
            Text = "PS4 手柄功能测试"; ClientSize = new Size(1240, 820); MinimumSize = new Size(1000, 700);
            StartPosition = FormStartPosition.CenterScreen; BackColor = Color.FromArgb(16, 18, 23);
            Font = new Font("Microsoft YaHei UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 54, Padding = new Padding(12, 8, 0, 0), BackColor = Color.FromArgb(27, 30, 38) };
            void Button(string text, Action action)
            {
                var b = new Button { Text = text, AutoSize = true, BackColor = Color.FromArgb(45, 49, 61), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Height = 34 };
                b.Click += (_, _) => action(); toolbar.Controls.Add(b);
            }
            Button("左马达 · 1 秒", () => StartRumble(140, 0));
            Button("右马达 · 1 秒", () => StartRumble(0, 140));
            Button("双马达 · 1 秒", () => StartRumble(100, 100));
            Button("停止震动", StopRumble);
            Button("重置测试记录", canvas.Reset);
            Button("陀螺仪显示归零", canvas.ZeroGyro);
            Button("姿态归零", canvas.ZeroPose);
            var poseToggle = new CheckBox { Text = "3D 姿态", Checked = true, AutoSize = true, ForeColor = Color.White, Margin = new Padding(8, 8, 0, 0) };
            poseToggle.CheckedChanged += (_, _) => { canvas.ShowPose = poseToggle.Checked; canvas.Invalidate(); };
            toolbar.Controls.Add(poseToggle);
            Controls.Add(canvas); Controls.Add(toolbar);
            refresh.Tick += (_, _) => canvas.Tick(); refresh.Start();
            FormClosing += (_, _) => StopRumble();
            FormClosed += (_, _) => refresh.Dispose();
        }
        sealed class TestCanvas : Control
        {
            static readonly Color Accent = Color.FromArgb(38, 215, 169), Muted = Color.FromArgb(153, 161, 180), Ink = Color.FromArgb(233, 237, 245);
            static readonly string[] Names = { "↑", "→", "↓", "←", "□", "×", "○", "△", "L1", "R1", "L2", "R2", "SHARE", "OPTIONS", "L3", "R3", "PS", "触摸板" };
            readonly HashSet<string> seen = new();
            readonly Queue<float[]> history = new();
            InputState state;
            DateTime lastSample;
            float[] bias = new float[3];
            double maxLeft, maxRight;
            int maxL2, maxR2;
            bool zeroed;
            public bool ShowPose = true;
            PoseReading pose;
            Quaternion poseOrigin = Quaternion.Identity;
            int poseSessionId = -1;
            public void ZeroPose()
            {
                var current = Volatile.Read(ref poseReading);
                if (current == null || (DateTime.UtcNow - current.Received).TotalSeconds > 1) return;
                poseOrigin = current.Rotation; poseSessionId = current.Session; Invalidate();
            }
            public TestCanvas() { DoubleBuffered = true; ResizeRedraw = true; }
            public void Reset() { seen.Clear(); history.Clear(); maxLeft = maxRight = 0; maxL2 = maxR2 = 0; bias = new float[3]; zeroed = false; Invalidate(); }
            public void ZeroGyro()
            {
                if (state == null) return;
                bias = new float[] { state.GX, state.GY, state.GZ }; zeroed = true; history.Clear();
            }
            public void Tick()
            {
                pose = Volatile.Read(ref poseReading);
                if (pose == null || (DateTime.UtcNow - pose.Received).TotalSeconds > 1) { pose = null; poseSessionId = -1; }
                else if (pose.Session != poseSessionId) { poseOrigin = pose.Rotation; poseSessionId = pose.Session; }
                var next = Volatile.Read(ref inputState);
                if (next == null || (DateTime.UtcNow - next.Received).TotalSeconds > 1)
                { state = null; history.Clear(); bias = new float[3]; zeroed = false; Invalidate(); return; }
                state = next;
                if (next.Received != lastSample)
                {
                    lastSample = next.Received;
                    foreach (var name in Names) if (next.Pressed(name)) seen.Add(name);
                    maxLeft = Math.Max(maxLeft, Math.Sqrt(Math.Pow(Axis(next.LX), 2) + Math.Pow(Axis(next.LY), 2)));
                    maxRight = Math.Max(maxRight, Math.Sqrt(Math.Pow(Axis(next.RX), 2) + Math.Pow(Axis(next.RY), 2)));
                    maxL2 = Math.Max(maxL2, next.L2); maxR2 = Math.Max(maxR2, next.R2);
                    history.Enqueue(new float[] { next.GX - bias[0], next.GY - bias[1], next.GZ - bias[2] });
                    while (history.Count > 120) history.Dequeue();
                }
                Invalidate();
            }
            static void DrawText(Graphics g, string text, float x, float y, float size = 12, Color? color = null)
            { using var f = new Font("Microsoft YaHei UI", size, FontStyle.Regular, GraphicsUnit.Pixel); using var b = new SolidBrush(color ?? Ink); g.DrawString(text, f, b, x, y); }
            static void Box(Graphics g, float x, float y, float w, float h, Color color)
            { using var b = new SolidBrush(color); g.FillRectangle(b, x, y, w, h); }
            static void Card(Graphics g, int x, int y, int w, int h, string title)
            {
                Box(g, x, y, w, h, Color.FromArgb(27, 30, 38));
                using var p = new Pen(Color.FromArgb(51, 57, 70)); g.DrawRectangle(p, x, y, w, h);
                DrawText(g, title, x + 18, y + 14, 17, Accent);
            }
            void Key(Graphics g, string name, float x, float y, float width = 40, bool circle = false)
            {
                bool pressed = state?.Pressed(name) == true;
                using var b = new SolidBrush(pressed ? Accent : Color.FromArgb(54, 59, 73));
                using var p = new Pen(seen.Contains(name) ? Accent : Color.FromArgb(100, 107, 123), 1.5f);
                if (circle) { g.FillEllipse(b, x, y, width, 40); g.DrawEllipse(p, x, y, width, 40); }
                else { g.FillRectangle(b, x, y, width, 30); g.DrawRectangle(p, x, y, width, 30); }
                DrawText(g, name, x + 6, y + 7, name.Length > 4 ? 10 : 15, pressed ? Color.Black : Ink);
            }
            void Stick(Graphics g, float x, float y, byte ax, byte ay, string label, bool valid, float radius = 48)
            {
                using var pen = new Pen(Color.FromArgb(77, 83, 99));
                g.DrawEllipse(pen, x - radius, y - radius, radius * 2, radius * 2);
                g.DrawLine(pen, x - radius, y, x + radius, y); g.DrawLine(pen, x, y - radius, x, y + radius);
                if (valid)
                { using var b = new SolidBrush(Accent); g.FillEllipse(b, x + (float)Axis(ax) * radius - 5, y + (float)Axis(ay) * radius - 5, 10, 10); }
                if (radius < 40) return;
                DrawText(g, label, x - radius, y + radius + 8, 12);
                DrawText(g, valid ? $"{Axis(ax),5:F3}  {Axis(ay),5:F3}" : "等待输入", x - radius, y + radius + 26, 11, Muted);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e); var g = e.Graphics; g.Clear(Color.FromArgb(16, 18, 23));
                float scale = Math.Min(Width / 1200f, Height / 740f);
                g.TranslateTransform((Width - 1200 * scale) / 2, (Height - 740 * scale) / 2); g.ScaleTransform(scale, scale);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                bool online = state != null;
                DrawText(g, "DUALSHOCK 4  /  功能测试", 18, 10, 23);
                DrawText(g, online ? $"{(state.Bluetooth ? "蓝牙" : "USB")} · 报告 {state.Received.ToLocalTime():HH:mm:ss.fff} · {(latest?.Text ?? "")}" : "未连接或数据已过期 · 按 PS 键连接手柄", 18, 43, 12, online ? Accent : Color.Orange);
                if (ShowPose)
                {
                    Card(g, 16, 76, 530, 440, "3D 手柄姿态 / 随手柄转动");
                    var rotation = pose == null ? Quaternion.Identity : Quaternion.Normalize(Quaternion.Conjugate(poseOrigin) * pose.Rotation);
                    DrawPoseModel(g, rotation, new RectangleF(30, 128, 502, 300), state);
                    DrawText(g, pose == null ? "等待手柄六轴数据…" : pose.Calibrated ? "实时跟随 · 已读取出厂标定" : "实时跟随 · 使用默认灵敏度（未读取出厂标定）", 36, 435, 12, pose == null ? Muted : Accent);
                    DrawText(g, "黄色 = 按下 · 摇杆随动 · L2/R2 填充 = 力度 · 触摸点实时显示", 36, 458, 12, Muted);
                    DrawText(g, "姿态归零：将当前方向设为正面；取消 3D 可查看按键", 36, 479, 12, Muted);
                    DrawText(g, "水平朝向可能缓慢漂移，归零即可重新对齐", 36, 498, 11, Muted);
                }
                else
                {
                Card(g, 16, 76, 530, 440, "手柄可视化");
                using (var path = new GraphicsPath())
                {
                    path.AddBezier(140, 185, 50, 190, 50, 340, 40, 410);
                    path.AddBezier(40, 410, 35, 490, 110, 500, 154, 405);
                    path.AddBezier(154, 405, 185, 365, 380, 365, 410, 405);
                    path.AddBezier(410, 405, 455, 500, 525, 490, 520, 410);
                    path.AddBezier(520, 410, 510, 330, 510, 190, 420, 185);
                    path.CloseFigure(); using var b = new SolidBrush(Color.FromArgb(37, 41, 51)); using var p = new Pen(Color.FromArgb(90, 97, 114), 3);
                    g.FillPath(b, path); g.DrawPath(p, path);
                }
                Key(g, "L2", 120, 116, 66); Key(g, "R2", 380, 116, 66);
                Key(g, "L1", 120, 152, 66); Key(g, "R1", 380, 152, 66);
                Key(g, "触摸板", 220, 201, 120); Key(g, "SHARE", 172, 212, 42); Key(g, "OPTIONS", 347, 212, 58);
                Key(g, "↑", 111, 240); Key(g, "←", 68, 274); Key(g, "→", 154, 274); Key(g, "↓", 111, 308);
                Key(g, "△", 422, 236, 40, true); Key(g, "□", 378, 276, 40, true); Key(g, "○", 466, 276, 40, true); Key(g, "×", 422, 316, 40, true);
                Stick(g, 211, 336, state?.LX ?? 128, state?.LY ?? 128, "", online, 30);
                Stick(g, 350, 336, state?.RX ?? 128, state?.RY ?? 128, "", online, 30);
                Key(g, "L3", 191, 381); Key(g, "R3", 330, 381); Key(g, "PS", 262, 311, 40, true);
                DrawText(g, $"已触发 {seen.Count}/{Names.Length} 项  ·  填充高亮 = 正在按下 / 描边 = 曾按下", 40, 485, 12, Muted);
                }
                Card(g, 562, 76, 300, 230, "摇杆 / 中心偏移");
                Stick(g, 640, 176, state?.LX ?? 128, state?.LY ?? 128, "左摇杆", online, 48);
                Stick(g, 782, 176, state?.RX ?? 128, state?.RY ?? 128, "右摇杆", online, 48);
                DrawText(g, $"最大半径  L {maxLeft:F3} / R {maxRight:F3}", 580, 280, 11, Muted);
                Card(g, 878, 76, 306, 230, "线性扳机 / L2 · R2");
                Trigger(g, 908, state?.L2 ?? 0, "L2", maxL2, online); Trigger(g, 1045, state?.R2 ?? 0, "R2", maxR2, online);
                Card(g, 562, 322, 300, 194, "双指触摸板");
                Box(g, 582, 366, 260, 115, Color.FromArgb(44, 49, 61));
                Touch(g, state?.Touch1, Color.FromArgb(38, 215, 169)); Touch(g, state?.Touch2, Color.FromArgb(180, 135, 255));
                DrawText(g, online ? $"T1 {(state.Touch1.Active ? $"{state.Touch1.X},{state.Touch1.Y}" : "—")}   T2 {(state.Touch2.Active ? $"{state.Touch2.X},{state.Touch2.Y}" : "—")}" : "等待输入", 582, 490, 11, Muted);
                Card(g, 878, 322, 306, 194, "震动 / 测试提示");
                DrawText(g, "使用上方按钮测试左右马达\n停止、关窗或断连会请求停止震动\n\n摇杆回中时观察偏移数值\n请勿仅凭一次偏移判定硬件损坏", 897, 365, 13, Muted);
                Card(g, 16, 532, 730, 170, "陀螺仪 / 三轴角速度原始计数");
                var values = online ? new float[] { state.GX - bias[0], state.GY - bias[1], state.GZ - bias[2] } : new float[3];
                var colors = new[] { Color.FromArgb(255, 130, 130), Accent, Color.FromArgb(130, 170, 255) };
                for (int i = 0; i < 3; i++) DrawText(g, $"{new[] { "X", "Y", "Z" }[i]}  {(online ? values[i].ToString("F0") : "—")}", 36 + i * 170, 568, 15, colors[i]);
                var samples = history.ToArray(); float bound = Math.Max(500, samples.SelectMany(s => s).Select(Math.Abs).DefaultIfEmpty().Max());
                Box(g, 36, 598, 690, 69, Color.FromArgb(20, 23, 29));
                for (int axis = 0; axis < 3; axis++)
                { using var p = new Pen(colors[axis], 1.5f); for (int i = 1; i < samples.Length; i++) g.DrawLine(p, 36 + (i - 1) * 690f / 119, 632 - samples[i - 1][axis] / bound * 30, 36 + i * 690f / 119, 632 - samples[i][axis] / bound * 30); }
                DrawText(g, $"约 4 秒波形 · 自动量程 ±{bound:F0} · {(zeroed ? "已减去显示零偏" : "未归零")} · 非硬件标定 / 非绝对姿态", 36, 677, 11, Muted);
                Card(g, 762, 532, 422, 170, "加速度 / 原始计数（含重力）");
                DrawText(g, online ? $"X  {state.AX,7}\nY  {state.AY,7}\nZ  {state.AZ,7}" : "等待传感器输入", 782, 572, 17);
                DrawText(g, "静止时也有重力分量\n转动或倾斜手柄观察变化", 953, 582, 12, Muted);
                DrawText(g, rumbleStatus, 18, 715, 12, Muted);
            }
            static void Trigger(Graphics g, int x, byte value, string name, int peak, bool online)
            {
                Box(g, x, 126, 96, 116, Color.FromArgb(44, 49, 61));
                Box(g, x, 242 - value / 255f * 116, 96, value / 255f * 116, Accent);
                DrawText(g, online ? $"{name}  {value / 255.0:F3}" : name + "  —", x, 250, 13);
                DrawText(g, $"原始 {value} / 峰值 {peak}", x, 275, 11, Muted);
            }
            static void Touch(Graphics g, TouchPoint p, Color color)
            {
                if (p?.Active != true) return;
                float x = 582 + Math.Clamp(p.X / 1920f, 0, 1) * 260, y = 366 + Math.Clamp(p.Y / 942f, 0, 1) * 115;
                using var b = new SolidBrush(color); g.FillEllipse(b, x - 6, y - 6, 12, 12); DrawText(g, p.Id.ToString(), x + 9, y - 8, 11, color);
            }
        }
    }
}
