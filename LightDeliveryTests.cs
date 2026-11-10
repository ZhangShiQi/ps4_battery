using System.Drawing;

public partial class Program
{
    static async Task TestLightDelivery()
    {
        var savedSettings = lightSettings;
        var savedPreview = previewColor;
        var savedStatus = lightStatus;
        try
        {
            var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var epoch = clock;
            var reading = new Reading(5, false, "", clock);
            var device = new Device("test", 0x09cc, 78);
            var writes = new List<byte[]>();
            int physicalColor = 0;
            lightSettings = new LightSettings(Mode: "fixed", R: 30, G: 60, B: 90, KeepLight: false);
            previewColor = null;
            using (var writer = new LightWriter(device, () => clock, (packet, _) =>
            {
                writes.Add(packet);
                // Simulate the first write succeeding in Windows but being ignored by the pad.
                if (writes.Count > 1) physicalColor = packet[8] << 16 | packet[9] << 8 | packet[10];
                return Task.CompletedTask;
            }))
            {
                await writer.Update(reading, default);
                if (writes.Count != 1 || physicalColor != 0) throw new Exception("Initial delivery simulation");
                clock = epoch.AddMilliseconds(999); await writer.Update(reading, default);
                if (writes.Count != 1) throw new Exception("Premature retry");
                clock = epoch.AddSeconds(1); await writer.Update(reading, default);
                if (physicalColor != 0x1e3c5a) throw new Exception("Ignored initial write did not recover");
                foreach (int second in new[] { 3, 6, 10, 11, 60, 600 })
                { clock = epoch.AddSeconds(second); await writer.Update(reading, default); }
                if (writes.Count != 5) throw new Exception("Startup retries must be bounded");
                lightSettings = lightSettings with { Mode = "off" };
                clock = epoch.AddSeconds(601); await writer.Update(reading, default);
                if (writes.Count != 6 || physicalColor != 0) throw new Exception("Setting changes after retry window");
            }
            // A reconnect gets its own initialization window, even for the same color.
            using (var writer = new LightWriter(device, () => clock, (p, _) => { writes.Add(p); return Task.CompletedTask; }))
            {
                await writer.Update(reading, default);
                clock = clock.AddSeconds(1); await writer.Update(reading, default);
                if (writes.Count != 8) throw new Exception("Reconnect did not reapply off mode");
            }
            // Simulate an external blue overwrite long after initialization, without an input change.
            foreach (var mode in new[] { "fixed", "battery", "gradient", "off" })
            {
                lightSettings = new LightSettings(Mode: mode);
                clock = epoch; int count = 0; int actual = 0;
                using var writer = new LightWriter(device, () => clock, (packet, _) =>
                { count++; actual = packet[8] << 16 | packet[9] << 8 | packet[10]; return Task.CompletedTask; });
                foreach (int second in new[] { 0, 1, 3, 6, 10, 60 })
                { clock = epoch.AddSeconds(second); await writer.Update(reading, default); }
                int expected = LightColor(lightSettings, reading).Value.ToArgb() & 0xffffff;
                actual = 0x0000ff; int before = count;
                clock = epoch.AddSeconds(64.9); await writer.Update(reading, default);
                if (count != before) throw new Exception("Keep-light writes too often");
                clock = epoch.AddSeconds(65); await writer.Update(reading, default);
                if (actual != expected || count != before + 1) throw new Exception("Late external overwrite not repaired: " + mode);
                lightSettings = lightSettings with { KeepLight = false };
                actual = 0x0000ff; before = count;
                clock = epoch.AddSeconds(100); await writer.Update(reading, default);
                if (count != before || actual != 0x0000ff) throw new Exception("Keep-light disable ignored");
            }
            if (!System.Text.Json.JsonSerializer.Deserialize<LightSettings>("{\"Mode\":\"gradient\"}").KeepLight)
                throw new Exception("Existing settings migration failed");
            int attempts = 0;
            if (ValidSettings(new(LightRefreshSeconds: 0)) || ValidSettings(new(LightRefreshSeconds: 61)) ||
                !ValidSettings(new(LightRefreshSeconds: 1)) || !ValidSettings(new(LightRefreshSeconds: 60)))
                throw new Exception("Refresh interval validation");
            if (System.Text.Json.JsonSerializer.Deserialize<LightSettings>("{\"Mode\":\"gradient\"}").LightRefreshSeconds != 5)
                throw new Exception("Legacy refresh default");
            foreach (int interval in new[] { 1, 7, 30, 60 })
            {
                clock = epoch;
                lightSettings = new LightSettings(Mode: "fixed", LightRefreshSeconds: interval);
                int count = 0;
                using var writer = new LightWriter(device, () => clock, (_, _) => { count++; return Task.CompletedTask; });
                await writer.Update(reading, default);
                clock = epoch.AddSeconds(60); await writer.Update(reading, default);
                int before = count;
                clock = epoch.AddSeconds(60 + interval).AddMilliseconds(-1); await writer.Update(reading, default);
                if (count != before) throw new Exception("Refresh before configured deadline");
                clock = epoch.AddSeconds(60 + interval); await writer.Update(reading, default);
                if (count != before + 1) throw new Exception("Configured refresh missed");
                lightSettings = lightSettings with { LightRefreshSeconds = 2 };
                clock = clock.AddSeconds(2); await writer.Update(reading, default);
                if (count != before + 2) throw new Exception("Interval change requires restart");
            }
            lightSettings = new LightSettings(Mode: "fixed", R: 30, G: 60, B: 90);
            using (var writer = new LightWriter(device, () => clock, (_, _) =>
            {
                attempts++;
                if (attempts == 2) throw new IOException("Simulated write failure");
                return Task.CompletedTask;
            }))
            {
                await writer.Update(reading, default);
                clock = clock.AddSeconds(1);
                lightSettings = lightSettings with { R = 255 };
                await writer.Update(reading, default);
                lightSettings = lightSettings with { R = 30 }; // Same as the old successful color.
                clock = clock.AddSeconds(9); await writer.Update(reading, default);
                if (attempts != 2) throw new Exception("Failure backoff ignored");
                clock = clock.AddSeconds(1); await writer.Update(reading, default);
                if (attempts != 3) throw new Exception("Old color cache suppressed recovery");
            }
        }
        finally { lightSettings = savedSettings; previewColor = savedPreview; lightStatus = savedStatus; }
    }
}
