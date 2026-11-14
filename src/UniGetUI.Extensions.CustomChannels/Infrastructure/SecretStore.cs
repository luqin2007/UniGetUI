using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UniGetUI.Extensions.CustomChannels.Infrastructure;

/// <summary>
/// 使用 Windows DPAPI（CurrentUser）加密保存网盘 Cookie、OpenList 口令等敏感信息。
/// 密文文件位于程序目录 state 下。
/// </summary>
public sealed class SecretStore
{
    private const string FileName = "secrets.json";

    private readonly string _path;
    private readonly Dictionary<string, string> _secrets;

    public SecretStore(AppPaths paths)
    {
        Directory.CreateDirectory(paths.State);
        _path = Path.Combine(paths.State, FileName);
        _secrets = Load(_path);
    }

    public IReadOnlyCollection<string> Keys => _secrets.Keys;

    public string? Get(string key)
    {
        if (!_secrets.TryGetValue(key, out var protectedValue))
        {
            return null;
        }

        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(protectedValue),
                optionalEntropy: null,
                scope: DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException)
        {
            // 换用户或系统迁移后无法解密，视作未保存
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public void Set(string key, string value)
    {
        _secrets[key] = Convert.ToBase64String(
            ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value),
                optionalEntropy: null,
                scope: DataProtectionScope.CurrentUser));
        Save();
    }

    public bool Remove(string key)
    {
        if (!_secrets.Remove(key))
        {
            return false;
        }

        Save();
        return true;
    }

    public bool Has(string key) => _secrets.ContainsKey(key);

    private void Save()
    {
        JsonFileStore.Write(_path, _secrets, AppJsonContext.Default.DictionaryStringString);
    }

    private static Dictionary<string, string> Load(string path)
    {
        try
        {
            return JsonFileStore.Read(path, AppJsonContext.Default.DictionaryStringString)
                   ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
