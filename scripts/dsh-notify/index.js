"use strict";
/*
 * dsh-notify —— dsh 会话完成 Windows 通知插件（cordis 宿主侧，由 dsh-app 壳首启幂等安装）
 *
 * 事件源：dsh-api-session-controller 官方广播
 *   ctx.emit("api-session/status", agent.id, status === "running")   // lib/index.js:2621
 * running true→false 边沿 = 任务完成 → 弹 WinRT Toast（无去抖：Four 拍板，秒回也通知，
 * 0.1.1 的 minBusySeconds 已删除）。
 *
 * 配置：$DSH_HOME/settings.yaml 的 `dsh-notify:` 命名空间（enabled）。
 * 完成边沿到达时重读一次配置——壳的「会话完成通知」开关改动即时生效，无需重启 harness。
 * 宽容原则：不认识的参数/坏配置/弹窗失败一律跳过，绝不影响 harness 主流程。
 */

const { spawn } = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");

exports.name = "dsh-notify";

// WinRT Toast 的 AppID 借用 PowerShell 自有标识（免注册 AUMID，弹窗来源显示为 PowerShell）
const TOAST_APP_ID =
  "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe";

function log(msg) {
  try { console.log("[dsh-notify] " + msg); } catch { /* 日志失败无碍 */ }
}

function dshHome() {
  return process.env.DSH_HOME || path.join(os.homedir(), ".dsh");
}

// 极简 YAML 命名空间读取：只认顶层 `dsh-notify:` 块下平铺的 key: value
// （缩进行解析、行内注释剥离；嵌套/数组等复杂结构不属于本插件的配置面，跳过）
function loadConfig() {
  const cfg = { enabled: true };
  let text;
  try {
    text = fs.readFileSync(path.join(dshHome(), "settings.yaml"), "utf8");
  } catch {
    return cfg; // 文件不存在/读失败：默认开
  }
  try {
    let inBlock = false;
    for (const line of text.split(/\r?\n/)) {
      if (/^\S/.test(line)) { inBlock = /^dsh-notify\s*:/.test(line); continue; }
      if (!inBlock) continue;
      const m = line.match(/^\s+([A-Za-z]+)\s*:\s*(.*)$/);
      if (!m) continue;
      const value = m[2].split(/\s+#/)[0].trim(); // 剥行内注释
      if (m[1] === "enabled") cfg.enabled = !/^(false|no|0)$/i.test(value);
    }
  } catch (e) {
    log("settings.yaml 解析异常，沿用默认配置: " + (e && e.message));
  }
  return cfg;
}

function xmlEscape(s) {
  return String(s)
    .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;").replace(/'/g, "&apos;");
}

// WinRT Toast：整条 PowerShell 脚本 UTF-16LE base64 后走 -EncodedCommand，免疫引号/编码地狱
function toast(title, message) {
  const script = [
    "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null",
    "[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType = WindowsRuntime] | Out-Null",
    "$xml = New-Object Windows.Data.Xml.Dom.XmlDocument",
    "$xml.LoadXml('<toast><visual><binding template=\"ToastGeneric\"><text>" + xmlEscape(title) + "</text><text>" + xmlEscape(message) + "</text></binding></visual></toast>')",
    "$toast = [Windows.UI.Notifications.ToastNotification]::new($xml)",
    "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('" + TOAST_APP_ID + "').Show($toast)",
  ].join("\n");
  try {
    const encoded = Buffer.from(script, "utf16le").toString("base64");
    const p = spawn("powershell.exe",
      ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded],
      { windowsHide: true, stdio: "ignore" });
    p.on("error", e => log("powershell 拉起失败: " + (e && e.message)));
    p.unref(); // 不拖住 harness 进程
  } catch (e) {
    log("Toast 弹窗失败已跳过: " + (e && e.message));
  }
}

exports.apply = function (ctx) {
  log("已加载（enabled=" + loadConfig().enabled + "）");

  const lastRunning = new Map(); // agentId -> 最近一次状态（防御重复 idle 帧；host 宣称严格交替）

  try {
    ctx.on("api-session/status", (agentId, running) => {
      try {
        if (agentId == null) return;           // 不认识的参数形态：跳过
        const id = String(agentId);
        if (running) { lastRunning.set(id, true); return; }
        if (lastRunning.get(id) === false) return; // 重复 idle 帧：防御跳过
        lastRunning.set(id, false);
        // 插件加载前就在跑的会话只见收尾帧：照常通知（沿袭 v1 实测确认的语义）
        if (!loadConfig().enabled) return;     // 完成边沿重读配置：开关改动即时生效
        toast("DSH 输出完成", "会话 " + id.slice(0, 8) + " 的模型输出已完成");
      } catch (e) {
        log("状态帧处理异常已跳过: " + (e && e.message));
      }
    });
  } catch (e) {
    log("事件订阅失败（插件空转，不影响 harness）: " + (e && e.message));
  }
};
