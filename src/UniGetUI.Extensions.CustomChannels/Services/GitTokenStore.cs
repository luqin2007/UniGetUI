using UniGetUI.Extensions.CustomChannels.Infrastructure;

namespace UniGetUI.Extensions.CustomChannels.Services;

/// <summary>
/// GitHub / GitLab 全局访问令牌的存储：键值走 <see cref="SecretStore"/>（Windows DPAPI 加密），
/// 不进 updater.json 明文。作为 per-source Token 的回退：源未单独配置令牌时使用全局令牌。
/// </summary>
public sealed class GitTokenStore
{
    public const string GithubKey = "git.github.token";
    public const string GitlabKey = "git.gitlab.token";

    private readonly SecretStore _secrets;

    public GitTokenStore(SecretStore secrets)
    {
        _secrets = secrets;
    }

    public string? GetGithubToken() => _secrets.Get(GithubKey);

    public string? GetGitlabToken() => _secrets.Get(GitlabKey);

    /// <summary>保存令牌；传 null/空白表示清除。</summary>
    public void SetGithubToken(string? token) => Set(GithubKey, token);

    public void SetGitlabToken(string? token) => Set(GitlabKey, token);

    private void Set(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (_secrets.Has(key))
            {
                _secrets.Remove(key);
            }

            return;
        }

        _secrets.Set(key, value.Trim());
    }
}
