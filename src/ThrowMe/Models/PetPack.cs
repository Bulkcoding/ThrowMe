namespace ThrowMe.Models;

/// <summary>
/// Codex Pet 형식(pet.json + 스프라이트시트) 아틀라스 규격.
/// 시트는 8열 고정, 한 칸 192×208. 행마다 동작이 정해져 있고(버전 1 = 9행, 버전 2 = 11행),
/// 행 안에서 왼쪽부터 프레임이 이어지며 남는 칸은 투명하다.
/// 표는 clawd-on-desk 의 codex-pet-adapter 가 쓰는 값을 그대로 옮겼다.
/// </summary>
public static class PetAtlas
{
    public const int Columns = 8;
    public const int FrameWidth = 192;
    public const int FrameHeight = 208;
    public const int SheetWidth = Columns * FrameWidth;   // 1536
    public const int MinRows = 9;

    /// <summary>행 키 → 행 번호와 프레임별 표시 시간(ms). 프레임 수가 표와 다르면 시간은 순환해 쓴다.</summary>
    public static readonly PetRow[] Rows =
    {
        new("idle",          0, new[] { 280, 110, 110, 140, 140, 320 }),
        new("running-right", 1, new[] { 120, 120, 120, 120, 120, 120, 120, 220 }),
        new("running-left",  2, new[] { 120, 120, 120, 120, 120, 120, 120, 220 }),
        new("waving",        3, new[] { 140, 140, 140, 280 }),
        new("jumping",       4, new[] { 140, 140, 140, 140, 280 }),
        new("failed",        5, new[] { 140, 140, 140, 140, 140, 140, 140, 240 }),
        new("waiting",       6, new[] { 150, 150, 150, 150, 150, 260 }),
        new("running",       7, new[] { 120, 120, 120, 120, 120, 220 }),
        new("review",        8, new[] { 150, 150, 150, 150, 150, 280 }),
    };

    public static PetRow? Find(string key)
    {
        foreach (var r in Rows) if (r.Key == key) return r;
        return null;
    }
}

/// <summary>아틀라스의 한 행(동작 하나).</summary>
public sealed record PetRow(string Key, int Row, int[] Durations);

/// <summary>가져온 펫 팩 하나. 파일은 데이터 폴더의 pets/&lt;Id&gt;/ 아래에 있다.</summary>
public sealed class PetPack
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>시트 행 수(9 또는 11).</summary>
    public int Rows { get; set; }
    /// <summary>행별 실제 프레임 수(투명 칸 제외). 길이 = Rows.</summary>
    public int[] FrameCounts { get; set; } = Array.Empty<int>();

    /// <summary>팩 폴더(저장 시 채움, 직렬화하지 않음).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Dir { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string SheetPath => System.IO.Path.Combine(Dir, "spritesheet.png");

    /// <summary>행의 프레임 수. 표에 없는 행이거나 감지 실패면 표의 길이(없으면 0).</summary>
    public int FramesIn(int row)
    {
        if (row < 0 || row >= FrameCounts.Length) return 0;
        return FrameCounts[row];
    }
}
