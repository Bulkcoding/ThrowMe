namespace ThrowMe.Models;

/// <summary>
/// 그리기 창에서 보관한 그림 한 장. PNG 는 데이터 폴더 templates/&lt;Id&gt;.png 에 있고,
/// 이 메타는 templates/templates.json 목록에 들어간다. 모든 테마가 한 목록을 공유한다.
/// </summary>
public sealed class DrawTemplate
{
    /// <summary>파일명으로 쓰는 고유 id(Guid "N" 형식).</summary>
    public string Id { get; set; } = "";
    /// <summary>표시 이름. 자동 보관은 "자동 · M/d HH:mm".</summary>
    public string Name { get; set; } = "";
    /// <summary>만든 테마의 표시 이름(예: "슬라임").</summary>
    public string Theme { get; set; } = "";
    /// <summary>공에 적용할 때 자동으로 보관된 것인가(상한 20개 정리 대상).</summary>
    public bool Auto { get; set; }
    public DateTime CreatedUtc { get; set; }
    /// <summary>PNG 바이트의 SHA-256(소문자 16진). 연속 중복 보관을 막는 데 쓴다.</summary>
    public string Hash { get; set; } = "";
}
