using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public partial class Program
{
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_GetFeature(SafeFileHandle h, byte[] data, int length);

    record Device(string Path, ushort Product, int Length);
    record Reading(int Level, bool Charging, string Text, DateTime Received);
    static Reading latest;
    static InputState inputState;
    static Device connectedDevice;
    static void Log(string text) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");

    static IEnumerable<Device> Find()
    {
        HidD_GetHidGuid(out Guid g);
        IntPtr devices = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (devices == new IntPtr(-1)) throw new IOException("无法枚举 HID 设备。");
        var found = new List<Device>();
        try
        {
            for (uint i = 0; ; i++)
            {
                Interface inf = new() { Size = (uint)Marshal.SizeOf<Interface>() };
                if (!SetupDiEnumDeviceInterfaces(devices, IntPtr.Zero, ref g, i, ref inf)) break;
                SetupDiGetDeviceInterfaceDetail(devices, ref inf, IntPtr.Zero, 0, out uint size, IntPtr.Zero);
                if (size < 8) continue;
                IntPtr detail = Marshal.AllocHGlobal((int)size);
                string path;
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(devices, ref inf, detail, size, out _, IntPtr.Zero)) continue;
                    path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                }
                finally { Marshal.FreeHGlobal(detail); }
                using var meta = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (meta.IsInvalid) continue;
                Attr a = new() { Size = Marshal.SizeOf<Attr>() };
                if (!HidD_GetAttributes(meta, ref a) || a.Vendor != 0x054c ||
                    (a.Product != 0x05c4 && a.Product != 0x09cc)) continue;
                if (!HidD_GetPreparsedData(meta, out IntPtr pre)) continue;
                IntPtr caps = Marshal.AllocHGlobal(64);
                try
                {
                    if (HidP_GetCaps(pre, caps) < 0) continue;
                    int usage = (ushort)Marshal.ReadInt16(caps, 0);
                    int page = (ushort)Marshal.ReadInt16(caps, 2);
                    int length = (ushort)Marshal.ReadInt16(caps, 4);
                    if (page == 1 && (usage == 4 || usage == 5) && length >= 64)
                        found.Add(new(path, a.Product, length));
                }
                finally { Marshal.FreeHGlobal(caps); HidD_FreePreparsedData(pre); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devices); }
        return found;
    }

    // Bluetooth CRC includes the HID input transaction prefix 0xA1.
    static uint Crc(byte[] b, int count, byte prefix = 0xa1)
    {
        uint crc = 0xffffffff;
        for (int i = -1; i < count; i++)
        {
            crc ^= i < 0 ? prefix : b[i];
            for (int j = 0; j < 8; j++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        return ~crc;
    }

    static string Decode(byte[] b, int n, bool bluetooth)
    {
        int offset;
        if (bluetooth)
        {
            // Never interpret padded Bluetooth 0x01 minimal reports as USB battery data.
            // Windows may pad reports to the descriptor's maximum input length (547).
            if (n < 78 || b[0] != 0x11 || Crc(b, 74) != BitConverter.ToUInt32(b, 74)) return null;
            offset = 32;
        }
        else
        {
            if (n != 64 || b[0] != 1) return null;
            offset = 30;
        }
        byte status = b[offset];
        int level = status & 15;
        bool cable = (status & 16) != 0;
        string connection = bluetooth ? "蓝牙" : "USB";
        if (cable && level == 11) return $"{connection} | 电量 100% | 已充满";
        if (level > 10) return $"{connection} | 电量未知 | 异常状态 0x{status:X2}";
        string range = level == 10 ? "100%" : $"{level * 10}–{level * 10 + 9}%";
        return $"{connection} | 电量 {range} | {(cable ? "充电中" : "使用电池")}";
    }

    static async Task<bool> Monitor(Device device, bool once, CancellationToken stop)
    {
        using var h = CreateFile(device.Path, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (h.IsInvalid) throw new IOException($"无法打开手柄，Windows 错误 {Marshal.GetLastWin32Error()}。请检查 DS4Windows 独占模式或 HidHide。");
        bool bt = device.Length > 64;
        Log($"发现 DualShock 4（{device.Product:X4}），{(bt ? "蓝牙" : "USB")}，等待电量数据……");
        using var stream = new FileStream(h, FileAccess.Read, device.Length, true);
        byte[] b = new byte[device.Length];
        var started = DateTime.UtcNow;
        var displayed = DateTime.MinValue;
        var lastValid = DateTime.UtcNow;
        bool requested = false;
        string previous = null;
        using var light = new LightWriter(device);
        connectedDevice = device;
        while (!stop.IsCancellationRequested)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            int n;
            try { n = await stream.ReadAsync(b.AsMemory(), timeout.Token); }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested)
            { throw new IOException("5 秒内未收到报告，手柄可能已断开或休眠。"); }
            if (n == 0) throw new IOException("手柄连接已关闭。");
            string state = Decode(b, n, bt);
            if (state != null)
            {
                Volatile.Write(ref inputState, ParseInput(b, bt, DateTime.UtcNow));
                lastValid = DateTime.UtcNow;
                byte raw = b[bt ? 32 : 30];
                latest = new Reading((raw & 15) <= 10 ? raw & 15 : ((raw & 31) == 27 ? 10 : -1),
                    (raw & 16) != 0, state, DateTime.UtcNow);
                await light.Update(latest, stop);
                if (state != previous || (DateTime.UtcNow - displayed).TotalSeconds >= 30)
                { Log(state); previous = state; displayed = DateTime.UtcNow; }
                if (once) return true;
            }
            else if (bt && !requested && (DateTime.UtcNow - started).TotalSeconds >= 1)
            {
                // Reading DS4 calibration feature 0x02 requests enhanced input reports.
                byte[] feature = new byte[37]; feature[0] = 2;
                bool ok = HidD_GetFeature(h, feature, feature.Length);
                requested = true;
                Log(ok ? "已请求蓝牙完整报告。" : $"请求完整报告失败（{Marshal.GetLastWin32Error()}），继续等待。");
            }
            if ((DateTime.UtcNow - lastValid).TotalSeconds > 8)
                throw new IOException($"已收到输入，但没有有效电量报告（报告 ID 0x{b[0]:X2}，长度 {n}）。请检查手柄兼容性或其他手柄软件。");
        }
        return false;
    }

    static void SelfTest()
    {
        byte[] usb = new byte[64]; usb[0] = 1; usb[30] = 7;
        if (Decode(usb, 64, false) != "USB | 电量 70–79% | 使用电池") throw new Exception("USB decode");
        if (Decode(usb, 64, true) != null) throw new Exception("Minimal report rejected");
        usb[30] = 0x1b;
        if (!Decode(usb, 64, false).Contains("已充满")) throw new Exception("Full");
        usb[30] = 0x1f;
        if (!Decode(usb, 64, false).Contains("未知")) throw new Exception("Error");
        byte[] bt = new byte[78]; bt[0] = 0x11; bt[32] = 0x13;
        BitConverter.GetBytes(Crc(bt, 74)).CopyTo(bt, 74);
        if (Decode(bt, 78, true) != "蓝牙 | 电量 30–39% | 充电中") throw new Exception("BT decode");
        byte[] padded = new byte[547]; bt.CopyTo(padded, 0);
        if (Decode(padded, 547, true) != Decode(bt, 78, true)) throw new Exception("Windows padding");
        bt[40] ^= 1;
        if (Decode(bt, 78, true) != null || Decode(bt, 10, true) != null) throw new Exception("Corruption");
        Console.WriteLine("PASS: USB / Bluetooth / charging / full / unknown / minimal / truncated / CRC corruption");
    }

    static async Task<int> ConsoleMain(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Contains("--self-test")) { SelfTest(); return 0; }
        bool once = args.Contains("--once");
        Console.WriteLine("PS4 手柄电量读取器\n电量为手柄报告的区间；Ctrl+C 退出。\n");
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        if (once) stop.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var device = Find().FirstOrDefault();
                    if (device == null) Log("未发现可访问的 PS4 手柄。请按 PS 键连接；若使用 HidHide，请允许此程序访问实体手柄。");
                    else if (await Monitor(device, once, stop.Token) && once) return 0;
                }
                catch (IOException e) { Log(e.Message); }
                await Task.Delay(3000, stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log("读取失败：" + e.Message); return 1; }
        if (once) { Log("本次未读取到电量。"); return 2; }
        return 0;
    }
}
