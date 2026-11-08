using System.Drawing;
using System.Windows.Forms;

public partial class Program
{
    sealed class GradientEditor : UserControl
    {
        readonly Band band = new();
        readonly NumericUpDown position = new() { Minimum = 0, Maximum = 100, Width = 70 };
        readonly Button color = new() { Text = "节点颜色…", AutoSize = true };
        readonly Button remove = new() { Text = "删除节点", AutoSize = true };
        readonly TrackBar simulation = new() { Minimum = 0, Maximum = 100, Value = 50, TickFrequency = 10, Width = 530, Height = 45 };
        readonly Label sample = new() { AutoSize = false, Width = 530, Height = 48, TextAlign = ContentAlignment.MiddleCenter };
        readonly CheckBox hardware = new() { Text = "在手柄上预览（临时覆盖灯条颜色）", AutoSize = true };
        bool updating;
        public event Action Changed;
        public ColorStop[] Stops { get => band.Points.ToArray(); set { band.Points = (value ?? DefaultStops()).ToList(); band.Selected = 0; Sync(); } }
        public void StopPreview() { hardware.Checked = false; Volatile.Write(ref previewColor, null); }
        public void Pulse()
        {
            if (hardware.Checked && Visible && Enabled)
                Volatile.Write(ref previewColor, new PreviewColor(GradientColor(Stops, simulation.Value), DateTime.UtcNow.AddSeconds(3)));
            else Volatile.Write(ref previewColor, null);
            var c = GradientColor(Stops, simulation.Value);
            sample.Text = $"模拟 {simulation.Value}% → #{c.R:X2}{c.G:X2}{c.B:X2}" + (hardware.Checked ? "\\n".Replace("\\n", "\n") + lightStatus : "");
        }
        public GradientEditor()
        {
            Width = 550; Height = 326;
            var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            band.Size = new Size(530, 100); layout.Controls.Add(band);
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            row.Controls.Add(new Label { Text = "节点电量 %", AutoSize = true, Margin = new Padding(0, 7, 8, 0) }); row.Controls.Add(position); row.Controls.Add(color); row.Controls.Add(remove);
            var add = new Button { Text = "添加节点", AutoSize = true }; row.Controls.Add(add); layout.Controls.Add(row);
            layout.Controls.Add(new Label { Text = "模拟电量（不影响电量提醒）", AutoSize = true });
            layout.Controls.Add(simulation); layout.Controls.Add(sample); layout.Controls.Add(hardware); Controls.Add(layout);
            band.Edited += () => { Sync(); Changed?.Invoke(); };
            band.SelectedChanged += Sync;
            band.PickColor += Pick;
            position.ValueChanged += (_, _) =>
            {
                if (updating) return;
                band.MoveSelected((int)position.Value); Sync(); Changed?.Invoke();
            };
            color.Click += (_, _) => Pick();
            remove.Click += (_, _) =>
            {
                if (band.Selected == 0 || band.Selected == band.Points.Count - 1) return;
                band.Points.RemoveAt(band.Selected); band.Selected--; Sync(); Changed?.Invoke();
            };
            add.Click += (_, _) =>
            {
                if (band.Points.Count >= 16) return;
                int desired = simulation.Value;
                int at = Enumerable.Range(1, 99).Where(x => band.Points.All(p => p.Percent != x)).OrderBy(x => Math.Abs(x - desired)).First();
                band.Points.Add(new ColorStop(at, GradientColor(Stops, at).ToArgb() & 0xffffff));
                band.Points = band.Points.OrderBy(p => p.Percent).ToList(); band.Selected = band.Points.FindIndex(p => p.Percent == at);
                Sync(); Changed?.Invoke();
            };
            simulation.ValueChanged += (_, _) => Preview(); hardware.CheckedChanged += (_, _) => Pulse();
            VisibleChanged += (_, _) => { if (!Visible) StopPreview(); };
            EnabledChanged += (_, _) => { if (!Enabled) StopPreview(); };
            Disposed += (_, _) => Volatile.Write(ref previewColor, null);
            Sync();
        }
        void Pick()
        {
            using var picker = new ColorDialog { FullOpen = true, Color = Rgb(band.Points[band.Selected].Color) };
            if (picker.ShowDialog(this) == DialogResult.OK)
            {
                band.Points[band.Selected] = band.Points[band.Selected] with { Color = picker.Color.ToArgb() & 0xffffff };
                Sync(); Changed?.Invoke();
            }
        }
        void Sync()
        {
            updating = true;
            position.Value = band.Points[band.Selected].Percent;
            position.Enabled = remove.Enabled = band.Selected > 0 && band.Selected < band.Points.Count - 1;
            color.BackColor = Rgb(band.Points[band.Selected].Color);
            updating = false; band.Invalidate(); Preview();
        }
        void Preview()
        {
            var c = GradientColor(Stops, simulation.Value);
            sample.BackColor = c; sample.ForeColor = c.R * .299 + c.G * .587 + c.B * .114 < 140 ? Color.White : Color.Black;
            sample.Text = $"模拟 {simulation.Value}% → #{c.R:X2}{c.G:X2}{c.B:X2}"; Pulse();
        }
        sealed class Band : Control
        {
            public List<ColorStop> Points = DefaultStops().ToList();
            public int Selected;
            public event Action Edited, SelectedChanged, PickColor;
            bool dragging;
            public Band() { DoubleBuffered = true; Cursor = Cursors.Hand; AccessibleName = "电量颜色渐变色带，拖动节点，双击节点选颜色"; }
            float X(int p) => 15 + (Width - 30) * p / 100f;
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                for (int x = 15; x < Width - 15; x++)
                { using var pen = new Pen(GradientColor(Points.ToArray(), (x - 15) * 100.0 / (Width - 30))); e.Graphics.DrawLine(pen, x, 12, x, 45); }
                for (int p = 0; p <= 100; p += 20)
                    e.Graphics.DrawString(p + "%", Font, Brushes.DimGray, X(p) - 12, 76);
                for (int i = 0; i < Points.Count; i++)
                {
                    float x = X(Points[i].Percent);
                    using var brush = new SolidBrush(Rgb(Points[i].Color));
                    e.Graphics.FillEllipse(brush, x - 7, 49, 14, 14);
                    using var pen = new Pen(i == Selected ? Color.Black : Color.Gray, i == Selected ? 3 : 1);
                    e.Graphics.DrawEllipse(pen, x - 7, 49, 14, 14);
                }
            }
            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left) return;
                int nearest = Enumerable.Range(0, Points.Count).OrderBy(i => Math.Abs(X(Points[i].Percent) - e.X)).First();
                if (Math.Abs(X(Points[nearest].Percent) - e.X) > 12) return;
                Selected = nearest; dragging = true; Capture = true; SelectedChanged?.Invoke();
            }
            public void MoveSelected(int at)
            {
                if (Selected == 0 || Selected == Points.Count - 1) return;
                Points[Selected] = Points[Selected] with { Percent = Math.Clamp(at, Points[Selected - 1].Percent + 1, Points[Selected + 1].Percent - 1) };
            }
            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e); if (!dragging) return;
                MoveSelected((int)Math.Round((e.X - 15) * 100.0 / (Width - 30))); Edited?.Invoke();
            }
            protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; }
            protected override void OnMouseDoubleClick(MouseEventArgs e) { base.OnMouseDoubleClick(e); if (e.Button == MouseButtons.Left) PickColor?.Invoke(); }
        }
    }
}
