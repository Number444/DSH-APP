using System.Security.Cryptography;
using System.Text;

namespace dsh_app.Helpers;

/// <summary>
/// DPAPI 加解密（CurrentUser 作用域）：settings.json 中密钥类字段的存储工具。
/// 同用户可解密，其他用户/拷贝文件不可用；解密失败返回 null（不抛异常）。
/// </summary>
public static class DpapiHelper
{
    /// <summary>加密为 Base64 密文；失败返回 null。</summary>
    public static string? Encrypt(string plain)
    {
        try
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解密 Base64 密文；空输入/失败返回 null。</summary>
    public static string? Decrypt(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(base64), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }
}
