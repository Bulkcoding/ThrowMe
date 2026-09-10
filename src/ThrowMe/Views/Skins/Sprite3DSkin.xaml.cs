using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using UserControl = System.Windows.Controls.UserControl;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;

namespace ThrowMe.Views.Skins;

/// <summary>고정 메시와 재질을 공유하고 자세만 갱신하는 내장 3D 슬라임.</summary>
public partial class Sprite3DSkin : UserControl
{
    private static readonly MeshGeometry3D Sphere = MakeSphere();
    private static readonly MeshGeometry3D Face = MakeFace();
    private static readonly Material BodyMaterial = MakeBodyMaterial();
    private static readonly BitmapSource DefaultFace = MakeDefaultFace();
    private readonly QuaternionRotation3D _rotation = new(Quaternion.Identity);
    private readonly MatrixTransform3D _deform = new();
    private readonly TranslateTransform3D _hop = new();
    private readonly GeometryModel3D _face;

    public Sprite3DSkin()
    {
        InitializeComponent();
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(100, 116, 111)));
        lights.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-2, -3, -4)));
        lights.Freeze();
        Viewport.Children.Add(new ModelVisual3D { Content = lights });
        var model = new Model3DGroup();
        model.Children.Add(new GeometryModel3D(Sphere, BodyMaterial));
        _face = new GeometryModel3D(Face, null);
        model.Children.Add(_face);
        var transform = new Transform3DGroup();
        transform.Children.Add(new RotateTransform3D(_rotation));
        transform.Children.Add(_deform);
        transform.Children.Add(_hop);
        model.Transform = transform;
        Viewport.Children.Add(new ModelVisual3D { Content = model });
        SetImage(null, 1);
    }

    public void SetPose(Quaternion orientation, Matrix3D deformation, double hop)
    {
        if (!Finite(orientation.X) || !Finite(orientation.Y) || !Finite(orientation.Z)
            || !Finite(orientation.W) || !Finite(hop) || !Finite(deformation.M11) || !Finite(deformation.M12)
            || !Finite(deformation.M13) || !Finite(deformation.M14) || !Finite(deformation.M21)
            || !Finite(deformation.M22) || !Finite(deformation.M23) || !Finite(deformation.M24)
            || !Finite(deformation.M31) || !Finite(deformation.M32) || !Finite(deformation.M33)
            || !Finite(deformation.M34) || !Finite(deformation.M44) || !Finite(deformation.OffsetX)
            || !Finite(deformation.OffsetY) || !Finite(deformation.OffsetZ)) return;
        if (orientation.X * orientation.X + orientation.Y * orientation.Y + orientation.Z * orientation.Z + orientation.W * orientation.W < 1e-12) orientation = Quaternion.Identity;
        else orientation.Normalize();
        _rotation.Quaternion = orientation;
        _deform.Matrix = deformation;
        _hop.OffsetY = Math.Clamp(hop, 0, 0.2);
        Shadow.Opacity = 1 - Math.Clamp(hop * 2, 0, 0.4);
    }

    public void SetImage(BitmapSource? image, double scale)
    {
        if (!Finite(scale)) scale = 1;
        scale = Math.Clamp(scale, 0.2, 2);
        var source = image ?? DefaultFace;
        double aspect = source.PixelWidth / (double)Math.Max(1, source.PixelHeight);
        double width = scale * Math.Min(1, aspect), height = scale * Math.Min(1, 1 / aspect);
        // 범위를 벗어난 UV는 투명: 이미지를 구 앞면에서 반복하거나 뒷면에 복제하지 않는다.
        var brush = new ImageBrush(source)
        {
            ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewport = new Rect((1 - width) / 2, (1 - height) / 2, width, height),
            TileMode = TileMode.None, Stretch = Stretch.Fill,
        };
        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();
        _face.Material = material;
    }

    private static bool Finite(double value) => double.IsFinite(value);

    private static Material MakeBodyMaterial()
    {
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(235, 82, 224, 176))));
        material.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(130, 230, 255, 250)), 55));
        material.Freeze();
        return material;
    }

    private static MeshGeometry3D MakeSphere()
    {
        const int columns = 48, rows = 24;
        var mesh = new MeshGeometry3D();
        for (int y = 0; y <= rows; y++)
        {
            double latitude = Math.PI * y / rows;
            for (int x = 0; x <= columns; x++)
            {
                double longitude = 2 * Math.PI * x / columns;
                var n = new Vector3D(Math.Sin(latitude) * Math.Cos(longitude), Math.Cos(latitude), Math.Sin(latitude) * Math.Sin(longitude));
                mesh.Positions.Add(new Point3D(n.X, n.Y, n.Z));
                mesh.Normals.Add(n);
                mesh.TextureCoordinates.Add(new Point(x / (double)columns, y / (double)rows));
            }
        }
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < columns; x++)
            {
                int a = y * (columns + 1) + x, b = a + columns + 1;
                AddTriangle(mesh, a, a + 1, b);
                AddTriangle(mesh, a + 1, b + 1, b);
            }
        mesh.Freeze();
        return mesh;
    }

    private static MeshGeometry3D MakeFace()
    {
        // 정면 반구의 중앙 곡면. 각 꼭짓점은 본체에서 0.2%만 띄운다.
        const int steps = 24;
        var mesh = new MeshGeometry3D();
        for (int y = 0; y <= steps; y++)
            for (int x = 0; x <= steps; x++)
            {
                double px = (x / (double)steps - 0.5) * 1.35;
                double py = (0.5 - y / (double)steps) * 1.35;
                double pz = Math.Sqrt(Math.Max(0, 1 - px * px - py * py));
                mesh.Positions.Add(new Point3D(px * 1.002, py * 1.002, pz * 1.002));
                mesh.Normals.Add(new Vector3D(px, py, pz));
                mesh.TextureCoordinates.Add(new Point(x / (double)steps, y / (double)steps));
            }
        for (int y = 0; y < steps; y++)
            for (int x = 0; x < steps; x++)
            {
                int a = y * (steps + 1) + x, b = a + steps + 1;
                AddTriangle(mesh, a, b, a + 1);
                AddTriangle(mesh, a + 1, b, b + 1);
            }
        mesh.Freeze();
        return mesh;
    }

    private static void AddTriangle(MeshGeometry3D mesh, int a, int b, int c)
    {
        mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(c);
    }

    private static BitmapSource MakeDefaultFace()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var ink = new SolidColorBrush(Color.FromRgb(20, 68, 59));
            dc.DrawEllipse(ink, null, new Point(91, 114), 10, 15);
            dc.DrawEllipse(ink, null, new Point(165, 114), 10, 15);
            dc.DrawEllipse(Brushes.White, null, new Point(89, 109), 3, 4);
            dc.DrawEllipse(Brushes.White, null, new Point(163, 109), 3, 4);
            var smile = new StreamGeometry();
            using (var p = smile.Open())
            {
                p.BeginFigure(new Point(113, 143), false, false);
                p.QuadraticBezierTo(new Point(128, 160), new Point(143, 143), true, false);
            }
            dc.DrawGeometry(null, new Pen(ink, 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, smile);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(135, 246, 136, 165)), null, new Point(69, 141), 14, 7);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(135, 246, 136, 165)), null, new Point(187, 141), 14, 7);
        }
        var bitmap = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
