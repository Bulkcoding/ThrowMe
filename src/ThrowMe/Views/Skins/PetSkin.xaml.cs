using UserControl = System.Windows.Controls.UserControl;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ThrowMe.Models;
using ThrowMe.Services;

namespace ThrowMe.Views.Skins;

/// <summary>
/// Codex Pet 스프라이트시트를 재생하는 스킨.
///
/// 동작(행)은 두 층으로 고른다.
/// 1) 바탕 동작: CLI 상태(<see cref="AgentState"/>)가 정한다 — 대기·생각·작업·기다림·실패.
/// 2) 위에 얹는 동작: 공이 던져져 빠르게 움직이면 진행 방향의 달리기, 작업 완료(Stop)면 손 흔들기 한 번.
/// 프레임 타이머는 스킨이 스스로 돌린다. 창의 렌더 루프는 공이 멎으면 잠들기 때문이다.
/// </summary>
public partial class PetSkin : UserControl
{
    private readonly PetPack _pack;
    private readonly BitmapSource? _sheet;
    private readonly Dictionary<(int Row, int Index), CroppedBitmap> _frames = new();
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render);

    private PetRow _row;
    private int _index;
    private bool _loop = true;
    private bool _oneShot;                 // 지금 재생 중인 것이 한 번짜리(끝나면 바탕으로 돌아감)
    private AgentState _agent = AgentState.Idle;
    private int _motionDir;                // -1 왼쪽, 0 정지, +1 오른쪽

    /// <summary>이 속도(px/s)보다 빠르면 달리기 동작을 얹는다.</summary>
    private const double RunSpeed = 140.0;
    /// <summary>달리기에서 내려오는 속도(떨림 방지 히스테리시스).</summary>
    private const double StopSpeed = 60.0;

    public PetPack Pack => _pack;

    /// <param name="animate">거짓이면 첫 프레임만 그린다(설정 미리보기용).</param>
    public PetSkin(PetPack pack, bool animate = true)
    {
        InitializeComponent();
        _pack = pack;
        _sheet = PetPackStore.LoadSheet(pack);
        _row = RowFor("idle");
        ShowFrame(0);

        if (animate)
        {
            _timer.Tick += (_, _) => Step();
            Restart();
            Loaded += (_, _) => { if (!_timer.IsEnabled) Restart(); };
            Unloaded += (_, _) => _timer.Stop();
        }
    }

    // ── 외부 입력 ─────────────────────────────────────────
    /// <summary>CLI 상태가 바뀌었다. 한 번짜리 동작 중이면 끝난 뒤 반영된다.</summary>
    public void SetAgentState(AgentState s)
    {
        if (_agent == s) return;
        var prev = _agent;
        _agent = s;
        if (_motionDir != 0) return;   // 날아가는 중이면 달리기가 우선. 멎으면 바탕으로 돌아온다.
        // 완료(점프) → 대기로 내려갈 때는 clawd 처럼 한 번 둘러보고(review) 쉰다.
        if (prev == AgentState.Done && s == AgentState.Idle) { PlayOnce("review"); return; }
        if (!_oneShot) Play(BaseRowKey(), loop: true);
    }

    /// <summary>작업 완료 등 한 번 보여 주고 끝나는 동작. 없으면 무시.</summary>
    public void PlayOnce(string rowKey)
    {
        if (PetAtlas.Find(rowKey) is null || _pack.FramesIn(PetAtlas.Find(rowKey)!.Row) == 0) return;
        _oneShot = true;
        Play(rowKey, loop: false);
    }

    /// <summary>공의 속도에 따라 달리기 동작을 얹거나 내린다.</summary>
    public void SetMotion(double vx, double speed)
    {
        int dir = _motionDir;
        if (speed > RunSpeed) dir = vx < 0 ? -1 : 1;
        else if (speed < StopSpeed) dir = 0;
        if (dir == _motionDir) return;
        _motionDir = dir;
        _oneShot = false;
        Play(dir == 0 ? BaseRowKey() : (dir < 0 ? "running-left" : "running-right"), loop: true);
    }

    // ── 내부 ─────────────────────────────────────────────
    private string BaseRowKey() => _agent switch
    {
        AgentState.Done => "jumping",
        AgentState.Thinking => "review",
        AgentState.Working => "running",
        AgentState.Juggling => "jumping",
        AgentState.Waiting => "waiting",
        AgentState.Error => "failed",
        _ => "idle",
    };

    /// <summary>행을 찾되, 팩에 프레임이 없으면 대기 행으로 대신한다.</summary>
    private PetRow RowFor(string key)
    {
        var r = PetAtlas.Find(key);
        if (r != null && _pack.FramesIn(r.Row) > 0) return r;
        return PetAtlas.Rows[0];
    }

    private void Play(string key, bool loop)
    {
        var row = RowFor(key);
        if (row.Row == _row.Row && loop == _loop && !_oneShot) return;
        _row = row;
        _loop = loop;
        _index = 0;
        ShowFrame(0);
        Restart();
    }

    private void Restart()
    {
        _timer.Stop();
        _timer.Interval = TimeSpan.FromMilliseconds(DurationOf(_index));
        _timer.Start();
    }

    private int DurationOf(int index)
    {
        var d = _row.Durations;
        return d.Length == 0 ? 140 : d[index % d.Length];
    }

    private void Step()
    {
        int count = Math.Max(1, _pack.FramesIn(_row.Row));
        int next = _index + 1;
        if (next >= count)
        {
            if (!_loop)
            {
                // 한 번짜리가 끝났다 → 바탕(또는 달리기)으로 복귀.
                _oneShot = false;
                Play(_motionDir == 0 ? BaseRowKey() : (_motionDir < 0 ? "running-left" : "running-right"), loop: true);
                return;
            }
            next = 0;
        }
        _index = next;
        ShowFrame(next);
        _timer.Interval = TimeSpan.FromMilliseconds(DurationOf(next));
    }

    private void ShowFrame(int index)
    {
        if (_sheet == null) return;
        var key = (_row.Row, index);
        if (!_frames.TryGetValue(key, out var bmp))
        {
            int x = index * PetAtlas.FrameWidth, y = _row.Row * PetAtlas.FrameHeight;
            if (x + PetAtlas.FrameWidth > _sheet.PixelWidth || y + PetAtlas.FrameHeight > _sheet.PixelHeight) return;
            bmp = new CroppedBitmap(_sheet, new Int32Rect(x, y, PetAtlas.FrameWidth, PetAtlas.FrameHeight));
            bmp.Freeze();
            _frames[key] = bmp;
        }
        Frame.Source = bmp;
    }
}
