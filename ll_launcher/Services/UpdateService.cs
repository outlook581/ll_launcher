using System.Net.Http;
using System.Text.Json;
using System.Threading;

namespace ll_launcher.Services;

internal sealed record LauncherRelease(Version Version, string PageUrl);

internal static class UpdateService
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders = { { "User-Agent", "Qidian" } }
    };

    internal static async Task<LauncherRelease> GetLatestAsync(HttpClient? client = null)
    {
        client ??= Client;
        using var cancellation = new CancellationTokenSource();
        var github = ReadReleaseAsync(client, "https://api.github.com/repos/outlook581/ll_launcher/releases/latest",
            "https://github.com/outlook581/ll_launcher", cancellation.Token);
        var gitee = ReadReleaseAsync(client, "https://gitee.com/api/v5/repos/xiaoqinzi/ll_launcher/releases/latest",
            "https://gitee.com/xiaoqinzi/ll_launcher", cancellation.Token);

        var first = await Task.WhenAny(github, gitee).ConfigureAwait(false);
        var result = await first.ConfigureAwait(false);
        if (result.Release != null)
        {
            cancellation.Cancel();
            return result.Release;
        }

        var remaining = first == github ? gitee : github;
        var fallback = await remaining.ConfigureAwait(false);
        if (fallback.Release != null) return fallback.Release;
        var (githubError, giteeError) = first == github
            ? (result.Error, fallback.Error)
            : (fallback.Error, result.Error);
        throw new InvalidDataException($"GitHub：{githubError}；Gitee：{giteeError}");
    }

    private static async Task<(LauncherRelease? Release, string? Error)> ReadReleaseAsync(
        HttpClient client, string endpoint, string repositoryUrl, CancellationToken cancellationToken)
    {
        try
        {
            var json = await client.GetStringAsync(endpoint, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var release = document.RootElement;
            if (release.ValueKind != JsonValueKind.Object ||
                release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True ||
                release.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True ||
                !release.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String)
                return (null, "未找到正式版标签");

            var name = tag.GetString()!;
            if (!name.StartsWith('v') || !Version.TryParse(name.AsSpan(1), out var version) ||
                version.Build < 0 || version.Revision >= 0 || name != $"v{version}")
                return (null, "正式版标签应为 v主版本.次版本.修订号");

            return (new LauncherRelease(version, $"{repositoryUrl}/releases/tag/{name}"), null);
        }
        catch (OperationCanceledException) { return (null, "请求超时或已取消"); }
        catch (HttpRequestException error) { return (null, error.Message); }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return (null, "发行信息格式无效");
        }
    }
}
