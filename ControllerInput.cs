using System.Drawing;

public partial class Program
{
    record TouchPoint(bool Active, int Id, int X, int Y);
    record InputState(DateTime Received, bool Bluetooth, byte LX, byte LY, byte RX, byte RY,
        byte L2, byte R2, byte Buttons1, byte Buttons2, byte Buttons3,
        short GX, short GY, short GZ, short AX, short AY, short AZ, TouchPoint Touch1, TouchPoint Touch2)
    {
        public ushort SensorTick { get; init; }
        public int Hat => Buttons1 & 15;
        public bool Pressed(string name) => name switch
        {
            "↑" => Hat is 0 or 1 or 7, "→" => Hat is 1 or 2 or 3,
            "↓" => Hat is 3 or 4 or 5, "←" => Hat is 5 or 6 or 7,
            "□" => (Buttons1 & 16) != 0, "×" => (Buttons1 & 32) != 0,
            "○" => (Buttons1 & 64) != 0, "△" => (Buttons1 & 128) != 0,
            "L1" => (Buttons2 & 1) != 0, "R1" => (Buttons2 & 2) != 0,
            "L2" => (Buttons2 & 4) != 0, "R2" => (Buttons2 & 8) != 0,
            "SHARE" => (Buttons2 & 16) != 0, "OPTIONS" => (Buttons2 & 32) != 0,
            "L3" => (Buttons2 & 64) != 0, "R3" => (Buttons2 & 128) != 0,
            "PS" => (Buttons3 & 1) != 0, "触摸板" => (Buttons3 & 2) != 0, _ => false
        };
    }
    // Called only after Decode has checked report ID, size and Bluetooth CRC.
    static InputState ParseInput(byte[] b, bool bt, DateTime received)
    {
        int o = bt ? 3 : 1;
        short S(int i) => System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(o + i, 2));
        var inactive = new TouchPoint(false, 0, 0, 0);
        TouchPoint a = inactive, c = inactive;
        int count = b[o + 32];
        if (count > 0 && count <= (bt ? 4 : 3))
        {
            int t = o + 34 + (count - 1) * 9;
            TouchPoint Read(int p) => new((b[p] & 128) == 0, b[p] & 127,
                b[p + 1] | ((b[p + 2] & 15) << 8), (b[p + 2] >> 4) | (b[p + 3] << 4));
            a = Read(t); c = Read(t + 4);
        }
        return new(received, bt, b[o], b[o + 1], b[o + 2], b[o + 3], b[o + 7], b[o + 8],
            b[o + 4], b[o + 5], b[o + 6], S(12), S(14), S(16), S(18), S(20), S(22), a, c)
            { SensorTick = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o + 9, 2)) };
    }
    static double Axis(byte value) => value >= 128 ? (value - 128) / 127.0 : (value - 128) / 128.0;
    static byte[] MotorPacket(bool bt, byte left, byte right)
    {
        var p = LightPacket(bt, Color.Black);
        p[bt ? 3 : 1] = 1; // Motor only; never overwrite LEDs during a vibration test.
        p[bt ? 6 : 4] = right; p[bt ? 7 : 5] = left;
        if (bt) BitConverter.GetBytes(Crc(p, 74, 0xa2)).CopyTo(p, 74);
        return p;
    }
    static CancellationTokenSource rumbleStop;
    static Task rumbleTask;
    static readonly object rumbleLock = new();
    static string rumbleStatus = "每次测试 1 秒，自动停止";
    static void StopRumble() { lock (rumbleLock) rumbleStop?.Cancel(); }
    static void StartRumble(byte left, byte right)
    {
        lock (rumbleLock)
        {
            if (rumbleTask != null && !rumbleTask.IsCompleted) return;
            var device = Volatile.Read(ref connectedDevice);
            var input = Volatile.Read(ref inputState);
            if (device == null || input == null || (DateTime.UtcNow - input.Received).TotalSeconds > 1)
            { rumbleStatus = "请先连接手柄"; return; }
            rumbleStop?.Dispose(); rumbleStop = new(); var token = rumbleStop.Token;
            rumbleTask = Task.Run(async () =>
            {
                using var h = CreateFile(device.Path, 0x40000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                if (h.IsInvalid) { rumbleStatus = "无法打开震动输出接口"; return; }
                using var output = new FileStream(h, FileAccess.Write, 1, true);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(1500);
                    await output.WriteAsync(MotorPacket(device.Length > 64, left, right), timeout.Token);
                    rumbleStatus = "震动测试中…";
                    await Task.Delay(1000, token);
                }
                catch (OperationCanceledException) { }
                catch (IOException e) { rumbleStatus = "震动发送失败：" + e.Message; }
                finally
                {
                    // Stop even after cancellation. Use a separate timeout, not the cancelled token.
                    try
                    {
                        using var timeout = new CancellationTokenSource(1000);
                        await output.WriteAsync(MotorPacket(device.Length > 64, 0, 0), timeout.Token);
                        rumbleStatus = "已发送停止指令；是否振动请以手感确认";
                    }
                    catch (Exception e) when (e is IOException or OperationCanceledException)
                    { rumbleStatus = "停止指令未送达；请断开手柄以停止震动"; }
                }
            });
        }
    }
    static void TestInput()
    {
        foreach (bool bt in new[] { false, true })
        {
            var b = new byte[bt ? 78 : 64]; int o = bt ? 3 : 1;
            b[0] = bt ? (byte)17 : (byte)1;
            b[o] = 0; b[o + 1] = 128; b[o + 2] = 255; b[o + 3] = 128;
            b[o + 4] = 0xf1; b[o + 5] = 255; b[o + 6] = 3; b[o + 7] = 255; b[o + 8] = 100;
            b[o + 12] = 0xfe; b[o + 13] = 0xff; b[o + 18] = 0; b[o + 19] = 32;
            b[o + 9] = 0x34; b[o + 10] = 0x12;
            b[o + 32] = 1; int t = o + 34;
            b[t] = 7; b[t + 1] = 0x80; b[t + 2] = 0x37; b[t + 3] = 0x20; b[t + 4] = 0x80;
            var s = ParseInput(b, bt, DateTime.UtcNow);
            if (s.SensorTick != 0x1234 || s.GX != -2 || s.AX != 8192 || s.Touch1.X != 1920 || s.Touch1.Y != 515 || s.Touch2.Active ||
                !s.Pressed("↑") || !s.Pressed("→") || s.Pressed("↓") || !s.Pressed("L3") || !s.Pressed("触摸板") || s.L2 != 255)
                throw new Exception("Input report decoding");
            b[o + 32] = 0; if (ParseInput(b, bt, DateTime.UtcNow).Touch1.Active) throw new Exception("Stale touch");
            var packet = MotorPacket(bt, 120, 60);
            if (packet[bt ? 3 : 1] != 1 || packet[bt ? 6 : 4] != 60 || packet[bt ? 7 : 5] != 120)
                throw new Exception("Motor packet");
            if (bt && BitConverter.ToUInt32(packet, 74) != Crc(packet, 74, 0xa2)) throw new Exception("Motor CRC");
        }
        if (Axis(0) != -1 || Axis(128) != 0 || Axis(255) != 1) throw new Exception("Axis normalization");
    }
}
