using System.Drawing;
using System.Drawing.Drawing2D;
using System.Numerics;

public partial class Program
{
    // Small software-rendered model: no OpenGL driver or additional runtime needed.
    static void DrawPoseModel(Graphics g, Quaternion rotation, RectangleF area, InputState input = null)
    {
        var camera = Quaternion.CreateFromAxisAngle(Vector3.UnitX, .92f);
        Vector3 View(Vector3 p) => Vector3.Transform(Vector3.Transform(p, rotation), camera);
        PointF Project(Vector3 p)
        {
            float scale = Math.Min(area.Width / 4.6f, (area.Height - 48) / 3.5f) * 7 / (7 - p.Z);
            return new(area.X + area.Width / 2 + p.X * scale, area.Y + (area.Height - 48) / 2 - p.Y * scale);
        }
        var faces = new List<(Vector3[] Points, Color Color)>();
        var details = new List<(Vector3[] Points, Color Color)>();
        bool drawingDetails = false;
        void Face(IEnumerable<Vector3> points, Color color) => (drawingDetails ? details : faces).Add((points.Select(View).ToArray(), color));
        var edge = new Vector2[] {
            new(-1.2f,-.85f),new(-1.55f,-.65f),new(-1.7f,-.25f),new(-1.85f,.8f),new(-1.77f,1.13f),new(-1.52f,1.22f),
            new(-1.28f,1.05f),new(-.82f,.42f),new(.82f,.42f),new(1.28f,1.05f),new(1.52f,1.22f),new(1.77f,1.13f),
            new(1.85f,.8f),new(1.7f,-.25f),new(1.55f,-.65f),new(1.2f,-.85f) };
        Face(edge.Select(p => new Vector3(p.X, .18f, p.Y)), Color.FromArgb(67, 77, 97));
        Face(edge.Select(p => new Vector3(p.X, -.18f, p.Y)), Color.FromArgb(32, 38, 51));
        for (int i = 0; i < edge.Length; i++)
        {
            var a = edge[i]; var b = edge[(i + 1) % edge.Length];
            Face(new[] {new Vector3(a.X,.18f,a.Y), new(b.X,.18f,b.Y), new(b.X,-.18f,b.Y), new(a.X,-.18f,a.Y)}, Color.FromArgb(44, 53, 70));
        }
        void Rect(float x, float z, float w, float h, Color color, float y = .195f) => Face(new[] { new Vector3(x,y,z), new(x+w,y,z), new(x+w,y,z+h), new(x,y,z+h) }, color);
        void Disc(float x, float z, float r, Color color, float y = .21f) => Face(Enumerable.Range(0, 24).Select(i => new Vector3(x + r * MathF.Cos(i * MathF.PI / 12), y, z + r * MathF.Sin(i * MathF.PI / 12))), color);
        var active = Color.FromArgb(255, 216, 93);
        bool Down(string name) => input?.Pressed(name) == true;
        Color KeyColor(string name, Color idle) => Down(name) ? active : idle;
        var labels = new List<(Vector3 Position, string Text)>();
        var strokes = new List<Vector3[]>();
        void Mark(float x, float z, params Vector2[] points) => strokes.Add(points.Select(p => View(new Vector3(x + p.X, .245f, z + p.Y))).ToArray());
        // Top face controls are hidden when the underside faces the viewer.
        drawingDetails = true;
        if (View(Vector3.UnitY).Z > 0)
        {
            Rect(-.51f,-.7f,1.02f,.53f,KeyColor("触摸板", Color.FromArgb(29,36,48)));
            Rect(-.48f,-.86f,.96f,.06f,Color.FromArgb(38,215,169));
            var dark = Color.FromArgb(20,25,34);
            Rect(-1.33f,-.44f,.16f,.18f,KeyColor("↑",dark));
            Rect(-1.33f,-.07f,.16f,.18f,KeyColor("↓",dark));
            Rect(-1.53f,-.24f,.18f,.16f,KeyColor("←",dark));
            Rect(-1.15f,-.24f,.18f,.16f,KeyColor("→",dark));
            Disc(-.62f,.27f,.28f,dark); Disc(.62f,.27f,.28f,dark);
            Disc(-.62f + (float)Axis(input?.LX ?? 128)*.12f,.27f + (float)Axis(input?.LY ?? 128)*.12f,.20f,KeyColor("L3",Color.FromArgb(92,106,125)),.23f);
            Disc(.62f + (float)Axis(input?.RX ?? 128)*.12f,.27f + (float)Axis(input?.RY ?? 128)*.12f,.20f,KeyColor("R3",Color.FromArgb(92,106,125)),.23f);
            Disc(1.26f,-.48f,.13f,KeyColor("△",Color.FromArgb(66,208,162))); Disc(1.52f,-.2f,.13f,KeyColor("○",Color.FromArgb(242,115,127)));
            Disc(1f,-.2f,.13f,KeyColor("□",Color.FromArgb(206,144,230))); Disc(1.26f,.08f,.13f,KeyColor("×",Color.FromArgb(116,174,247)));
            Mark(1.26f,-.48f,new(0,-.07f),new(.07f,.06f),new(-.07f,.06f),new(0,-.07f));
            Mark(1f,-.2f,new(-.06f,-.06f),new(.06f,-.06f),new(.06f,.06f),new(-.06f,.06f),new(-.06f,-.06f));
            Mark(1.26f,.08f,new(-.06f,-.06f),new(.06f,.06f)); Mark(1.26f,.08f,new(-.06f,.06f),new(.06f,-.06f));
            Mark(1.52f,-.2f,Enumerable.Range(0,17).Select(i => new Vector2(.07f*MathF.Cos(i*MathF.PI/8),.07f*MathF.Sin(i*MathF.PI/8))).ToArray());
            Disc(0,.21f,.11f,KeyColor("PS",Color.FromArgb(190,200,219)));
            Rect(-.77f,-.57f,.13f,.26f,KeyColor("SHARE",Color.FromArgb(130,144,163))); Rect(.64f,-.57f,.13f,.26f,KeyColor("OPTIONS",Color.FromArgb(130,144,163)));
            foreach (var touch in new[] { input?.Touch1, input?.Touch2 })
                if (touch?.Active == true) Disc(-.51f+Math.Clamp(touch.X/1920f,0,1)*1.02f,-.7f+Math.Clamp(touch.Y/942f,0,1)*.53f,.035f,Color.FromArgb(38,215,169),.23f);
        }
        // Shoulder controls are solid geometry on the front edge, not decals on top.
        drawingDetails = false;
        void Solid(Vector3[] v, Color color)
        {
            int[][] sides = { new[]{0,1,2,3}, new[]{4,7,6,5}, new[]{0,4,5,1}, new[]{3,2,6,7}, new[]{0,3,7,4}, new[]{1,5,6,2} };
            for (int i=0;i<sides.Length;i++)
            {
                float shade = .65f + i*.06f;
                Face(sides[i].Select(j=>v[j]), Color.FromArgb((int)(color.R*shade),(int)(color.G*shade),(int)(color.B*shade)));
            }
        }
        for (int side = 0; side < 2; side++)
        {
            float x = side == 0 ? -1.42f : .88f;
            string shoulder = side == 0 ? "L1" : "R1";
            float amount = (side == 0 ? input?.L2 : input?.R2).GetValueOrDefault()/255f;
            float press = Down(shoulder) ? .035f : 0;
            Solid(new[] {
                new Vector3(x,.10f,-1.00f+press),new(x+.54f,.10f,-1.00f+press),new(x+.54f,.27f,-1.00f+press),new(x,.27f,-1.00f+press),
                new(x,.10f,-.68f+press),new(x+.54f,.10f,-.68f+press),new(x+.54f,.27f,-.68f+press),new(x,.27f,-.68f+press)
            },KeyColor(shoulder,Color.FromArgb(100,112,131)));
            // Pivot below L1/R1. Pulling the curved paddle moves its tip toward the grip.
            var pivot=new Vector3(x+.27f,.02f,-.94f);
            var hinge=Quaternion.CreateFromAxisAngle(Vector3.UnitX,-amount*.55f);
            Vector3 Paddle(float px,float py,float pz)=>pivot+Vector3.Transform(new Vector3(px,py,pz),hinge);
            Solid(new[] { Paddle(-.27f,0,-.08f),Paddle(.27f,0,-.08f),Paddle(.27f,-.58f,-.19f),Paddle(-.27f,-.58f,-.19f),
                Paddle(-.27f,0,.08f),Paddle(.27f,0,.08f),Paddle(.27f,-.58f,-.03f),Paddle(-.27f,-.58f,-.03f) },
                Color.FromArgb((int)(75+180*amount),(int)(87+129*amount),(int)(106-13*amount)));
            // Only label the shoulder when its top surface faces the camera.
            if(View(Vector3.UnitY).Z>0) labels.Add((View(new Vector3(x+.27f,.28f,-.84f)),shoulder));
        }        var saved = g.Save(); g.SetClip(new RectangleF(area.X, area.Y, area.Width, area.Height - 44), CombineMode.Intersect);
        using var outline = new Pen(Color.FromArgb(105,125,150), 1);
        // All controls sit on the same visible top plane. Draw them after the shell
        // so the top polygon's average depth cannot incorrectly cover distant keys.
        foreach (var face in faces.OrderBy(f => f.Points.Average(p => p.Z)).Concat(details))
        {
            using var brush = new SolidBrush(face.Color); var points = face.Points.Select(Project).ToArray();
            g.FillPolygon(brush, points); g.DrawPolygon(outline, points);
        }
        using var symbol = new Pen(Color.FromArgb(20,25,34), 1.5f);
        foreach (var line in strokes) g.DrawLines(symbol, line.Select(Project).ToArray());
        using var font = new Font("Segoe UI", 9, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        foreach (var label in labels) g.DrawString(label.Text, font, Brushes.White, Project(label.Position), format);
        // Keep exact trigger travel readable even when physical paddles are occluded.
        g.Restore(saved); saved = g.Save(); g.SetClip(area, CombineMode.Intersect);
        using var meterFont = new Font("Segoe UI", 11, FontStyle.Bold, GraphicsUnit.Pixel);
        for (int side=0;side<2;side++)
        {
            float value=(side==0?input?.L2:input?.R2).GetValueOrDefault()/255f;
            float x=area.X+12+side*(area.Width-176), y=area.Bottom-24;
            string name=side==0?"L2":"R2";
            g.DrawString(input==null ? name+"  —" : $"{name}  {value*100:F0}%",meterFont,Brushes.White,x,y-16);
            using var track=new SolidBrush(Color.FromArgb(44,49,61));
            using var fill=new SolidBrush(active);
            g.FillRectangle(track,x,y,152,7); g.FillRectangle(fill,x,y,152*value,7);
        }
        g.Restore(saved);
    }
}
