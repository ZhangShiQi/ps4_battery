using System.Drawing;
using System.Text.Json;

public partial class Program
{
    record LightSettings(string Mode = "battery", int R = 128, int G = 0, int B = 255, bool Alerts = true, int Low = 20, int Critical = 10, bool CriticalEnabled = true, int NormalColor = 0x00FF00, int LowColor = 0xFF0000, int ChargingColor = 0x0000FF, ColorStop[] Stops = null);
    record ColorStop(int Percent, int Color);
    record PreviewColor(Color Color, DateTime Expires);
    static PreviewColor previewColor;
    static ColorStop[] DefaultStops() => new[] { new ColorStop(0, 0xff0000), new ColorStop(20, 0xff8000), new ColorStop(50, 0xffff00), new ColorStop(100, 0x00ff00) };
    static bool ValidStops(ColorStop[] stops) => stops == null || (stops.Length >= 2 && stops.Length <= 16 &&
        stops.All(s => s != null && s.Percent >= 0 && s.Percent <= 100 && s.Color >= 0 && s.Color <= 0xffffff) &&
        stops[0].Percent == 0 && stops[^1].Percent == 100 && stops.Zip(stops.Skip(1)).All(p => p.First.Percent < p.Second.Percent));
    static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        (int)Math.Round(a.R + (b.R - a.R) * t), (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));
    static Color GradientColor(ColorStop[] stops, double percent)
    {
        stops ??= DefaultStops();
        percent = Math.Clamp(percent, 0, 100);
        for (int i = 1; i < stops.Length; i++)
            if (percent <= stops[i].Percent)
                return Blend(Rgb(stops[i - 1].Color), Rgb(stops[i].Color), (percent - stops[i - 1].Percent) / (stops[i].Percent - stops[i - 1].Percent));
        return Rgb(stops[^1].Color);
    }
    static LightSettings lightSettings;
    static string lightStatus = "灯条：等待连接";
    static readonly string SettingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
    static Color Rgb(int value) => Color.FromArgb((value >> 16) & 255, (value >> 8) & 255, value & 255);
    static bool ValidSettings(LightSettings s) => ValidStops(s.Stops) && s.Low >= 10 && s.Low <= 90 && s.Low % 10 == 0 &&
        s.Critical >= 10 && s.Critical <= 80 && s.Critical % 10 == 0 && (!s.CriticalEnabled || s.Critical < s.Low) &&
        s.NormalColor >= 0 && s.NormalColor <= 0xffffff && s.LowColor >= 0 && s.LowColor <= 0xffffff && s.ChargingColor >= 0 && s.ChargingColor <= 0xffffff;

    static LightSettings LoadLights()
    {
        try
        {
            var s = JsonSerializer.Deserialize<LightSettings>(File.ReadAllText(SettingsPath));
            if (s != null && new[] { "fixed", "battery", "off", "gradient" }.Contains(s.Mode) &&
                s.R >= 0 && s.R <= 255 && s.G >= 0 && s.G <= 255 && s.B >= 0 && s.B <= 255 && ValidSettings(s)) return s;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return new();
    }

    static Color? LightColor(LightSettings s, Reading r) => s.Mode switch
    {
        "off" => Color.Black,
        "fixed" => Color.FromArgb(s.R, s.G, s.B),
        "gradient" => r.Charging ? Rgb(s.ChargingColor) : r.Level < 0 ? null : GradientColor(s.Stops, r.Level * 10),
        _ => r.Charging ? Rgb(s.ChargingColor) : r.Level < 0 ? null :
            r.Level * 10 < s.Low ? Rgb(s.LowColor) : Rgb(s.NormalColor)
    };

    static byte[] LightPacket(bool bt, Color color)
    {
        byte[] packet = new byte[bt ? 78 : 32];
        packet[0] = bt ? (byte)0x11 : (byte)0x05;
        if (bt) packet[1] = 0xc4;
        // Only LED color and blink flags; rumble/audio validity bits remain clear.
        packet[bt ? 3 : 1] = 0x06;
        int offset = bt ? 8 : 6;
        packet[offset] = color.R; packet[offset + 1] = color.G; packet[offset + 2] = color.B;
        if (bt) BitConverter.GetBytes(Crc(packet, 74, 0xa2)).CopyTo(packet, 74);
        return packet;
    }

    sealed class LightWriter(Device device) : IDisposable
    {
        FileStream output;
        int? sent;
        Color? target;
        Color from;
        DateTime transitionStart;
        DateTime nextAttempt;
        public async Task Update(Reading r, CancellationToken stop)
        {
            var settings = Volatile.Read(ref lightSettings);
            if (settings == null) return;
            var preview = Volatile.Read(ref previewColor);
            var color = preview != null && preview.Expires > DateTime.UtcNow ? preview.Color : LightColor(settings, r);
            if (color != null && target != color)
            {
                target = color; from = sent.HasValue ? Color.FromArgb(sent.Value) : color.Value;
                transitionStart = DateTime.UtcNow;
            }
            if (color != null && (settings.Mode == "gradient" || preview != null))
                color = Blend(from, color.Value, Math.Clamp((DateTime.UtcNow - transitionStart).TotalMilliseconds / 600, 0, 1));
            if (color == null || sent == color.Value.ToArgb() || DateTime.UtcNow < nextAttempt) return;
            nextAttempt = DateTime.UtcNow.AddMilliseconds(50);
            try
            {
                if (output == null)
                {
                    var handle = CreateFile(device.Path, 0x40000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                    if (handle.IsInvalid)
                    {
                        int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error(); handle.Dispose();
                        throw new IOException($"无法写入手柄（{error}）");
                    }
                    output = new FileStream(handle, FileAccess.Write, 1, true);
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                timeout.CancelAfter(2000);
                await output.WriteAsync(LightPacket(device.Length > 64, color.Value), timeout.Token);
                sent = color.Value.ToArgb();
                lightStatus = $"灯条：已发送 #{color.Value.R:X2}{color.Value.G:X2}{color.Value.B:X2}";
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                lightStatus = "灯条控制失败：" + e.Message;
                output?.Dispose(); output = null;
                nextAttempt = DateTime.UtcNow.AddSeconds(10);
            }
        }
        public void Dispose() => output?.Dispose();
    }

    static void TestLights()
    {
        var stops = new[] { new ColorStop(0, 0x000000), new ColorStop(100, 0xffffff) };
        if (GradientColor(stops, 50) != Rgb(0x808080) || GradientColor(stops, -10) != Rgb(0) || GradientColor(stops, 110) != Rgb(0xffffff)) throw new Exception("Gradient interpolation");
        if (ValidStops(new[] { new ColorStop(0, 0), new ColorStop(0, 1), new ColorStop(100, 2) }) || ValidStops(new[] { new ColorStop(20, 0), new ColorStop(100, 0) })) throw new Exception("Gradient validation");
        var gradient = new LightSettings(Mode: "gradient", Stops: DefaultStops());
        if (LightColor(gradient, new Reading(2, false, "", DateTime.UtcNow)) != Rgb(0xff8000) || LightColor(gradient, new Reading(2, true, "", DateTime.UtcNow)) != Rgb(gradient.ChargingColor)) throw new Exception("Gradient priority");
        if (!JsonSerializer.Deserialize<LightSettings>(JsonSerializer.Serialize(gradient)).Stops.SequenceEqual(gradient.Stops)) throw new Exception("Gradient persistence");
        if (ValidSettings(new(Low: 25)) || ValidSettings(new(Low: 20, Critical: 20)) || !ValidSettings(new(Low: 10, CriticalEnabled: false))) throw new Exception("Threshold validation");
        var policy = new AlertPolicy();
        if (policy.Update(4, false, 40, 20) || !policy.Update(3, false, 40, 20) || policy.Update(2, false, 40, 20) || !policy.Update(1, false, 40, 20)) throw new Exception("Custom thresholds");
        var r = new Reading(2, false, "", DateTime.UtcNow);
        if (LightColor(new(), r) != Color.FromArgb(0, 255, 0) ||
            LightColor(new(), r with { Level = 1 }) != Color.FromArgb(255, 0, 0) ||
            LightColor(new(), r with { Charging = true }) != Color.FromArgb(0, 0, 255) ||
            LightColor(new("off"), r) != Color.Black ||
            LightColor(new("fixed", 12, 34, 56), r) != Color.FromArgb(12, 34, 56)) throw new Exception("Light modes");
        foreach (bool bt in new[] { false, true })
        {
            var p = LightPacket(bt, Color.FromArgb(12, 34, 56));
            int offset = bt ? 8 : 6;
            if (p.Length != (bt ? 78 : 32) || p[bt ? 3 : 1] != 6 || p[offset] != 12 ||
                p[offset + 1] != 34 || p[offset + 2] != 56 || p[offset - 1] != 0 || p[offset - 2] != 0)
                throw new Exception("Light packet");
            if (bt && BitConverter.ToUInt32(p, 74) != Crc(p, 74, 0xa2)) throw new Exception("Output CRC");
        }
        var s = new LightSettings("fixed", 12, 34, 56);
        if (JsonSerializer.Deserialize<LightSettings>(JsonSerializer.Serialize(s)) != s) throw new Exception("Settings roundtrip");
    }
}
