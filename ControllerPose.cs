using System.Numerics;
using Microsoft.Win32.SafeHandles;

public partial class Program
{
    record PoseReading(Quaternion Rotation, DateTime Received, bool Calibrated, int Session);
    static PoseReading poseReading;
    static int poseSession;

    // DS4 feature layout and timestamp units: SDL HIDAPI PS4 / Linux hid-playstation.
    sealed class MotionCalibration
    {
        public readonly float[] Bias = new float[6];
        public readonly float[] Scale = { MathF.PI / 2880, MathF.PI / 2880, MathF.PI / 2880, 1f / 8192, 1f / 8192, 1f / 8192 };
        public bool Valid;
        public Vector3 Gyro(InputState s) => new((s.GX - Bias[0]) * Scale[0], (s.GY - Bias[1]) * Scale[1], (s.GZ - Bias[2]) * Scale[2]);
        public Vector3 Accel(InputState s) => new((s.AX - Bias[3]) * Scale[3], (s.AY - Bias[4]) * Scale[4], (s.AZ - Bias[5]) * Scale[5]);
        public static MotionCalibration Parse(byte[] b, bool bt)
        {
            var c = new MotionCalibration();
            if (b.Length < (bt ? 41 : 37) || b[0] != (bt ? 5 : 2)) return c;
            if (bt && Crc(b, 37, 0xa3) != BitConverter.ToUInt32(b, 37)) return c;
            short S(int p) => System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(p, 2));
            float speed = S(19) + S(21);
            for (int i = 0; i < 3; i++)
            {
                float bias = S(1 + i * 2);
                float plus = S(bt ? 7 + i * 2 : 7 + i * 4), minus = S(bt ? 13 + i * 2 : 9 + i * 4);
                float range = Math.Abs(plus - bias) + Math.Abs(minus - bias);
                float factor = speed * 16 / range;
                float ap = S(23 + i * 4), am = S(25 + i * 4), ar = ap - am;
                float ab = (ap + am) / 2, af = 16384 / ar;
                if (!float.IsFinite(factor) || !float.IsFinite(af) || factor < .5f || factor > 1.5f || af < .5f || af > 1.5f || Math.Abs(bias) > 1024 || Math.Abs(ab) > 1024)
                    return new MotionCalibration();
                c.Bias[i] = bias; c.Scale[i] *= factor;
                c.Bias[i + 3] = ab; c.Scale[i + 3] *= af;
            }
            c.Valid = true; return c;
        }
        public static MotionCalibration Read(SafeFileHandle h, bool bt)
        {
            byte[] b = new byte[37]; b[0] = 2;
            if (!HidD_GetFeature(h, b, b.Length)) return new();
            if (bt)
            {
                b = new byte[41]; b[0] = 5;
                if (!HidD_GetFeature(h, b, b.Length)) return new();
            }
            return Parse(b, bt);
        }
    }

    // Body-to-world quaternion; DS4 X right, Y up, Z toward the player.
    // Integrate every valid report, not the UI's sampled/latest report.
    sealed class PoseFilter
    {
        public Quaternion Rotation { get; private set; } = Quaternion.Identity;
        bool initialized;
        ushort lastTick;
        DateTime lastReceived;
        static Quaternion AlignUp(Vector3 a)
        {
            if (a.LengthSquared() < .01f) return Quaternion.Identity;
            a = Vector3.Normalize(a);
            float dot = Vector3.Dot(a, Vector3.UnitY);
            if (dot < -.9999f) return Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
            return Quaternion.Normalize(new Quaternion(Vector3.Cross(a, Vector3.UnitY), 1 + dot));
        }
        public void Update(Vector3 gyro, Vector3 accel, ushort tick, DateTime received)
        {
            double gap = (received - lastReceived).TotalSeconds;
            float dt = ((tick - lastTick) & 65535) * (16f / 3_000_000);
            lastTick = tick; lastReceived = received;
            if (!initialized || gap > .25 || gap < 0 || dt > .1f)
            { Rotation = AlignUp(accel); initialized = true; return; }
            if (dt <= 0) return; // Duplicate sensor timestamp.
            float magnitude = accel.Length();
            if (magnitude > .85f && magnitude < 1.15f)
            {
                var predicted = Vector3.Transform(Vector3.UnitY, Quaternion.Conjugate(Rotation));
                // Gravity corrects tilt only; no magnetometer means yaw is relative.
                gyro += Vector3.Cross(accel / magnitude, predicted) * 1.4f;
            }
            float speed = gyro.Length();
            if (speed > 1e-7f)
                Rotation = Quaternion.Normalize(Rotation * Quaternion.CreateFromAxisAngle(gyro / speed, speed * dt));
        }
    }

    static void TestPose()
    {
        foreach (bool bt in new[] { false, true })
        {
            var b = new byte[bt ? 41 : 37]; b[0] = bt ? (byte)5 : (byte)2;
            void S(int p, short value) => System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(p, 2), value);
            S(19, 500); S(21, 500);
            for (int i = 0; i < 3; i++)
            { S(1 + 2 * i, (short)(10 + i)); S(bt ? 7 + 2 * i : 7 + 4 * i, (short)(8010 + i)); S(bt ? 13 + 2 * i : 9 + 4 * i, (short)(-7990 + i)); S(23 + 4 * i, 8192); S(25 + 4 * i, -8192); }
            if (bt) BitConverter.GetBytes(Crc(b, 37, 0xa3)).CopyTo(b, 37);
            var c = MotionCalibration.Parse(b, bt);
            if (!c.Valid || Math.Abs(c.Scale[0] - MathF.PI / 2880) > 1e-7 || c.Bias[2] != 12) throw new Exception("Motion calibration");
            b[7] ^= 127;
            if (bt && MotionCalibration.Parse(b, bt).Valid) throw new Exception("Calibration CRC");
            if (MotionCalibration.Parse(new byte[41], bt).Valid) throw new Exception("Invalid calibration fallback");
        }
        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
        {
            var filter = new PoseFilter(); var now = DateTime.UtcNow;
            filter.Update(Vector3.Zero, Vector3.UnitY, 65000, now);
            // 250 * 4 ms, including timestamp wrap, must rotate 90 degrees.
            for (int i = 1; i <= 250; i++) filter.Update(axis * (MathF.PI / 2), Vector3.Zero, unchecked((ushort)(65000 + 750 * i)), now.AddMilliseconds(i * 4));
            var expected = Quaternion.CreateFromAxisAngle(axis, MathF.PI / 2);
            if (Math.Abs(Quaternion.Dot(filter.Rotation, expected)) < .9999f) throw new Exception("Pose rotation / timestamp wrap");
            var before = filter.Rotation;
            filter.Update(axis * 100, Vector3.Zero, unchecked((ushort)(65000 + 750 * 250)), now.AddSeconds(1));
            if (Math.Abs(Quaternion.Dot(before, filter.Rotation)) < .99999f) throw new Exception("Duplicate pose sample");
            filter.Update(Vector3.Zero, Vector3.UnitY, 100, now.AddSeconds(4));
            if (Math.Abs(filter.Rotation.W) < .99999f) throw new Exception("Reconnect pose reset");
        }
        var tilt = new PoseFilter(); var time = DateTime.UtcNow;
        tilt.Update(Vector3.Zero, Vector3.UnitY, 0, time);
        var gravity = Vector3.Normalize(new Vector3(.5f, .866f, 0));
        for (int i = 1; i <= 2000; i++) tilt.Update(Vector3.Zero, gravity, unchecked((ushort)(750 * i)), time.AddMilliseconds(i * 4));
        if (Vector3.Distance(Vector3.Transform(gravity, tilt.Rotation), Vector3.UnitY) > .01f) throw new Exception("Gravity correction direction");
    }
}
