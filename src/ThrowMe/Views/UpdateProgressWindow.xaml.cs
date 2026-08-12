using System.Windows;

namespace ThrowMe.Views;

/// <summary>
/// 앱을 켤 때 새 버전이 있으면 잠깐 뜨는 진행 창.
///
/// 묻지 않는다 — 받아서 적용하고 자동으로 다시 시작한다. 무엇이 달라졌는지는
/// 설정 → 일반 → "최근 변경 내용 보기" 에서 언제든 확인할 수 있다.
/// </summary>
public partial class UpdateProgressWindow : Window
{
    public UpdateProgressWindow(System.Version version)
    {
        InitializeComponent();
        SubText.Text = $"v{Trim(version)} 로 업데이트하고 있습니다.";
        // 두 번째 줄("끝나면 자동으로 다시 시작합니다.")은 XAML 기본값 그대로 둔다.
    }

    /// <summary>다운로드 진행률(0~1) 반영.</summary>
    public void SetProgress(double value)
    {
        value = System.Math.Clamp(value, 0, 1);
        Bar.IsIndeterminate = false;
        Bar.Value = value;
        PercentText.Text = $"{value * 100:0}%";
    }

    /// <summary>길이를 모르는 다운로드나 교체 단계처럼 진행률을 알 수 없을 때.</summary>
    public void SetIndeterminate(string caption)
    {
        Bar.IsIndeterminate = true;
        PercentText.Text = "";
        TitleText.Text = caption;
    }

    /// <summary>교체 직전 단계 표시.</summary>
    public void SetApplying()
    {
        Bar.IsIndeterminate = true;
        PercentText.Text = "";
        TitleText.Text = "적용하는 중…";
        SubText.Text = "새 버전으로 바꾸고 있습니다.";
        SubText2.Text = "잠시 후 자동으로 다시 시작합니다.";
    }

    private static string Trim(System.Version v) =>
        v.Revision <= 0 && v.Build >= 0 ? v.ToString(3) : v.ToString();
}
