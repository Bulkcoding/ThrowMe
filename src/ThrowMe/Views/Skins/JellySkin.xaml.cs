using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ThrowMe.Models;
using UserControl = System.Windows.Controls.UserControl;

namespace ThrowMe.Views.Skins;

public partial class JellySkin : UserControl, ISkinExpressions, ISkinCrawl
{
    /// <summary>
    /// 기어다니기 컷. 원본 그림에서 잘라낸 것으로, <b>늘어난 정도 순</b>으로 늘어놓았다.
    /// 걸음의 뻗기 세기(0~1)를 그대로 이 배열의 위치로 바꿔 쓴다 — 그림이 곧 자세다.
    /// (가로/세로 비: 1.33 → 1.66 → 1.81 → 1.84 → 2.15)
    /// </summary>
    private static readonly string[] CrawlFrames =
    {
        "slime0.png", "slime3.png", "slime2.png", "slime1.png", "slime4.png",
    };

    private static readonly BitmapImage?[] Cache = new BitmapImage?[CrawlFrames.Length];

    private bool _crawling;
    private int _shownFrame = -1;

    public JellySkin() => InitializeComponent();

    public void SetExpression(SlimeExpression expression)
    {
        ExprNormal.Visibility = expression == SlimeExpression.Normal ? Visibility.Visible : Visibility.Collapsed;
        ExprFlying.Visibility = expression == SlimeExpression.Flying ? Visibility.Visible : Visibility.Collapsed;
        ExprDizzy.Visibility = expression == SlimeExpression.Dizzy ? Visibility.Visible : Visibility.Collapsed;
    }

    private static BitmapImage Frame(int i)
    {
        if (Cache[i] is { } cached) return cached;
        var img = new BitmapImage();
        img.BeginInit();
        img.UriSource = new Uri($"pack://application:,,,/Resources/Slime/{CrawlFrames[i]}", UriKind.Absolute);
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        Cache[i] = img;
        return img;
    }

    /// <summary>
    /// 기어다니는 자세로 바꾼다. 뻗기 세기에 맞는 컷을 보여 주고, 왼쪽으로 갈 때는 좌우를 뒤집는다.
    /// 원본 그림은 오른쪽을 보고 있다.
    /// </summary>
    public void SetCrawlPose(double lunge, double dirX)
    {
        int i = (int)Math.Round(Math.Clamp(lunge, 0, 1) * (CrawlFrames.Length - 1));
        if (i != _shownFrame)
        {
            CrawlSprite.Source = Frame(i);
            _shownFrame = i;
        }
        CrawlFlip.ScaleX = dirX >= 0 ? 1 : -1;

        if (_crawling) return;
        _crawling = true;
        CrawlSprite.Visibility = Visibility.Visible;
        BodyLayer.Visibility = Visibility.Collapsed;   // 코드로 그리던 몸통은 감춘다
        Face.Visibility = Visibility.Collapsed;        // 눈·볼터치는 그림에 이미 들어 있다
    }

    public void ClearCrawlPose()
    {
        if (!_crawling) return;
        _crawling = false;
        CrawlSprite.Visibility = Visibility.Collapsed;
        BodyLayer.Visibility = Visibility.Visible;
        Face.Visibility = Visibility.Visible;
    }
}
