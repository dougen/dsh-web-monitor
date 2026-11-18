using DshWebMonitor.Services;

namespace DshWebMonitor.Models;

/// <summary>插件管理界面的数据行。</summary>
internal sealed record PluginInfo(string Name, string Version, string? LatestVersion, bool IsEnabled)
{
    /// <summary>
    /// 是否存在真正的新版本：必须按 semver 比较（目标版本高于已安装版本），
    /// 不能用字符串不等判断，否则预发布 / 高版本被误判为「可更新」并降级安装。
    /// </summary>
    public bool HasUpdate =>
        !string.IsNullOrWhiteSpace(LatestVersion)
        && SemverComparer.Instance.Compare(LatestVersion, Version) > 0;
}

/// <summary>主页插件概况统计。</summary>
internal readonly record struct PluginSummary(int Installed, int Enabled, int Updatable);

/// <summary>一键更新结果。</summary>
internal readonly record struct PluginBulkUpdateResult(int Updated, int Failed);
