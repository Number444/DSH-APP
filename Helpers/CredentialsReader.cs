using System.IO;
using YamlDotNet.Serialization;

namespace dsh_app.Helpers;

/// <summary>
/// 读取 dsh 凭据文件（~/.dsh/.credentials.yaml）中的 API Key（DeepSeek / Kimi Coding）。
/// 仅当用户显式授权（AppSettings.AllowReadDshCredentials == true）后调用；
/// 安全红线：本类不记录、不输出任何凭据内容（日志不得出现 key 明文）。
/// </summary>
public static class CredentialsReader
{
    /// <summary>DeepSeek API Key 的凭据引用名（dsh 默认；用户改过 apiKeyEnv 设置时以设置值为准，本期仅此键）。</summary>
    public const string DeepSeekApiKeyName = "DEEPSEEK_API_KEY";

    /// <summary>Kimi Coding API Key 的凭据引用名（Kimi for Coding 订阅额度查询用）。</summary>
    public const string KimiCodingApiKeyName = "KIMI_CODING_API_KEY";

    /// <summary>凭据文件默认目录：%USERPROFILE%\.dsh（dsh 官方默认，可被环境变量 DSH_HOME 覆盖）。</summary>
    public static string DefaultDshHome => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");

    /// <summary>
    /// dsh home 目录：DSH_HOME 环境变量（非空）优先，否则 %USERPROFILE%\.dsh。
    /// 注意：DSH_HOME 可能未设置或指向不存在的目录，读取时需容错（见 ReadDeepSeekApiKey 的兜底）。
    /// </summary>
    public static string? DshHomePath
    {
        get
        {
            var home = Environment.GetEnvironmentVariable("DSH_HOME");
            return string.IsNullOrWhiteSpace(home) ? DefaultDshHome : home;
        }
    }

    /// <summary>凭据文件路径（&lt;DshHomePath&gt;/.credentials.yaml）。</summary>
    public static string? CredentialsFilePath => DshHomePath is { } home
        ? Path.Combine(home, ".credentials.yaml")
        : null;

    /// <summary>默认目录下的凭据文件路径（DSH_HOME 未命中时的兜底路径）。</summary>
    public static string? DefaultCredentialsFilePath => Path.Combine(DefaultDshHome, ".credentials.yaml");

    /// <summary>凭据文件是否存在（&lt;DshHomePath&gt;/.credentials.yaml）。</summary>
    public static bool CredentialsFileExists => CredentialsFilePath is { } path && File.Exists(path);

    /// <summary>
    /// 仅当 AppSettings.AllowReadDshCredentials == true 时读取 DEEPSEEK_API_KEY 的值。
    /// </summary>
    public static string? ReadDeepSeekApiKey() => ReadKey(DeepSeekApiKeyName);

    /// <summary>
    /// 仅当 AppSettings.AllowReadDshCredentials == true 时读取 KIMI_CODING_API_KEY 的值。
    /// </summary>
    public static string? ReadKimiCodingApiKey() => ReadKey(KimiCodingApiKeyName);

    /// <summary>
    /// 用 YamlDotNet 解析顶层映射（键 = 凭据引用名，值 = 非空字符串），取指定键。
    /// 文件不存在 / 解析失败 / 无该键 → 返回 null（不抛异常）。
    /// DSH_HOME 指向的路径未命中时，兜底再试默认目录（设计文档 §2"两处都尝试"）。
    /// </summary>
    private static string? ReadKey(string keyName)
    {
        // 红线：仅用户显式授权后读取
        if (!AppSettings.Current.AllowReadDshCredentials)
            return null;

        var primary = CredentialsFilePath;
        var key = TryReadKey(primary, keyName);
        if (key is not null)
            return key;

        // 兜底：主路径与默认目录不同且未命中时，再试默认目录（DSH_HOME 可能不存在/指向别处）
        var fallback = DefaultCredentialsFilePath;
        if (fallback is not null &&
            !string.Equals(primary, fallback, StringComparison.OrdinalIgnoreCase))
        {
            key = TryReadKey(fallback, keyName);
        }
        return key;
    }

    /// <summary>读取单个路径下的凭据文件并取指定键；任何失败均返回 null（不抛异常）。</summary>
    private static string? TryReadKey(string? path, string keyName)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var deserializer = new DeserializerBuilder().Build();
            var dict = deserializer.Deserialize<Dictionary<string, string>>(text);
            if (dict is null)
                return null;

            // 值必须为非空字符串
            if (dict.TryGetValue(keyName, out var key) && !string.IsNullOrWhiteSpace(key))
                return key;

            return null;
        }
        catch
        {
            // 解析失败按"该来源不可用"处理，不报错打断；不记录文件内容（红线）
            return null;
        }
    }
}
