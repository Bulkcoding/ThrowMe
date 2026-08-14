using System.Runtime.InteropServices;

namespace ThrowMe.Services;

/// <summary>
/// 탐색기의 아이콘 캐시를 갱신하도록 셸에 알린다.
///
/// 자동 업데이트는 exe 를 <b>같은 경로에 덮어쓰기</b> 때문에, 바탕화면 바로가기(.lnk)의
/// 경로와 아이콘 인덱스가 그대로다. 셸은 "바뀔 이유가 없다"고 보고 캐시에 저장해 둔
/// 옛 아이콘을 계속 쓴다. 그래서 아이콘을 바꿔 배포해도 바로가기만 예전 그림으로 남았다.
///
/// 설치 프로그램들이 쓰는 방법 그대로, 셸에 "연결 정보가 바뀌었다"고 알려 다시 읽게 한다.
/// 탐색기 재시작·로그아웃·관리자 권한 모두 필요 없다.
/// </summary>
public static class ShellIconRefresh
{
    /// <summary>파일 연결(아이콘 포함)이 바뀜.</summary>
    private const uint SHCNE_ASSOCCHANGED = 0x08000000;

    /// <summary>항목 하나가 바뀜(경로로 지정).</summary>
    private const uint SHCNE_UPDATEITEM = 0x00002000;

    private const uint SHCNF_IDLIST = 0x0000;
    private const uint SHCNF_PATHW = 0x0005;
    private const uint SHCNF_FLUSH = 0x1000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags, string item1, IntPtr item2);

    /// <summary>
    /// 아이콘 캐시 갱신을 요청한다. 실패해도 앱 동작에는 영향이 없다(아이콘만 늦게 바뀔 뿐).
    /// </summary>
    /// <param name="changedFile">
    /// 내용이 바뀐 파일(보통 이 앱의 exe). 지정하면 그 항목을 콕 집어 먼저 알린다.
    /// </param>
    public static void Refresh(string? changedFile = null)
    {
        try
        {
            // 바뀐 exe 를 먼저 알린다 — 이 파일에서 아이콘을 뽑는 바로가기들이 대상이 된다.
            if (!string.IsNullOrWhiteSpace(changedFile))
                SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSH, changedFile, IntPtr.Zero);

            // 아이콘 캐시 전반을 다시 읽게 한다(설치 프로그램들이 쓰는 신호).
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST | SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero);

            Logger.Info("Requested shell icon cache refresh.");
        }
        catch (Exception ex)
        {
            Logger.Error("Shell icon cache refresh failed.", ex);
        }
    }
}
