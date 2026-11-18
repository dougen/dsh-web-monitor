using System.Text.RegularExpressions;

namespace DshWebMonitor.Services;

/// <summary>npm dist-tag 更新通道。</summary>
internal enum UpdateChannel
{
    Stable,
    Rc,
    Beta,
    Alpha,
    Next,
    All,
}

/// <summary>通道解析结果。</summary>
internal sealed record ChannelTarget(string Version, string ResolvedTag, bool UsedFallback);

internal static partial class UpdateChannels
{
    public static IReadOnlyList<UpdateChannel> Selectable { get; } = new[]
    {
        UpdateChannel.Stable,
        UpdateChannel.Rc,
        UpdateChannel.Beta,
        UpdateChannel.Alpha,
        UpdateChannel.Next,
        UpdateChannel.All,
    };

    public static string ToLabel(UpdateChannel channel) => channel switch
    {
        UpdateChannel.Stable => "stable",
        UpdateChannel.Rc => "rc",
        UpdateChannel.Beta => "beta",
        UpdateChannel.Alpha => "alpha",
        UpdateChannel.Next => "next",
        _ => "all",
    };

    public static string ToDistTag(UpdateChannel channel) => channel switch
    {
        UpdateChannel.Stable => "latest",
        UpdateChannel.Rc => "rc",
        UpdateChannel.Beta => "beta",
        UpdateChannel.Alpha => "alpha",
        UpdateChannel.Next => "next",
        _ => "latest",
    };

    /// <summary>
    /// 按通道解析目标版本：stable=latest；rc/beta/alpha/next 对应同名 dist-tag；
    /// rc/beta/alpha 缺失同名 tag 时先取所有 tag 中同一预发布线的最高版本，再缺失时回退 latest；
    /// all=所有 dist-tag 指向版本中的最高 semver（含预发布）。
    /// </summary>
    public static bool TryResolve(
        IReadOnlyDictionary<string, string> distTags,
        UpdateChannel channel,
        out ChannelTarget? target)
    {
        target = null;
        var versions = distTags
            .Where(pair => IsVersion(pair.Value))
            .ToList();
        if (versions.Count == 0)
        {
            return false;
        }

        if (channel == UpdateChannel.All)
        {
            var best = versions.OrderByDescending(pair => pair.Value, SemverComparer.Instance).First();
            target = new ChannelTarget(best.Value, "all", false);
            return true;
        }

        var tag = ToDistTag(channel);
        var hit = versions.FirstOrDefault(pair => string.Equals(pair.Key, tag, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(hit.Key))
        {
            target = new ChannelTarget(hit.Value, tag, false);
            return true;
        }

        // rc / beta / alpha：包方可能只把预发布版本挂在 next 或其它 tag 上（例如 dsh 的
        // 0.2.2-rc.2 只有 next 指向），此时要在所有 tag 里找同一预发布线的最高版本，
        // 而不是直接回退 latest 让更新「消失」。
        if (channel is UpdateChannel.Rc or UpdateChannel.Beta or UpdateChannel.Alpha)
        {
            var label = ToLabel(channel);
            var sameLine = versions
                .Where(pair => HasPrereleaseLabel(pair.Value, label))
                .OrderByDescending(pair => pair.Value, SemverComparer.Instance)
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(sameLine.Key))
            {
                target = new ChannelTarget(sameLine.Value, sameLine.Key, true);
                return true;
            }
        }

        // stable 通道缺失 latest 时，只能回退到最高的正式版；
        // 绝不能回退到「所有 tag 里最高的 semver」，那会把 rc / next 当成正式版安装。
        if (channel == UpdateChannel.Stable)
        {
            var stable = versions
                .Where(pair => !IsPrerelease(pair.Value))
                .OrderByDescending(pair => pair.Value, SemverComparer.Instance)
                .FirstOrDefault();
            if (string.IsNullOrEmpty(stable.Key))
            {
                return false;
            }

            target = new ChannelTarget(stable.Value, stable.Key, true);
            return true;
        }

        var latest = versions.FirstOrDefault(pair => string.Equals(pair.Key, "latest", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(latest.Key))
        {
            target = new ChannelTarget(latest.Value, "latest", true);
            return true;
        }

        return false;
    }

    public static bool IsVersion(string? value) =>
        !string.IsNullOrWhiteSpace(value) && VersionPattern().IsMatch(value.Trim());

    /// <summary>版本号是否带预发布标识（1.2.3-rc.1）。</summary>
    public static bool IsPrerelease(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        var buildIndex = text.IndexOf('+');
        if (buildIndex >= 0)
        {
            text = text[..buildIndex];
        }

        return text.Contains('-', StringComparison.Ordinal);
    }

    /// <summary>版本号的预发布线是否为指定标签（1.2.3-rc.2 的预发布线是 rc）。</summary>
    private static bool HasPrereleaseLabel(string version, string label)
    {
        var text = version.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        var buildIndex = text.IndexOf('+');
        if (buildIndex >= 0)
        {
            text = text[..buildIndex];
        }

        var prereleaseIndex = text.IndexOf('-');
        if (prereleaseIndex < 0 || prereleaseIndex == text.Length - 1)
        {
            return false;
        }

        var prerelease = text[(prereleaseIndex + 1)..];
        var dotIndex = prerelease.IndexOf('.');
        var firstIdentifier = dotIndex >= 0 ? prerelease[..dotIndex] : prerelease;
        return string.Equals(firstIdentifier, label, StringComparison.OrdinalIgnoreCase);
    }

    // 预发布段必须以 '-' 开头；允许 '[.-]' 会把 1.2.3.4 这类四段版本号误判为合法，
    // 而 Parse 只取前三段并与 1.2.3 比较相等，导致更新判断失真。
    [GeneratedRegex(@"^v?\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex VersionPattern();
}

/// <summary>semver 版本比较（含预发布标识规则）。</summary>
internal sealed class SemverComparer : IComparer<string>
{
    public static readonly SemverComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        var left = Parse(x);
        var right = Parse(y);

        var result = left.Major.CompareTo(right.Major);
        if (result != 0)
        {
            return result;
        }

        result = left.Minor.CompareTo(right.Minor);
        if (result != 0)
        {
            return result;
        }

        result = left.Patch.CompareTo(right.Patch);
        if (result != 0)
        {
            return result;
        }

        return ComparePrerelease(left.Prerelease, right.Prerelease);
    }

    private static (int Major, int Minor, int Patch, string? Prerelease) Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return (0, 0, 0, null);
        }

        var text = version.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        var buildIndex = text.IndexOf('+');
        if (buildIndex >= 0)
        {
            text = text[..buildIndex];
        }

        string? prerelease = null;
        var prereleaseIndex = text.IndexOf('-');
        if (prereleaseIndex >= 0)
        {
            prerelease = text[(prereleaseIndex + 1)..];
            text = text[..prereleaseIndex];
        }

        var parts = text.Split('.');
        var major = parts.Length > 0 && int.TryParse(parts[0], out var parsedMajor) ? parsedMajor : 0;
        var minor = parts.Length > 1 && int.TryParse(parts[1], out var parsedMinor) ? parsedMinor : 0;
        var patch = parts.Length > 2 && int.TryParse(parts[2], out var parsedPatch) ? parsedPatch : 0;
        return (major, minor, patch, prerelease);
    }

    private static int ComparePrerelease(string? left, string? right)
    {
        if (left is null && right is null)
        {
            return 0;
        }

        // 正式版高于同版本号的预发布版。
        if (left is null)
        {
            return 1;
        }

        if (right is null)
        {
            return -1;
        }

        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        for (var index = 0; index < Math.Max(leftParts.Length, rightParts.Length); index++)
        {
            if (index >= leftParts.Length)
            {
                return -1;
            }

            if (index >= rightParts.Length)
            {
                return 1;
            }

            var leftNumeric = int.TryParse(leftParts[index], out var leftNumber);
            var rightNumeric = int.TryParse(rightParts[index], out var rightNumber);
            int result;
            if (leftNumeric && rightNumeric)
            {
                result = leftNumber.CompareTo(rightNumber);
            }
            else if (leftNumeric)
            {
                result = -1;
            }
            else if (rightNumeric)
            {
                result = 1;
            }
            else
            {
                // SemVer 2.0 规定非数字预发布标识符按 ASCII 字典序（区分大小写）比较。
                result = string.Compare(leftParts[index], rightParts[index], StringComparison.Ordinal);
            }

            if (result != 0)
            {
                return result;
            }
        }

        return 0;
    }
}
