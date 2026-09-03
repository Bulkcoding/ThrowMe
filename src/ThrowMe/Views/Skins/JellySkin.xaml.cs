using System.Windows;
using System.Windows.Media.Imaging;
using ThrowMe.Models;
using UserControl = System.Windows.Controls.UserControl;

namespace ThrowMe.Views.Skins;

public partial class JellySkin : UserControl, ISkinExpressions, ISkinCrawl
{
    /// <summary>
    /// 오른쪽으로 기어갈 때의 컷 순서. 원본 시트 Slime_2 의 번호 그대로다.
    /// 쉬는 자세 → 조금 뻗음 → 더 뻗음 → 최대 → 다시 접힘 → 쉬는 자세.
    /// </summary>
    private static readonly string[] Right = { "f1", "f7", "f11", "f12", "f11", "f7", "f1" };

    /// <summary>
    /// 왼쪽으로 기어갈 때의 컷 순서. 쉬는 자세만 Slime_2, 나머지는 왼쪽 전용 시트 Slime_3 이다.
    /// <b>그림을 좌우로 뒤집지 않는다</b> — 뒤집어 쓰면 방향이 흔들릴 때마다 좌우가 번갈아
    /// 나타나 제자리에서 떠는 것처럼 보였다.
    /// </summary>
    private static readonly string[] Left = { "f1", "f18", "f22", "f19", "f22", "f18", "f1" };

    private static readonly Dictionary<string, BitmapImage> Cache = new(StringComparer.Ordinal);

    private bool _crawling;
    private string? _shownA;
    private string? _shownB;

    public JellySkin() => InitializeComponent();

    public void SetExpression(SlimeExpression expression)
    {
        ExprNormal.Visibility = expression == SlimeExpression.Normal ? Visibility.Visible : Visibility.Collapsed;
        ExprFlying.Visibility = expression == SlimeExpression.Flying ? Visibility.Visible : Visibility.Collapsed;
        ExprDizzy.Visibility = expression == SlimeExpression.Dizzy ? Visibility.Visible : Visibility.Collapsed;
    }

    private static BitmapImage Frame(string name)
    {
        if (Cache.TryGetValue(name, out var cached)) return cached;
        var img = new BitmapImage();
        img.BeginInit();
        img.UriSource = new Uri($"pack://application:,,,/Resources/Slime/{name}.png", UriKind.Absolute);
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        Cache[name] = img;
        return img;
    }

    public void SetCrawlPose(double t, bool faceRight)
    {
        string[] seq = faceRight ? Right : Left;

        // 걸음 위상을 연속 인덱스로 본다. 정수부는 현재 컷, 소수부는 다음 컷으로 넘어간 정도.
        // 두 컷을 그 비율로 겹쳐(크로스페이드) 즉시 교체의 뚝뚝 끊김을 없앤다.
        double f = Math.Clamp(t, 0, 0.9999) * seq.Length;
        int i = (int)f;
        double frac = f - i;
        string name0 = seq[Math.Clamp(i, 0, seq.Length - 1)];
        string name1 = seq[(i + 1) % seq.Length];

        if (name0 != _shownA) { CrawlSpriteA.Source = Frame(name0); _shownA = name0; }
        if (name1 != _shownB) { CrawlSpriteB.Source = Frame(name1); _shownB = name1; }
        CrawlSpriteA.Opacity = 1.0 - frac;
        CrawlSpriteB.Opacity = frac;

        if (_crawling) return;
        _crawling = true;
        CrawlSpriteA.Visibility = Visibility.Visible;
        CrawlSpriteB.Visibility = Visibility.Visible;
        BodyLayer.Visibility = Visibility.Collapsed;   // 코드로 그리던 몸통은 감춘다
        Face.Visibility = Visibility.Collapsed;        // 눈·볼터치는 그림에 이미 들어 있다
    }

    public void ClearCrawlPose()
    {
        if (!_crawling) return;
        _crawling = false;
        _shownA = null;
        _shownB = null;
        CrawlSpriteA.Visibility = Visibility.Collapsed;
        CrawlSpriteB.Visibility = Visibility.Collapsed;
        BodyLayer.Visibility = Visibility.Visible;
        Face.Visibility = Visibility.Visible;
    }
}
