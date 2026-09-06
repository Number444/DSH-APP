using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace dsh_app.Server;

/// <summary>
/// dsh-notify 插件安装器（会话完成通知 v2，方向文件 docs/NEXT-NOTIFY-PLUGIN.md）：
/// 壳首启把内嵌插件包幂等写入 profile——挂载链四要件：包本体（node_modules/dsh-notify/）+
/// profile package.json 的 dependencies 声明 + dsh.profile.bundles 登记 + 插件自身 package.json 的
/// `dsh.bundle.patch` 声明（指向包内 cordis.patch.yml；bundle 靠补丁文件挂载而非 main 入口直载，
/// 缺第四件 harness 启动抛 "declares no dsh.bundle" exit 1——首轮实测踩坑），
/// 并在 settings.yaml 补 dsh-notify 默认配置块。
/// 幂等策略：包本体按版本号比对，不一致才覆写；声明只增不删；配置块缺才补。
/// 生效时机：bundle 只在 harness 启动时加载——有实质变更时返回 true，由调用方提示「重启 dsh 服务后生效」。
/// （旧 SSE 通路 /api/events.host 已随新版 harness 移除，CompletionNotifier 同期删除；本类是其替代。）
/// </summary>
public static class NotifyPluginInstaller
{
    /// <summary>插件名 / 版本（版本号与内嵌 scripts/dsh-notify/package.json 保持一致；升级插件先升这里）。</summary>
    public const string PluginName = "dsh-notify";
    public const string PluginVersion = "0.1.2";

    private const string ResourcePrefix = "dsh_app.Scripts.dsh-notify.";
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 确保插件已安装（四要件 + settings.yaml 默认块）。返回是否有实质变更（首次安装/版本更新/补声明），
    /// 供调用方提示重启生效。profile 目录不存在等异常场景只记日志不抛出——通知是增值功能，绝不拖累主流程。
    /// </summary>
    public static bool EnsureInstalled(Action<string>? log = null)
    {
        try
        {
            var dshHome = DshHome();
            var profileDir = Path.Combine(dshHome, "profiles", "web");
            if (!Directory.Exists(profileDir))
            {
                log?.Invoke($"通知插件安装跳过：profile 目录不存在（{profileDir}）");
                return false;
            }

            var changed = EnsurePackageBody(profileDir, log);
            changed |= EnsureProfileDeclarations(profileDir, log);
            changed |= EnsureSettingsBlock(dshHome, enabled: true, log);
            return changed;
        }
        catch (Exception ex)
        {
            log?.Invoke($"通知插件安装失败（不影响主功能）：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 同步总开关到 settings.yaml 的 dsh-notify.enabled（壳设置页开关的唯一消费者；插件在完成边沿重读配置，
    /// 改动即时生效无需重启）。块缺失则按当前开关值补整块。
    /// </summary>
    public static void SyncEnabled(bool enabled, Action<string>? log = null)
    {
        try
        {
            var dshHome = DshHome();
            var yamlPath = Path.Combine(dshHome, "settings.yaml");
            var (text, encoding) = ReadTextPreservingEncoding(yamlPath);
            var newline = text.Contains("\r\n") ? "\r\n" : "\n";

            var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
            int blockStart = lines.FindIndex(l => Regex.IsMatch(l, @"^dsh-notify\s*:"));
            if (blockStart < 0)
            {
                AppendDefaultBlock(dshHome, enabled, log);
                return;
            }

            // 块内找 enabled 行（缩进行），找不到则插在块首之后；顺带清掉 0.1.2 起废弃的 minBusySeconds 遗留行
            var value = enabled ? "true" : "false";
            var changed = false;
            var enabledFound = false;
            int insertAt = blockStart + 1;
            for (int i = blockStart + 1; i < lines.Count; i++)
            {
                var l = lines[i];
                if (l.Length > 0 && !char.IsWhiteSpace(l[0])) break; // 顶层新键：块结束
                if (Regex.IsMatch(l, @"^\s+minBusySeconds\s*:"))
                {
                    lines.RemoveAt(i); // 去抖已删（Four 拍板）：遗留行无消费者，顺手清除
                    if (i < insertAt) insertAt--;
                    i--;
                    changed = true;
                    continue;
                }
                if (Regex.IsMatch(l, @"^\s+enabled\s*:"))
                {
                    enabledFound = true;
                    var replaced = Regex.Replace(l, @"(:\s*)\S+", "${1}" + value);
                    if (replaced != l) { lines[i] = replaced; changed = true; }
                    continue;
                }
                if (l.TrimStart().StartsWith('#')) insertAt = i + 1; // 注释行也算块内，插到注释后
            }
            if (!enabledFound)
            {
                lines.Insert(insertAt, "  enabled: " + value);
                changed = true;
            }
            if (changed)
                File.WriteAllText(yamlPath, string.Join(newline, lines), encoding);
        }
        catch (Exception ex)
        {
            log?.Invoke($"通知开关同步失败（不影响主功能）：{ex.Message}");
        }
    }

    // ---- 内部实现 ----

    private static string DshHome()
    {
        var env = Environment.GetEnvironmentVariable("DSH_HOME");
        return !string.IsNullOrWhiteSpace(env)
            ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
    }

    /// <summary>包本体：版本不一致才覆写（内嵌资源原样写盘，UTF-8 字节流不经二次编码）。
    /// 同版本但关键文件残缺（如 0.1.0 时代没有 cordis.patch.yml）也视为需覆写。</summary>
    private static bool EnsurePackageBody(string profileDir, Action<string>? log)
    {
        var pkgDir = Path.Combine(profileDir, "node_modules", PluginName);
        var installedPkgPath = Path.Combine(pkgDir, "package.json");
        if (File.Exists(installedPkgPath) && File.Exists(Path.Combine(pkgDir, "cordis.patch.yml")))
        {
            var m = Regex.Match(File.ReadAllText(installedPkgPath), "\"version\"\\s*:\\s*\"([^\"]+)\"");
            if (m.Success && m.Groups[1].Value == PluginVersion)
                return false; // 同版本且文件齐：本体与声明无需动
        }

        Directory.CreateDirectory(pkgDir);
        WriteEmbeddedResource(ResourcePrefix + "index.js", Path.Combine(pkgDir, "index.js"));
        WriteEmbeddedResource(ResourcePrefix + "package.json", installedPkgPath);
        WriteEmbeddedResource(ResourcePrefix + "cordis.patch.yml", Path.Combine(pkgDir, "cordis.patch.yml"));
        log?.Invoke($"通知插件包本体已写入 {pkgDir}（v{PluginVersion}）");
        return true;
    }

    /// <summary>profile package.json 两处声明（dependencies 版本钉 + dsh.profile.bundles 登记），只增不删。</summary>
    private static bool EnsureProfileDeclarations(string profileDir, Action<string>? log)
    {
        var pkgPath = Path.Combine(profileDir, "package.json");
        if (!File.Exists(pkgPath))
        {
            log?.Invoke("通知插件声明跳过：profile package.json 不存在");
            return false;
        }

        var changed = false;
        var root = JsonNode.Parse(File.ReadAllText(pkgPath))!.AsObject();

        var deps = root["dependencies"] as JsonObject;
        if (deps is null)
        {
            deps = new JsonObject();
            root["dependencies"] = deps;
        }
        if ((string?)deps[PluginName] != PluginVersion)
        {
            deps[PluginName] = PluginVersion;
            changed = true;
        }

        // dsh.profile.bundles 链：缺层补层，缺登记补登记
        if (root["dsh"] is not JsonObject dsh) root["dsh"] = dsh = new JsonObject();
        if (dsh["profile"] is not JsonObject profile) dsh["profile"] = profile = new JsonObject();
        if (profile["bundles"] is not JsonArray bundles) profile["bundles"] = bundles = new JsonArray();
        if (!bundles.Any(b => (string?)b == PluginName))
        {
            bundles.Add(PluginName);
            changed = true;
        }

        if (changed)
        {
            File.WriteAllText(pkgPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n", Utf8NoBom);
            log?.Invoke("通知插件声明已写入 profile package.json（dependencies + bundles）");
        }
        return changed;
    }

    /// <summary>settings.yaml：dsh-notify 块缺才补（含注释说明；追加写，不动既有内容与编码）。</summary>
    private static bool EnsureSettingsBlock(string dshHome, bool enabled, Action<string>? log)
    {
        var yamlPath = Path.Combine(dshHome, "settings.yaml");
        var (text, _) = ReadTextPreservingEncoding(yamlPath);
        if (Regex.IsMatch(text, @"(?m)^dsh-notify\s*:"))
            return false;
        AppendDefaultBlock(dshHome, enabled, log);
        return true;
    }

    private static void AppendDefaultBlock(string dshHome, bool enabled, Action<string>? log)
    {
        var yamlPath = Path.Combine(dshHome, "settings.yaml");
        var (text, encoding) = ReadTextPreservingEncoding(yamlPath);
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var block =
            (text.Length > 0 && !text.EndsWith('\n') ? nl : "") +
            nl +
            "# dsh-notify 插件：会话完成 Windows 通知（dsh-app 壳首启写入）" + nl +
            "# enabled 为总开关（壳设置页「会话完成通知」即改此处，插件完成边沿重读即时生效）" + nl +
            "dsh-notify:" + nl +
            $"  enabled: {(enabled ? "true" : "false")}" + nl;
        File.WriteAllText(yamlPath, text + block, encoding);
        log?.Invoke("settings.yaml 已补 dsh-notify 默认配置块");
    }

    /// <summary>读文本并保留原编码与 BOM 风格（StreamReader.CurrentEncoding 对无 BOM 文件返回带 BOM 的
    /// UTF8Encoding，直接拿去写会无中生有加 BOM——必须自判前导字节）。</summary>
    private static (string Text, Encoding Encoding) ReadTextPreservingEncoding(string path)
    {
        if (!File.Exists(path)) return ("", Utf8NoBom);
        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var encoding = hasBom ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: true) : Utf8NoBom;
        return (Encoding.UTF8.GetString(bytes), encoding);
    }

    private static void WriteEmbeddedResource(string logicalName, string targetPath)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var src = asm.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"内嵌资源缺失：{logicalName}");
        using var dst = File.Create(targetPath);
        src.CopyTo(dst);
    }
}
