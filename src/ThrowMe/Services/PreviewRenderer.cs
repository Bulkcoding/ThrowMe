using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ThrowMe.Models;
using ThrowMe.Views;
using ThrowMe.Views.Skins;
using Color = System.Windows.Media.Color;
using Size = System.Windows.Size;
using Point = System.Windows.Point;
using Brush = System.Windows.Media.Brush;

namespace ThrowMe.Services;

/// <summary>
/// 개발용 오프라인 렌더러. 스포츠 스킨과 경기장을 창을 띄우지 않고 PNG로 저장한다.
/// 일반 실행 경로에는 영향 없음(커맨드라인 인자로만 진입).
/// </summary>
internal static class PreviewRenderer
{
    public static void Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var settings = new AppSettings { SlimeSizeBase = 96 };

        // 농구공 기본 + 여러 바운스 무늬
        Save(Wrap(new BasketballSkin(), 220), 220, 220, Path.Combine(outDir, "ball_default.png"));
        for (int i = 0; i < 4; i++)
        {
            var b = new BasketballSkin();
            b.OnBounce();
            Save(Wrap(b, 220), 220, 220, Path.Combine(outDir, $"ball_{i}.png"));
        }

        // 골대(좌/우)
        SaveHoop(HoopSide.Left, settings, Path.Combine(outDir, "hoop_left.png"));
        SaveHoop(HoopSide.Right, settings, Path.Combine(outDir, "hoop_right.png"));
        // 볼링핀 단독 + 실제 레인 배치
        Save(Wrap(new PinSkin(), 320), 320, 320, Path.Combine(outDir, "bowling_pin.png"));
        SaveBowlingLane(Path.Combine(outDir, "bowling_lane.png"));
        SaveBowlingScoreboard(Path.Combine(outDir, "bowling_scoreboard.png"));
        // 종이비행기 스킨: 오른쪽 비행 / 왼쪽 비행(뒤집힘) / 구겨짐
        Save(Wrap(new PaperPlaneSkin(), 320), 320, 320, Path.Combine(outDir, "paperplane.png"));
        var planeLeft = new PaperPlaneSkin();
        planeLeft.SetHeading(170);   // 왼쪽으로 살짝 내려가며 비행
        Save(Wrap(planeLeft, 320), 320, 320, Path.Combine(outDir, "paperplane_left.png"));
        var planeRight = new PaperPlaneSkin();
        planeRight.SetHeading(-10);  // 오른쪽으로 살짝 올라가며 비행
        Save(Wrap(planeRight, 320), 320, 320, Path.Combine(outDir, "paperplane_right.png"));
        var planeCrumpled = new PaperPlaneSkin();
        planeCrumpled.SetCrumpled(true);
        Save(Wrap(planeCrumpled, 320), 320, 320, Path.Combine(outDir, "paperplane_crumpled.png"));
    }

    private static void SaveBowlingScoreboard(string path)
    {
        var board = new BowlingScoreboardWindow(new Rect(0, 0, 1920, 1080));
        var frames = new List<BowlingFrameDisplay>
        {
            new("", "X", "", 30, true),
            new("", "X", "", 60, true),
            new("9", "/", "", 80, true),
            new("7", "–", "", 87, true),
            new("8", "", "", null, false),
            new("", "", "", null, false),
            new("", "", "", null, false),
            new("", "", "", null, false),
            new("", "", "", null, false),
            new("", "", "", null, false),
        };
        board.SetGame(5, 2, 87, frames, "TURN · 5 FRAME", Color.FromRgb(0xFF, 0xD1, 0x3A), false);
        FrameworkElement panel = board.DetachPreviewPanel();
        var host = new Border { Width = 760, Height = 330, Background = Gray, Child = panel };
        Save(host, 760, 330, path);
    }
    private static void SaveBowlingLane(string path)
    {
        const int w = 900, h = 700;
        var monitors = new MonitorLayoutService();
        var lane = new LaneOverlayWindow(monitors);
        lane.PreparePreview(new Rect(0, 0, w, h));

        const double cx = w / 2.0, topY = 68, botY = 685, foulY = 590;
        const double laneHalfTop = 142, laneHalfBot = 238;
        const double alleyHalfTop = 202, alleyHalfBot = 340;
        const double deckBotY = 298, arrowsY = 425;
        lane.Setup(cx, topY, botY, foulY, laneHalfTop, laneHalfBot,
                   alleyHalfTop, alleyHalfBot, deckBotY, arrowsY);

        Canvas root = lane.PreviewRoot;
        lane.Content = null;

        const double size = 96, box = 320 * PinWindow.VisualScale;
        const double backY = 160, hGap = size * 0.72, vGap = size * 0.62;
        int[] counts = { 4, 3, 2, 1 };
        for (int row = 0; row < counts.Length; row++)
        {
            int count = counts[row];
            double y = backY + row * vGap;
            for (int i = 0; i < count; i++)
            {
                double x = cx + (i - (count - 1) / 2.0) * hGap;
                var pin = new PinSkin { Width = box, Height = box };
                Canvas.SetLeft(pin, x - box / 2);
                Canvas.SetTop(pin, y - box / 2);
                root.Children.Add(pin);
            }
        }

        var host = new Border { Width = w, Height = h, Background = Gray, Child = root };
        Save(host, w, h, path);
        monitors.Dispose();
    }
    private static void SaveHoop(HoopSide side, AppSettings settings, string path)
    {
        var hoop = new HoopWindow(side, new Rect(0, 0, 1920, 1080), settings);
        Canvas root = hoop.PreviewRoot;
        int w = (int)root.Width, h = (int)root.Height;

        var host = new Border { Width = w, Height = h, Background = Gray };
        // Root 를 창에서 떼어내 host 에 넣는다(미리보기 전용).
        (root.Parent as System.Windows.Controls.Decorator)!.Child = null;
        host.Child = root;
        Save(host, w, h, path);
    }

    private static FrameworkElement Wrap(FrameworkElement child, int size)
    {
        return new Grid { Width = size, Height = size, Background = Gray, Children = { child } };
    }

    private static readonly Brush Gray = Freeze(Color.FromRgb(0x80, 0x80, 0x86));
    private static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private static void Save(FrameworkElement fe, int w, int h, string path)
    {
        fe.Measure(new Size(w, h));
        fe.Arrange(new Rect(0, 0, w, h));
        fe.UpdateLayout();

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(fe);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
