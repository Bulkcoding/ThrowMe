using System.Collections.Generic;
using System.Windows.Input;

namespace ThrowMe.Services;

/// <summary>
/// 단축키를 사람이 읽는 문구로 바꾼다("Ctrl + 좌클릭").
///
/// 설정 창의 입력 칸과, 슬라임 쪽 안내 문구가 <b>같은 표기</b>를 써야 해서 한곳에 모았다.
/// (사용자가 지정한 잡기 단축키를 알림에 그대로 보여 준다.)
/// </summary>
public static class HotkeyText
{
    /// <summary>수정자 비트(Alt=1, Ctrl=2, Shift=4, Win=8) → "Ctrl + Shift".</summary>
    public static string Mod(int mod)
    {
        var parts = new List<string>();
        if ((mod & 2) != 0) parts.Add("Ctrl");
        if ((mod & 4) != 0) parts.Add("Shift");
        if ((mod & 1) != 0) parts.Add("Alt");
        if ((mod & 8) != 0) parts.Add("Win");
        return string.Join(" + ", parts);
    }

    /// <summary>키 또는 마우스 버튼 → "G" / "좌클릭".</summary>
    public static string Key(int vk, int mouse)
    {
        if (vk != 0) return KeyName(vk);
        return mouse switch { 1 => "좌클릭", 2 => "우클릭", 3 => "중간클릭", _ => "" };
    }

    /// <summary>수정자와 키를 합친 전체 표기. 둘 다 비면 "(없음)".</summary>
    public static string Combo(int mod, int vk, int mouse)
    {
        string m = Mod(mod), k = Key(vk, mouse);
        if (m.Length == 0 && k.Length == 0) return "(없음)";
        if (m.Length == 0) return k;
        if (k.Length == 0) return m;
        return $"{m} + {k}";
    }

    /// <summary>Key.Oem3 처럼 알아보기 어려운 이름을 실제 새겨진 글자로 바꿔 보여준다.</summary>
    public static string KeyName(int vk) => vk switch
    {
        0xC0 => "`",   // Oem3 (물결/백틱)
        0xBD => "-",   // OemMinus
        0xBB => "=",   // OemPlus
        0xDB => "[",   // Oem4
        0xDD => "]",   // Oem6
        0xDC => "\\",  // Oem5
        0xBA => ";",   // Oem1
        0xDE => "'",   // Oem7
        0xBC => ",",   // OemComma
        0xBE => ".",   // OemPeriod
        0xBF => "/",   // Oem2
        _ => KeyInterop.KeyFromVirtualKey(vk).ToString(),
    };
}
