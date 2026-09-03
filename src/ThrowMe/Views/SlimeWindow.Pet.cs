using UserControl = System.Windows.Controls.UserControl;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ThrowMe.Models;
using ThrowMe.Services;
using ThrowMe.Views.Skins;

namespace ThrowMe.Views;

/// <summary>
/// 펫 테마(<see cref="SlimeSkinKind.Pet"/>)와 CLI 상태 연동.
/// Claude Code 훅 → <see cref="CliStateServer"/> → 합친 상태 → 펫 스킨 동작.
/// 우클릭 메뉴와 트레이 메뉴에 살아 있는 세션 목록도 여기서 채운다.
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

    // ── 세션 목록 ─────────────────────────────────────────
    /// <summary>상태 이름(설정 창·메뉴 공용).</summary>
    internal static string StateLabel(AgentState s) => s switch
    {
        AgentState.Done => "완료",
        AgentState.Thinking => "생각 중",
        AgentState.Working => "작업 중",
        AgentState.Juggling => "서브에이전트 실행 중",
        AgentState.Waiting => "승인 대기",
        AgentState.Error => "오류",
        _ => "대기",
    };

    /// <summary>메뉴 한 줄: "폴더명 · 상태 · n분 전". 폴더는 눌러서 열 수 있게 함께 돌려준다.</summary>
    public IReadOnlyList<(string Text, string? Folder)> SessionMenuEntries()
    {
        var list = new List<(string, string?)>();
        if (_cli == null || !_settings.CliLinkEnabled) return list;
        foreach (var s in _cli.Sessions)
        {
            string folder = string.IsNullOrWhiteSpace(s.Cwd) ? "" : s.Cwd;
            string name = folder.Length == 0
                ? $"세션 {(s.Id.Length > 8 ? s.Id[..8] : s.Id)}"
                : (Path.GetFileName(folder.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : folder);
            list.Add(($"{name}  ·  {StateLabel(s.State)}  ·  {Ago(s.LastSeen)}", folder.Length > 0 ? folder : null));
        }
        return list;
    }

    private static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d.TotalSeconds < 60) return "방금";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}분 전";
        return $"{(int)d.TotalHours}시간 전";
    }

    /// <summary>탐색기로 세션 작업 폴더를 연다.</summary>
    internal static void OpenFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        try
        {
            string p = folder.Replace('/', '\\');
            if (Directory.Exists(p)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{p}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Logger.Error("Open session folder failed.", ex); }
    }

    /// <summary>우클릭 메뉴가 열릴 때 세션 항목을 다시 채운다(헤더 아래에 평평하게 끼워 넣는다).</summary>
    private void FillSessionMenu()
    {
        var menu = ContextMenu;
        if (menu == null) return;

        // 이전에 넣은 동적 항목 제거
        for (int i = menu.Items.Count - 1; i >= 0; i--)
            if (menu.Items[i] is MenuItem mi && mi.Tag is string tag && tag == "session") menu.Items.RemoveAt(i);

        bool on = _settings.CliLinkEnabled;
        MenuSessionsSep.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        MenuSessionsHeader.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) return;

        var entries = SessionMenuEntries();
        MenuSessionsHeader.Header = entries.Count == 0 ? "Claude Code 세션 — 없음" : $"Claude Code 세션 ({entries.Count})";
        int at = menu.Items.IndexOf(MenuSessionsHeader) + 1;
        var style = (Style)FindResource("DarkMenuItem");
        foreach (var (text, folder) in entries)
        {
            var item = new MenuItem { Header = "   " + text, Style = style, Tag = "session", IsEnabled = folder != null, ToolTip = folder };
            string? f = folder;
            item.Click += (_, _) => OpenFolder(f);
            menu.Items.Insert(at++, item);
        }
    }
}
