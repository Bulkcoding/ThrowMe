using UserControl = System.Windows.Controls.UserControl;
using System.Windows.Controls;
using ThrowMe.Models;
using ThrowMe.Services;
using ThrowMe.Views.Skins;

namespace ThrowMe.Views;

/// <summary>
/// 펫 테마(<see cref="SlimeSkinKind.Pet"/>)와 CLI 상태 연동.
/// Claude Code 훅 → <see cref="CliStateServer"/> → 합친 상태 → 펫 스킨 동작.
/// </summary>
public partial class SlimeWindow
{
    private CliStateServer? _cli;
    private AgentState _agentState = AgentState.Idle;

    /// <summary>설정 창 표시용.</summary>
    public AgentState CurrentAgentState => _agentState;
    public bool CliServerRunning => _cli?.IsRunning == true;
    public string? CliLastError => _cli?.LastError;
    public int CliSessionCount => _cli?.SessionCount ?? 0;
    /// <summary>상태·세션 수·서버 상태가 바뀌었다(UI 스레드).</summary>
    public event EventHandler? CliStatusChanged;

    /// <summary>설정에 맞춰 수신 서버를 켜거나 끈다.</summary>
    private void UpdateCliLink()
    {
        if (_settings.CliLinkEnabled)
        {
            if (_cli == null)
            {
                _cli = new CliStateServer(AppSettings.CliLinkPort);
                _cli.StateChanged += (_, s) => Dispatcher.BeginInvoke(() => OnAgentState(s));
                _cli.TurnFinished += (_, _) => Dispatcher.BeginInvoke(OnAgentTurnFinished);
                _cli.SessionsChanged += (_, _) => Dispatcher.BeginInvoke(() => CliStatusChanged?.Invoke(this, EventArgs.Empty));
            }
            _cli.Start();
        }
        else
        {
            _cli?.Stop();
            OnAgentState(AgentState.Idle);
        }
        CliStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnAgentState(AgentState s)
    {
        if (_agentState == s) return;
        _agentState = s;
        (SkinHost.Content as PetSkin)?.SetAgentState(s);
        CliStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>한 턴이 끝났다 → 손 흔들기 한 번.</summary>
    private void OnAgentTurnFinished() => (SkinHost.Content as PetSkin)?.PlayOnce("waving");

    /// <summary>설정 창에서 팩을 가져오거나 지운 뒤, 같은 테마여도 스킨을 새로 그리게 한다.</summary>
    public void ReapplySkinPublic() => ApplySkinChange();

    /// <summary>설정의 팩(없으면 첫 팩)으로 펫 스킨을 만든다. 팩이 하나도 없으면 젤리로 대신한다.</summary>
    private UserControl MakePetSkin()
    {
        var pack = PetPackStore.Get(_settings.PetId) ?? PetPackStore.List().FirstOrDefault();
        if (pack == null) return new JellySkin();
        var skin = new PetSkin(pack);
        skin.SetAgentState(_agentState);
        return skin;
    }
}
