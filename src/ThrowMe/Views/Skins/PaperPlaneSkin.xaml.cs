using System.Windows;
using UserControl = System.Windows.Controls.UserControl;

namespace ThrowMe.Views.Skins;

/// <summary>
/// 종이비행기 스킨. 참고 이미지(image/종이비행기.jpg)의 라인아트 형태를
/// 네 개의 접힌 면 + 굵은 검정 외곽선으로 옮긴 모양. 단단한 스킨(Rigid).
///
/// 굴러가는 스핀은 쓰지 않는다. 방향은 <see cref="SetHeading"/> 로
/// 좌우 뒤집기(Mirror) + 기수 각도(Pitch)로 표현하고,
/// 벽에 부딪히면 <see cref="SetCrumpled"/> 로 구겨진 종이 뭉치로 바뀐다.
/// </summary>
public partial class PaperPlaneSkin : UserControl, ISkinHeading, ISkinCrumple
{
    /// <summary>기본 그림에서 기수가 향한 각도(deg). 꼬리 중앙 → 기수 방향.</summary>
    private const double NoseAxisDeg = -24.4;

    public PaperPlaneSkin()
    {
        InitializeComponent();
    }

    public bool IsCrumpled { get; private set; }

    /// <summary>지금 왼쪽을 향하고 있는가(Mirror 적용 상태).</summary>
    private bool _faceLeft;

    public void SetHeading(double travelAngleDeg)
    {
        // 진행 방향의 x 성분이 음수면 왼쪽 비행 → 그림을 좌우로 뒤집는다.
        // 거의 수직으로 오르내릴 때 좌우가 떨리지 않게 히스테리시스를 둔다.
        double cos = Math.Cos(travelAngleDeg * Math.PI / 180.0);
        bool left = _faceLeft ? cos < 0.10 : cos < -0.10;
        if (left != _faceLeft)
        {
            _faceLeft = left;
            Mirror.ScaleX = left ? -1 : 1;
        }

        // 뒤집으면 기수 축도 함께 뒤집힌다(-24.4 → 204.4).
        double axis = left ? 180.0 - NoseAxisDeg : NoseAxisDeg;
        Pitch.Angle = Normalize(travelAngleDeg - axis);
    }

    public void SetRestPose() => Pitch.Angle = 0;

    public void SetCrumpled(bool on)
    {
        if (IsCrumpled == on) return;
        IsCrumpled = on;
        PlaneLayer.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        CrumpledLayer.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) Pitch.Angle = 0; // 구겨지면 자세 개념이 없다
    }

    private static double Normalize(double deg)
    {
        while (deg > 180) deg -= 360;
        while (deg < -180) deg += 360;
        return deg;
    }
}
