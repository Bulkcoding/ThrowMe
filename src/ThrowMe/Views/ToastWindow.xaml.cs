using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ThrowMe.Services;

namespace ThrowMe.Views;

/// <summary>
/// 우측 하단에 잠깐 떠오르는 작은 알림(토스트).
///
/// 공이 다른 PC로 넘어갔을 때처럼 "슬라임이 사라진 이유"를 알려주는 데 쓴다.
/// 그동안은 공이 상대 PC로 가면 화면에서 그냥 사라져서, 앱이 죽은 것처럼 보였다.
///
/// - 트레이 풍선(NotifyIcon.ShowBalloonTip)은 Windows 알림 설정·집중 지원에 막히면
///   아예 안 뜨므로, 직접 그린 창을 쓴다.
/// - 포커스를 훔치지 않는다(ShowActivated=False) → 작업 중 방해되지 않는다.
/// - 같은 시점에 여러 개가 뜨면 위로 쌓인다.
/// </summary>
public partial class ToastWindow : Window
{
    /// <summary>화면 가장자리에서 띄울 여백(px).</summary>
    private const double MarginPx = 16;

    /// <summary>토스트 사이 간격(px).</summary>
    private const double GapPx = 8;

    /// <summary>현재 떠 있는 토스트(위로 쌓기 위해 추적).</summary>
    private static readonly List<ToastWindow> Live = new();

    /// <summary>스스로 사라지지 않는 안내(키 → 창). 조건이 풀릴 때 내리려고 붙잡아 둔다.</summary>
    private static readonly Dictionary<string, ToastWindow> Sticky = new(StringComparer.Ordinal);

    /// <summary>이 토스트가 <see cref="Sticky"/> 에 등록된 키. 아니면 null.</summary>
    private string? _stickyKey;

    private readonly DispatcherTimer? _timer;
    private bool _closing;

    private ToastWindow(string title, string body, TimeSpan duration)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = body;
        BodyText.Visibility = string.IsNullOrWhiteSpace(body) ? Visibility.Collapsed : Visibility.Visible;

        // duration 이 0 이하면 시간으로 닫지 않는다(조건이 풀릴 때까지 떠 있는 안내).
        if (duration > TimeSpan.Zero)
        {
            _timer = new DispatcherTimer { Interval = duration };
            _timer.Tick += (_, _) => FadeOutAndClose();
        }

        // 클릭하면 바로 닫힌다.
        MouseLeftButtonDown += (_, _) => FadeOutAndClose();

        Loaded += OnLoadedInternal;
        Closed += (_, _) =>
        {
            _timer?.Stop();
            Live.Remove(this);
            if (_stickyKey != null) Sticky.Remove(_stickyKey);
            Reflow();
        };
    }

    /// <summary>
    /// 토스트를 띄운다. UI 스레드에서 호출해야 한다.
    /// 실패해도 앱 동작에 영향이 없도록 예외를 삼킨다.
    /// </summary>
    public static void Show(string title, string body, double seconds = 4.5)
    {
        try
        {
            var t = new ToastWindow(title, body, TimeSpan.FromSeconds(seconds));
            Live.Add(t);
            t.Show();
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to show toast.", ex);
        }
    }

    /// <summary>
    /// 시간이 지나도 사라지지 않는 안내를 띄운다. 같은 <paramref name="key"/> 로 이미 떠 있으면 그대로 둔다.
    /// 조건이 풀리면 <see cref="CloseSticky"/> 로 내린다. 사용자가 클릭해 닫을 수는 있다.
    /// </summary>
    public static void ShowSticky(string key, string title, string body)
    {
        if (Sticky.TryGetValue(key, out var exist) && !exist._closing) return;
        try
        {
            var t = new ToastWindow(title, body, TimeSpan.Zero) { _stickyKey = key };
            Sticky[key] = t;
            Live.Add(t);
            t.Show();
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to show sticky toast.", ex);
        }
    }

    /// <summary>고정 안내를 내린다. 떠 있지 않으면 아무 일도 하지 않는다.</summary>
    public static void CloseSticky(string key)
    {
        if (!Sticky.TryGetValue(key, out var t)) return;
        Sticky.Remove(key);
        t._stickyKey = null;
        try { t.FadeOutAndClose(); } catch { }
    }

    /// <summary>떠 있는 모든 토스트를 닫는다(종료 시 정리).</summary>
    public static void CloseAll()
    {
        foreach (var t in Live.ToArray())
        {
            try { t.Close(); } catch { }
        }
    }

    private void OnLoadedInternal(object sender, RoutedEventArgs e)
    {
        Reflow();
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Card.BeginAnimation(OpacityProperty, fade);
        _timer?.Start();
    }

    /// <summary>모든 토스트를 우측 하단부터 위로 다시 배치한다.</summary>
    private static void Reflow()
    {
        Rect area;
        try
        {
            // 작업표시줄을 가리지 않도록 주 모니터의 작업 영역을 쓴다.
            var wa = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea;
            area = wa.HasValue
                ? new Rect(wa.Value.X, wa.Value.Y, wa.Value.Width, wa.Value.Height)
                : new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        }
        catch
        {
            area = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        }

        double bottom = area.Bottom - MarginPx;
        // 최근 것이 아래에 오도록 역순으로 쌓는다.
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            var t = Live[i];
            if (!t.IsLoaded) continue;

            double scaleX = 1.0, scaleY = 1.0;
            var src = PresentationSource.FromVisual(t);
            if (src?.CompositionTarget != null)
            {
                scaleX = src.CompositionTarget.TransformToDevice.M11;
                scaleY = src.CompositionTarget.TransformToDevice.M22;
                if (scaleX <= 0) scaleX = 1.0;
                if (scaleY <= 0) scaleY = 1.0;
            }

            double wPx = t.ActualWidth * scaleX;
            double hPx = t.ActualHeight * scaleY;

            t.Left = (area.Right - MarginPx - wPx) / scaleX;
            t.Top = (bottom - hPx) / scaleY;
            bottom -= hPx + GapPx;
        }
    }

    private void FadeOutAndClose()
    {
        if (_closing) return;
        _closing = true;
        _timer?.Stop();

        var fade = new DoubleAnimation(Card.Opacity, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) => { try { Close(); } catch { } };
        Card.BeginAnimation(OpacityProperty, fade);
    }
}
