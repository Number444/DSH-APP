using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using dsh_app.Helpers;
using dsh_app.Server;
using dsh_app.Views;
using Microsoft.Web.WebView2.Core;

namespace dsh_app;

/// <summary>主窗口分部：Harness 更新与应用自更新两条状态机（检查 / 下载 / 安装 / 进度 / 取消）（自 MainWindow.xaml.cs 拆出，纯移动无逻辑变更）。</summary>
public partial class MainWindow
{
    // ---------------- 菜单（Harness 更新） ----------------

    /// <summary>弹出检查进度窗（非模态，Owner 主窗口；重复调用先关旧的）。</summary>
    private void ShowCheckProgress(string title, string detail)
    {
        _checkProgress?.Close();
        _checkProgress = new Views.UpdateProgressWindow(title, detail) { Owner = this };
        _checkProgress.Show();
    }

    /// <summary>关闭检查进度窗（幂等）。</summary>
    private void HideCheckProgress()
    {
        _checkProgress?.Close();
        _checkProgress = null;
    }

    /// <summary>检查 Harness 更新：顶栏菜单与托盘菜单共用入口。防重入，状态经 UpdateMenuItems 反映。</summary>
    private async Task CheckUpdateAsync()
    {
        if (_checking || _updating) return;
        _checking = true;
        UpdateMenuItems(); // "检查更新…"禁用态
        ShowCheckProgress("检查更新", "正在检查 Harness 更新…");
        try
        {
            // 手动点击一律重新检查（不复用启动时的陈旧结果，审查加固项）
            var ok = await _updater.CheckAsync();
            HideCheckProgress(); // 检查完成：先关进度窗再弹结果
            if (!ok)
            {
                new Views.ConfirmDialog("检查更新",
                    $"检查更新失败：{_updater.LastError ?? "未知原因"}。\n详情见日志。",
                    "知道了", glyph: "⚠") { Owner = this }.ShowDialog();
                return;
            }

            if (_updater.HasUpdate)
            {
                var dlg = new Views.ConfirmDialog("发现 Harness 新版本",
                    $"当前版本：v{_updater.LocalVersion ?? "未知"}\n" +
                    $"最新版本：v{_updater.LatestVersion}\n\n" +
                    "更新期间服务会中断，页面将重新加载（约 1 分钟）。是否继续？",
                    "更新", "暂不") { Owner = this };
                if (dlg.ShowDialog() == true)
                    await RunHarnessUpdateAsync();
            }
            else
            {
                new Views.ConfirmDialog("检查更新",
                    _updater.LocalVersion is null
                        ? "已是最新版本（本地版本未知）。"
                        : $"已是最新版本（v{_updater.LocalVersion}）。",
                    "知道了", glyph: "ℹ") { Owner = this }.ShowDialog();
            }
        }
        catch (Exception ex)
        {
            // fire-and-forget 入口：检查期间退出（HarnessUpdater.Dispose 释放管道流）会抛
            // ObjectDisposedException——必须在此兜住，否则直达 DispatcherUnhandledException 崩溃
            HideCheckProgress();
            AppendLog($"检查更新异常：{ex.Message}");
        }
        finally
        {
            _checking = false;
            UpdateMenuItems(); // 复位"检查更新…"文案（有新版则显示高亮提示）
        }
    }

    /// <summary>执行 Harness 更新：收起页面（R1）→ 停服 → npm install → 重启服务。</summary>
    private async Task RunHarnessUpdateAsync()
    {
        // 前置：端口服务必须由本应用管理（自家拉起或接管验证过的 dsh），非 dsh 占用时无法安全停服
        if (!_server.IsManaged)
        {
            new Views.ConfirmDialog("无法更新",
                "当前端口被非 dsh 服务占用，无法安全更新 Harness。\n请先停止该服务后再试。",
                "知道了", glyph: "⚠") { Owner = this }.ShowDialog();
            return;
        }

        _updating = true;
        _abortUpdateConfirmed = false;
        TitleBtnRestart.IsEnabled = false;
        UpdateMenuItems(); // 菜单项显示"正在更新 Harness…"
        try
        {
            // R1：WebView2 是 HWND 子窗口，覆盖层盖不住它——必须先收起页面
            WebView.Visibility = Visibility.Collapsed;
            ShowLoading();
            CenterSubtitle.Text = "正在更新 Harness…";

            _server.Shutdown(); // 必须先停服：运行中的 node 进程持有 bin.js 文件句柄，npm 覆盖安装会 EPERM
            var updated = await _updater.UpdateAsync();
            var restarted = await _server.EnsureServerAsync();

            if (updated && restarted)
            {
                AppendLog("Harness 更新成功，重新加载页面");
                UpdateMenuItems();
                new Views.ConfirmDialog("更新完成",
                    $"Harness 已更新到 v{_updater.LocalVersion}。",
                    "好的", glyph: "✓") { Owner = this }.ShowDialog();
                WebView.CoreWebView2.Navigate($"http://127.0.0.1:{_server.Port}");
            }
            else
            {
                ShowError("更新失败", updated
                    ? "服务重启失败，请重试或查看日志。"
                    : $"Harness 更新失败，已尝试恢复服务。\n{_updater.LastError ?? "详情见日志"}；若服务异常请手动执行：\nnpm install -g @deepseek-ai/dsh@latest",
                    allowRetry: true);
            }
        }
        catch (Exception ex)
        {
            ShowError("更新失败", ex.Message, allowRetry: true);
        }
        finally
        {
            _updating = false;
            TitleBtnRestart.IsEnabled = true;
            UpdateMenuItems(); // 恢复菜单文案（"正在更新 Harness…" → 检查更新/更新可用）
        }
    }

    /// <summary>
    /// 启动后后台自动检查一次（默认开，设置里可关）；只提示不自动安装。
    /// Harness 与应用两条检查并行（原串行：Harness 最坏 60s 超时期间应用检查干等）；
    /// 各自保留防重入标志与设置开关判断，异常自吞记日志。
    /// </summary>
    private void MaybeAutoCheckUpdate()
    {
        // 两条检查续体均回 UI 线程（Dispatcher 串行化），UpdateMenuItems 整体重建 ItemsSource 幂等，重入安全
        _ = AutoCheckHarnessAsync();
        _ = AutoCheckAppAsync();
    }

    private async Task AutoCheckHarnessAsync()
    {
        if (_autoCheckRan || _updating || !AppSettings.Current.AutoCheckUpdate) return;
        _autoCheckRan = true;
        try
        {
            var ok = await _updater.CheckAsync();
            if (ok && _updater.HasUpdate)
            {
                AppendLog($"发现 Harness 新版本：v{_updater.LocalVersion} → v{_updater.LatestVersion}");
                UpdateMenuItems();
            }
        }
        catch (Exception ex)
        {
            // 后台检查失败不打扰用户（写日志即可）
            AppendLog($"自动检查更新异常：{ex.Message}");
        }
    }

    private async Task AutoCheckAppAsync()
    {
        if (_appAutoCheckRan || _appUpdating || !AppSettings.Current.AutoCheckAppUpdate) return;
        _appAutoCheckRan = true;
        try
        {
            var ok = await _appUpdater.CheckAsync();
            if (ok && _appUpdater.HasUpdate)
            {
                AppendLog($"发现应用新版本：v{_appUpdater.LocalVersion} → v{_appUpdater.LatestVersion}");
                UpdateMenuItems();
            }
        }
        catch (Exception ex)
        {
            AppendLog($"自动检查应用更新异常：{ex.Message}");
        }
    }

    // ---------------- 菜单（应用更新：壳自身，GitHub Releases） ----------------

    /// <summary>检查应用更新：顶栏菜单与托盘菜单共用入口。防重入；就绪态（已下载待安装）时点击直接走安装确认。</summary>
    private async Task CheckAppUpdateAsync()
    {
        if (_appChecking || _appUpdating) return;
        if (_appUpdateReady)
        {
            _appUpdateReady = false;
            UpdateMenuItems();
            ConfirmInstallAppUpdate();
            return;
        }

        _appChecking = true;
        UpdateMenuItems(); // "检查应用更新…"禁用态
        ShowCheckProgress("检查应用更新", "正在检查应用更新（GitHub Releases）…");
        try
        {
            // 手动点击一律重新检查（不复用启动时的陈旧结果）
            var ok = await _appUpdater.CheckAsync();
            HideCheckProgress(); // 检查完成：先关进度窗再弹结果
            if (!ok)
            {
                new Views.ConfirmDialog("检查应用更新",
                    $"检查更新失败：{_appUpdater.LastError ?? "未知原因"}。\n详情见日志。",
                    "知道了", glyph: "⚠") { Owner = this }.ShowDialog();
                return;
            }

            if (_appUpdater.HasUpdate)
            {
                // Release 说明（Markdown 纯文本展示，不渲染；为空回退无 changelog 文案）
                var notes = _appUpdater.LatestReleaseNotes;
                var message = $"当前版本：v{_appUpdater.LocalVersion}\n最新版本：v{_appUpdater.LatestVersion}\n";
                if (notes is not null)
                    message += $"\n更新内容：\n{notes}\n";
                message += "\n下载期间服务不受影响；下载完成后应用将自动重启完成更新，重启时服务短暂中断（约 10~30 秒）。是否继续？";
                var dlg = new Views.ConfirmDialog("发现新版本",
                    message,
                    "下载更新", "暂不") { Owner = this };
                if (dlg.ShowDialog() == true)
                    await RunAppUpdateAsync();
            }
            else
            {
                new Views.ConfirmDialog("检查应用更新",
                    $"已是最新版本（v{_appUpdater.LocalVersion}）。",
                    "知道了", glyph: "ℹ") { Owner = this }.ShowDialog();
            }
        }
        catch (Exception ex)
        {
            // fire-and-forget 入口：检查期间退出（AppUpdater.Dispose）会抛 ObjectDisposedException，必须兜住
            HideCheckProgress();
            AppendLog($"检查应用更新异常：{ex.Message}");
        }
        finally
        {
            _appChecking = false;
            UpdateMenuItems();
        }
    }

    /// <summary>安装确认弹窗（就绪态入口）：确认后走安装段。</summary>
    private void ConfirmInstallAppUpdate()
    {
        var dlg = new Views.ConfirmDialog("应用更新已就绪",
            $"已下载 v{_appUpdater.LatestVersion}。是否现在重启应用完成更新？\n重启时服务短暂中断（约 10~30 秒）。",
            "立即重启", "稍后") { Owner = this };
        if (dlg.ShowDialog() == true)
            InstallDownloadedAppUpdate();
    }

    /// <summary>
    /// 应用更新完整流程：R1 收起页面 → 覆盖层下载（进度 + 日志 + 取消按钮）→ 校验 → 安装段。
    /// 下载不打断服务；托盘化（Hide）期间下载继续，完成置 _appUpdateReady（恢复窗口时确认）。
    /// </summary>
    private async Task RunAppUpdateAsync()
    {
        if (_appUpdating) return;
        _appUpdating = true;
        TitleBtnRestart.IsEnabled = false;
        _appDownloadCts = new CancellationTokenSource();
        UpdateMenuItems();
        try
        {
            // R1：WebView2 是 HWND 子窗口，覆盖层盖不住它——必须先收起页面
            WebView.Visibility = Visibility.Collapsed;
            ShowLoading();
            CenterSubtitle.Text = "正在下载应用更新…";
            ShowCancelDownloadButton();

            var ok = await _appUpdater.DownloadAndVerifyAsync(OnAppDownloadProgress, _appDownloadCts.Token);
            HideCancelDownloadButton();
            if (!ok)
            {
                if (_appDownloadCts.IsCancellationRequested)
                {
                    AppendLog("应用更新下载已取消");
                    RestoreAfterCancelledDownload();
                    return;
                }
                ShowError("更新失败",
                    $"下载或校验失败：{_appUpdater.LastError ?? "详情见日志"}。\n服务未受影响，可稍后重试。",
                    allowRetry: false);
                return;
            }

            // 下载完成：窗口可见 → 立即安装确认；窗口隐藏（托盘化）→ 气泡 + 待安装状态
            if (!IsVisible)
            {
                _appUpdateReady = true;
                UpdateMenuItems();
                try
                {
                    TrayIcon.ShowBalloonTip("DeepSeek Harness",
                        $"应用更新已下载完成（v{_appUpdater.LatestVersion}），恢复窗口后安装。",
                        Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
                }
                catch (Exception ex)
                {
                    AppendLog($"托盘提示失败: {ex.Message}");
                }
                return;
            }

            InstallDownloadedAppUpdate();
        }
        catch (Exception ex)
        {
            ShowError("更新失败", ex.Message, allowRetry: false);
        }
        finally
        {
            // 仅未进入重启路径时复位状态（进入重启路径后 _quitForAppUpdate=true，走 OnClosing 放行退出）
            if (!_quitForAppUpdate)
            {
                _appUpdating = false;
                TitleBtnRestart.IsEnabled = true;
                UpdateMenuItems();
            }
        }
    }

    /// <summary>安装段：生成更新器脚本 → 启动脚本（不等待）→ 放行退出。更新器在旧实例退出后覆盖 exe 并重启。</summary>
    private void InstallDownloadedAppUpdate()
    {
        _appUpdating = true;
        TitleBtnRestart.IsEnabled = false;
        _appDownloadCts = null;
        UpdateMenuItems();
        try
        {
            if (WebView.Visibility == Visibility.Visible)
            {
                // R1：收起页面（覆盖层可见）
                WebView.Visibility = Visibility.Collapsed;
            }
            ShowLoading();
            CenterSubtitle.Text = "正在准备更新…";

            var scriptPath = _appUpdater.WriteUpdateScript(_server.Port);
            if (scriptPath is null)
            {
                ShowError("更新失败", _appUpdater.LastError ?? "无法生成更新脚本。", allowRetry: false);
                return;
            }

            // 启动更新器（隐藏窗口，不等待；ArgumentList 传参免引号转义问题）
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-WindowStyle");
            psi.ArgumentList.Add("Hidden");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(scriptPath);
            Process.Start(psi);

            AppendLog("更新器已启动，应用即将重启完成更新");
            _quitForAppUpdate = true; // OnClosing 放行真退出（托盘化分支前短路）
            Close();
        }
        catch (Exception ex)
        {
            // 更新器启动失败：不退出应用，恢复 UI 与状态（服务不受影响）
            AppendLog($"启动更新器失败：{ex.Message}");
            ShowError("更新失败", $"无法启动更新器：{ex.Message}\n服务未受影响，可稍后重试。", allowRetry: false);
        }
        finally
        {
            if (!_quitForAppUpdate)
            {
                _appUpdating = false;
                TitleBtnRestart.IsEnabled = true;
                UpdateMenuItems();
            }
        }
    }

    /// <summary>下载进度回调（AppUpdater 已节流 250ms）：更新覆盖层副标题，不阻塞下载线程。</summary>
    private void OnAppDownloadProgress(long read, long total)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!IsVisible) return;
            CenterSubtitle.Text = total > 0
                ? $"正在下载应用更新… {read / 1048576.0:F1} / {total / 1048576.0:F0} MB（{read * 100 / total}%）"
                : $"正在下载应用更新… {read / 1048576.0:F1} MB";
        });
    }

    /// <summary>取消下载后恢复：隐藏覆盖层、恢复页面、菜单复位（状态复位在调用方 finally）。</summary>
    private void RestoreAfterCancelledDownload()
    {
        HideOverlay();
        WebView.Visibility = Visibility.Visible;
    }

    private void CancelDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        _appDownloadCts?.Cancel();
        CancelDownloadButton.IsEnabled = false;
        CenterSubtitle.Text = "正在取消下载…";
    }

    private void ShowCancelDownloadButton()
    {
        CancelDownloadButton.Visibility = Visibility.Visible;
        CancelDownloadButton.IsEnabled = true;
    }

    private void HideCancelDownloadButton()
    {
        CancelDownloadButton.Visibility = Visibility.Collapsed;
    }
}
