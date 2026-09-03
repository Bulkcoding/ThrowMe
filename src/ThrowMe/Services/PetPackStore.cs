using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using Image = SixLabors.ImageSharp.Image;
using SixLabors.ImageSharp.PixelFormats;
using ThrowMe.Models;

namespace ThrowMe.Services;

/// <summary>
/// Codex Pet zip(pet.json + spritesheet.webp/png)을 가져와 보관하고 읽는다.
/// 경로: 데이터 폴더/pets/&lt;id&gt;/ { pack.json, spritesheet.png }
///
/// webp 는 WPF 가 기본으로 못 읽는 PC 가 있어(스토어 확장 필요) 가져올 때 ImageSharp 로 PNG 로 바꿔 둔다.
/// 실행 중에는 PNG 만 읽으므로 외부 패키지는 가져오기 순간에만 쓰인다.
/// </summary>
public static class PetPackStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly Dictionary<string, BitmapSource?> _sheets = new(StringComparer.OrdinalIgnoreCase);

    public static string Dir
    {
        get
        {
            string dir = Path.Combine(AppPaths.Roaming, "pets");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>보관 중인 팩 목록(이름순).</summary>
    public static List<PetPack> List()
    {
        var result = new List<PetPack>();
        try
        {
            foreach (string d in Directory.GetDirectories(Dir))
            {
                var p = Read(d);
                if (p != null) result.Add(p);
            }
        }
        catch (Exception ex) { Logger.Error("Pet pack list failed.", ex); }
        result.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    public static PetPack? Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return Read(Path.Combine(Dir, SafeId(id)));
    }

    private static PetPack? Read(string dir)
    {
        try
        {
            string meta = Path.Combine(dir, "pack.json");
            string sheet = Path.Combine(dir, "spritesheet.png");
            if (!File.Exists(meta) || !File.Exists(sheet)) return null;
            var p = JsonSerializer.Deserialize<PetPack>(File.ReadAllText(meta), Json);
            if (p == null || string.IsNullOrWhiteSpace(p.Id)) return null;
            p.Dir = dir;
            return p;
        }
        catch (Exception ex)
        {
            Logger.Error($"Pet pack read failed: {dir}", ex);
            return null;
        }
    }

    /// <summary>
    /// zip 을 검증·변환해 보관한다. 같은 id 가 있으면 덮어쓴다.
    /// 실패하면 null 을 돌려주고 <paramref name="error"/> 에 사용자에게 보여 줄 이유를 담는다.
    /// </summary>
    public static PetPack? Import(string zipPath, out string error)
    {
        error = "";
        string temp = Path.Combine(Path.GetTempPath(), "ThrowMe-pet-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            ZipFile.ExtractToDirectory(zipPath, temp, overwriteFiles: true);

            // pet.json 은 루트 또는 한 단계 아래 폴더에 있을 수 있다.
            string? petJson = Directory.GetFiles(temp, "pet.json", SearchOption.AllDirectories).FirstOrDefault();
            if (petJson == null) { error = "zip 안에 pet.json 이 없습니다."; return null; }
            string root = Path.GetDirectoryName(petJson)!;

            using var doc = JsonDocument.Parse(File.ReadAllText(petJson));
            var m = doc.RootElement;
            string id = m.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString()!.Trim() : "";
            string name = m.TryGetProperty("displayName", out var nEl) && nEl.ValueKind == JsonValueKind.String ? nEl.GetString()!.Trim() : "";
            string desc = m.TryGetProperty("description", out var dEl) && dEl.ValueKind == JsonValueKind.String ? dEl.GetString()!.Trim() : "";
            string sheetRel = m.TryGetProperty("spritesheetPath", out var sEl) && sEl.ValueKind == JsonValueKind.String ? sEl.GetString()!.Trim() : "";
            if (string.IsNullOrEmpty(id)) id = Path.GetFileNameWithoutExtension(zipPath);
            if (string.IsNullOrEmpty(name)) name = id;
            if (string.IsNullOrEmpty(sheetRel)) { error = "pet.json 에 spritesheetPath 가 없습니다."; return null; }

            string sheetSrc = Path.GetFullPath(Path.Combine(root, sheetRel.Replace('/', Path.DirectorySeparatorChar)));
            if (!sheetSrc.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) || !File.Exists(sheetSrc))
            { error = $"스프라이트시트 파일을 찾지 못했습니다: {sheetRel}"; return null; }

            using var img = Image.Load<Rgba32>(sheetSrc);
            if (img.Width != PetAtlas.SheetWidth || img.Height % PetAtlas.FrameHeight != 0 || img.Height / PetAtlas.FrameHeight < PetAtlas.MinRows)
            {
                error = $"시트 크기가 규격과 다릅니다. {PetAtlas.SheetWidth}×(208의 배수, 9행 이상)이어야 하는데 {img.Width}×{img.Height} 입니다.";
                return null;
            }
            int rows = img.Height / PetAtlas.FrameHeight;
            int[] counts = new int[rows];
            for (int r = 0; r < rows; r++) counts[r] = CountFrames(img, r);

            string safe = SafeId(id);
            string dir = Path.Combine(Dir, safe);
            Directory.CreateDirectory(dir);
            img.SaveAsPng(Path.Combine(dir, "spritesheet.png"));

            var pack = new PetPack { Id = safe, DisplayName = name, Description = desc, Rows = rows, FrameCounts = counts, Dir = dir };
            File.WriteAllText(Path.Combine(dir, "pack.json"), JsonSerializer.Serialize(pack, Json));
            _sheets.Remove(safe);
            Logger.Info($"Pet pack imported: {safe} ('{name}', {rows} rows, frames=[{string.Join(",", counts)}]).");
            return pack;
        }
        catch (Exception ex)
        {
            Logger.Error("Pet pack import failed.", ex);
            error = ex.Message;
            return null;
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    /// <summary>행에서 실제로 그림이 있는 칸 수(왼쪽부터 이어지는 것으로 본다).</summary>
    private static int CountFrames(Image<Rgba32> img, int row)
    {
        int count = 0;
        for (int c = 0; c < PetAtlas.Columns; c++)
        {
            if (!CellHasPixels(img, c, row)) break;
            count++;
        }
        return count;
    }

    private static bool CellHasPixels(Image<Rgba32> img, int col, int row)
    {
        int x0 = col * PetAtlas.FrameWidth, y0 = row * PetAtlas.FrameHeight;
        bool found = false;
        img.ProcessPixelRows(acc =>
        {
            for (int y = y0; y < y0 + PetAtlas.FrameHeight && !found; y += 3)
            {
                var span = acc.GetRowSpan(y);
                for (int x = x0; x < x0 + PetAtlas.FrameWidth; x += 3)
                    if (span[x].A > 16) { found = true; break; }
            }
        });
        return found;
    }

    public static void Remove(string id)
    {
        try
        {
            string dir = Path.Combine(Dir, SafeId(id));
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            _sheets.Remove(SafeId(id));
        }
        catch (Exception ex) { Logger.Error($"Pet pack remove failed: {id}", ex); }
    }

    /// <summary>시트 비트맵(캐시). 파일 핸들을 잡지 않도록 스트림으로 읽어 OnLoad 한다.</summary>
    public static BitmapSource? LoadSheet(PetPack pack)
    {
        if (_sheets.TryGetValue(pack.Id, out var cached)) return cached;
        BitmapSource? bmp = null;
        try
        {
            using var fs = File.OpenRead(pack.SheetPath);
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.StreamSource = fs;
            b.EndInit();
            b.Freeze();
            bmp = b;
        }
        catch (Exception ex) { Logger.Error($"Pet sheet load failed: {pack.SheetPath}", ex); }
        _sheets[pack.Id] = bmp;
        return bmp;
    }

    private static string SafeId(string id)
    {
        var chars = id.Trim().Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray();
        string s = new string(chars).Trim('-');
        return string.IsNullOrEmpty(s) ? "pet" : s.ToLowerInvariant();
    }
}
