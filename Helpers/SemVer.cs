namespace dsh_app.Helpers;

/// <summary>
/// semver 比较（简化版，不引第三方包；npm 生态标准规则，未来有需要可换 NuGet.Versioning）。
/// 供 Harness 更新（npm 包版本）与应用自更新（GitHub Release tag）共用。
/// 规则：数字段 major.minor.patch 从左到右比较，先大者新；
/// 同数字段时无 pre-release 段 &gt; 有 pre-release 段（如 1.0.0 &gt; 1.0.0-rc.6）；
/// pre-release 段按 . 分段比较（数字段按数值、字母段按字典序，数字段 &lt; 字母段），段数少者旧；
/// build metadata（+ 后缀）忽略；任一侧解析失败视为相同（不误报更新）。
/// </summary>
public static class SemVer
{
    /// <summary>版本比较：返回 a 相对 b 的关系（1 新 / 0 相同 / -1 旧）。</summary>
    public static int Compare(string? a, string? b)
    {
        if (!TryParse(a, out var amajor, out var aminor, out var apatch, out var apre)
            || !TryParse(b, out var bmajor, out var bminor, out var bpatch, out var bpre))
            return 0;

        if (amajor != bmajor) return amajor > bmajor ? 1 : -1;
        if (aminor != bminor) return aminor > bminor ? 1 : -1;
        if (apatch != bpatch) return apatch > bpatch ? 1 : -1;
        if (apre is null && bpre is null) return 0;
        if (apre is null) return 1;  // 无 pre-release 段 > 有 pre-release 段
        if (bpre is null) return -1;
        return ComparePreRelease(apre, bpre);
    }

    /// <summary>解析 semver 到数字段 + pre-release 段（build metadata 剥离后；失败返回 false）。</summary>
    private static bool TryParse(string? s, out int major, out int minor, out int patch, out string? pre)
    {
        major = minor = patch = 0;
        pre = null;
        if (string.IsNullOrEmpty(s)) return false;

        var core = s;
        var plus = core.IndexOf('+');
        if (plus >= 0) core = core[..plus]; // build metadata 不参与比较

        var dash = core.IndexOf('-');
        var numbers = (dash >= 0 ? core[..dash] : core).Split('.');
        var prePart = dash >= 0 ? core[(dash + 1)..] : null;
        if (numbers.Length != 3) return false;
        if (!int.TryParse(numbers[0], out major)
            || !int.TryParse(numbers[1], out minor)
            || !int.TryParse(numbers[2], out patch))
            return false;
        if (prePart is not null && prePart.Length == 0) return false;
        pre = prePart;
        return true;
    }

    /// <summary>pre-release 段比较：按 . 分段逐段比较，前段相同则段数少者旧（1.0.0-alpha &lt; 1.0.0-alpha.1）。</summary>
    private static int ComparePreRelease(string a, string b)
    {
        var sa = a.Split('.');
        var sb = b.Split('.');
        for (var i = 0; i < Math.Min(sa.Length, sb.Length); i++)
        {
            var cmp = ComparePreSegment(sa[i], sb[i]);
            if (cmp != 0) return cmp;
        }
        return sa.Length.CompareTo(sb.Length);
    }

    /// <summary>pre-release 单段比较：数字段按数值、字母段按字典序；数字段 &lt; 字母段（semver 标准规则）。</summary>
    private static int ComparePreSegment(string x, string y)
    {
        var xNum = long.TryParse(x, out var xn);
        var yNum = long.TryParse(y, out var yn);
        if (xNum && yNum) return xn.CompareTo(yn);
        if (xNum) return -1;
        if (yNum) return 1;
        return string.CompareOrdinal(x, y);
    }
}
