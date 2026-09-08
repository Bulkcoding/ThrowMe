using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;
using ThrowMe.Models;

namespace ThrowMe.Services;

/// <summary>
/// 그리기 창의 템플릿(보관한 그림) 저장소.
/// 경로: 데이터 폴더/templates/ { templates.json, &lt;id&gt;.png }
///
/// 자동 보관(공에 적용 시)은 <see cref="MaxAuto"/> 개까지만 남기고 오래된 것부터 지운다.
/// 이름 붙여 저장한 것은 사용자가 지울 때까지 남는다. 직전 자동 항목과 같은 그림(PNG 해시 동일)은 다시 보관하지 않는다.
/// </summary>
public static class DrawTemplateStore
{
    public const int MaxAuto = 20;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly object Gate = new();

    /// <summary>
    /// 시험용 저장 루트 대체. null 이면 데이터 폴더의 templates/ 를 쓴다.
    /// 검증 하네스가 실제 사용자 템플릿(자동 보관 상한 정리로 지워질 수 있음)을 건드리지 않게 하려는 격리 지점이다. 앱 코드는 설정하지 않는다.
    /// </summary>
    public static string? DirOverride { get; set; }

    public static string Dir
    {
        get
        {
            string dir = DirOverride ?? Path.Combine(AppPaths.Roaming, "templates");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string IndexPath => Path.Combine(Dir, "templates.json");

    public static string PathFor(string id) => Path.Combine(Dir, id + ".png");

    /// <summary>목록(최신 생성 순). PNG 파일이 없는 항목은 건너뛴다.</summary>
    public static List<DrawTemplate> List()
    {
        lock (Gate)
        {
            var all = ReadIndex();
            all.RemoveAll(t => !File.Exists(PathFor(t.Id)));
            all.Sort((a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
            return all;
        }
    }

    /// <summary>공에 적용할 때 자동 보관. 직전 자동 항목과 같은 그림이면 null.</summary>
    public static DrawTemplate? AddAuto(BitmapSource image, string theme)
    {
        lock (Gate)
        {
            byte[] png = Encode(image);
            string hash = HashOf(png);
            var all = ReadIndex();
            var lastAuto = all.Where(t => t.Auto).OrderByDescending(t => t.CreatedUtc).FirstOrDefault();
            if (lastAuto != null && lastAuto.Hash == hash) return null;

            var now = DateTime.Now;
            var t = new DrawTemplate
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = $"자동 · {now.Month}/{now.Day} {now:HH:mm}",
                Theme = theme,
                Auto = true,
                CreatedUtc = DateTime.UtcNow,
                Hash = hash,
            };
            if (!Write(t, png, all)) return null;

            // 자동 보관 상한: 오래된 것부터 지운다(이름 저장분은 건드리지 않는다).
            var autos = all.Where(x => x.Auto).OrderBy(x => x.CreatedUtc).ToList();
            while (autos.Count > MaxAuto)
            {
                var old = autos[0];
                autos.RemoveAt(0);
                all.Remove(old);
                try { File.Delete(PathFor(old.Id)); } catch (Exception ex) { Logger.Error($"Template trim delete failed: {old.Id}", ex); }
            }
            WriteIndex(all);
            return t;
        }
    }

    /// <summary>이름을 붙여 보관. 이름이 비면 "템플릿 N" 을 붙인다.</summary>
    public static DrawTemplate? AddNamed(BitmapSource image, string name, string theme)
    {
        lock (Gate)
        {
            byte[] png = Encode(image);
            var all = ReadIndex();
            string finalName = string.IsNullOrWhiteSpace(name) ? $"템플릿 {all.Count(x => !x.Auto) + 1}" : name.Trim();
            var t = new DrawTemplate
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = finalName,
                Theme = theme,
                Auto = false,
                CreatedUtc = DateTime.UtcNow,
                Hash = HashOf(png),
            };
            if (!Write(t, png, all)) return null;
            WriteIndex(all);
            return t;
        }
    }

    public static void Remove(string id)
    {
        lock (Gate)
        {
            var all = ReadIndex();
            all.RemoveAll(t => t.Id == id);
            try { if (File.Exists(PathFor(id))) File.Delete(PathFor(id)); }
            catch (Exception ex) { Logger.Error($"Template delete failed: {id}", ex); }
            WriteIndex(all);
        }
    }

    /// <summary>PNG 를 읽는다. decodeWidth &gt; 0 이면 그 폭으로 축소 디코드(썸네일용). 없거나 손상이면 null.</summary>
    public static BitmapSource? Load(string id, int decodeWidth = 0)
    {
        try
        {
            string path = PathFor(id);
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;   // 파일 핸들을 잡아 두지 않는다(삭제 가능)
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Logger.Error($"Template load failed: {id}", ex);
            return null;
        }
    }

    // ── 내부 ─────────────────────────────────────────────
    private static byte[] Encode(BitmapSource image)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static string HashOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>PNG 를 임시 파일로 쓰고 바꿔 넣은 뒤 목록에 추가한다. 실패하면 false(목록은 그대로).</summary>
    private static bool Write(DrawTemplate t, byte[] png, List<DrawTemplate> all)
    {
        try
        {
            string path = PathFor(t.Id);
            File.WriteAllBytes(path + ".tmp", png);
            File.Move(path + ".tmp", path, overwrite: true);
            all.Add(t);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Template save failed: {t.Name}", ex);
            return false;
        }
    }

    private static List<DrawTemplate> ReadIndex()
    {
        try
        {
            if (!File.Exists(IndexPath)) return new List<DrawTemplate>();
            return JsonSerializer.Deserialize<List<DrawTemplate>>(File.ReadAllText(IndexPath), Json) ?? new List<DrawTemplate>();
        }
        catch (Exception ex)
        {
            Logger.Error("Template index read failed; starting empty.", ex);
            return new List<DrawTemplate>();
        }
    }

    private static void WriteIndex(List<DrawTemplate> all)
    {
        try
        {
            string tmp = IndexPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, Json));
            File.Move(tmp, IndexPath, overwrite: true);
        }
        catch (Exception ex) { Logger.Error("Template index write failed.", ex); }
    }
}
