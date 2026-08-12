using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ThrowMe.Models;

namespace ThrowMe.Services;

/// <summary>
/// 테마(스킨)별 커스텀 이미지를 보관·로드한다.
/// 경로: %APPDATA%/ThrowMe/skins/&lt;스킨이름&gt;.png
///
/// 사용자가 고른 원본 파일을 그대로 참조하지 않고 **위 경로로 복사(PNG 재인코딩)** 한다.
/// 원본을 옮기거나 지워도 공이 깨지지 않게 하기 위함. 그림판으로 그린 결과도 같은 경로에 저장된다.
/// 외부 패키지 없이 WPF 내장 이미징(System.Windows.Media.Imaging)만 사용한다.
/// </summary>
public static class SkinImageStore
{
    /// <summary>저장 시 긴 변 최대 픽셀. 공은 최대 180px 로 표시되므로 이 이상은 낭비다.</summary>
    private const int MaxDimension = 512;

    private static readonly Dictionary<SlimeSkinKind, BitmapSource?> _cache = new();

    /// <summary>커스텀 이미지를 지원하는 테마. 몬스터볼/하이퍼볼/마스터볼은 고유 디자인이라 제외.</summary>
    public static bool Supports(SlimeSkinKind kind)
        => kind is SlimeSkinKind.Jelly or SlimeSkinKind.Billiard;

    private static string Dir
    {
        get
        {
            string dir = Path.Combine(AppPaths.Roaming, "skins");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string PathFor(SlimeSkinKind kind) => Path.Combine(Dir, $"{kind}.png");

    public static bool Has(SlimeSkinKind kind)
    {
        try { return File.Exists(PathFor(kind)); }
        catch { return false; }
    }

    /// <summary>보관된 이미지를 읽는다(캐시). 없거나 손상 시 null.</summary>
    public static BitmapSource? Load(SlimeSkinKind kind)
    {
        if (_cache.TryGetValue(kind, out var cached)) return cached;

        BitmapSource? img = null;
        try
        {
            string path = PathFor(kind);
            if (File.Exists(path))
            {
                // OnLoad + 스트림 직접 열기: 파일 핸들을 잡아두지 않아 이후 덮어쓰기가 가능하다.
                using var fs = File.OpenRead(path);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                // OnLoad 만 쓴다. StreamSource 와 IgnoreImageCache 를 함께 주면
                // UriSource 가 없어 WPF 내부 FinalizeCreation 이 터진다(ArgumentNullException).
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = fs;
                bmp.EndInit();
                bmp.Freeze();
                img = bmp;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Skin image load failed for {kind}.", ex);
        }

        _cache[kind] = img;
        return img;
    }

    /// <summary>사용자가 고른 이미지 파일을 이 테마의 커스텀 이미지로 가져온다. 성공 시 true.</summary>
    public static bool Import(SlimeSkinKind kind, string sourceFile)
    {
        try
        {
            BitmapSource src;
            using (var fs = File.OpenRead(sourceFile))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                // OnLoad 만 쓴다. StreamSource 와 IgnoreImageCache 를 함께 주면
                // UriSource 가 없어 WPF 내부 FinalizeCreation 이 터진다(ArgumentNullException).
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = fs;
                bmp.EndInit();
                bmp.Freeze();
                src = bmp;
            }
            return SaveBitmap(kind, Downscale(src));
        }
        catch (Exception ex)
        {
            Logger.Error($"Skin image import failed for {kind}.", ex);
            return false;
        }
    }

    /// <summary>비트맵(그림판 결과 등)을 이 테마의 커스텀 이미지로 저장한다. 성공 시 true.</summary>
    public static bool SaveBitmap(SlimeSkinKind kind, BitmapSource image)
    {
        try
        {
            string path = PathFor(kind);
            string tmp = path + ".tmp";

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var fs = File.Create(tmp)) encoder.Save(fs);

            File.Move(tmp, path, overwrite: true);
            _cache.Remove(kind);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Skin image save failed for {kind}.", ex);
            return false;
        }
    }

    /// <summary>파일을 밖에서 바꿔 썼을 때(방장 이미지 수신 등) 캐시를 버린다.</summary>
    public static void Invalidate(SlimeSkinKind kind) => _cache.Remove(kind);

    /// <summary>이 테마의 커스텀 이미지를 지운다.</summary>
    public static void Remove(SlimeSkinKind kind)
    {
        try
        {
            string path = PathFor(kind);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Logger.Error($"Skin image delete failed for {kind}.", ex);
        }
        _cache.Remove(kind);
    }

    /// <summary>긴 변이 MaxDimension 을 넘으면 비율을 유지해 줄인다.</summary>
    private static BitmapSource Downscale(BitmapSource src)
    {
        if (src.CanFreeze && !src.IsFrozen) src.Freeze();

        int longest = Math.Max(src.PixelWidth, src.PixelHeight);
        if (longest <= MaxDimension) return src;

        double f = MaxDimension / (double)longest;
        var scaled = new TransformedBitmap(src, new ScaleTransform(f, f));
        scaled.Freeze();
        return scaled;
    }
}
