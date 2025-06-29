using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using ArtfulWall.Models;
using ArtfulWall.Services;
using ArtfulWall.UI;

namespace ArtfulWall.Core
{
    internal static class Constants
    {
        public const int DefaultImageCacheSize = 10;
        public const string BackupFileName     = "config_backup.json";
        public const string AutoStartKey       = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        public const string AutoStartValue     = "ArtfulWall";
        public static readonly string[] AllowedExt = { ".jpg", ".jpeg", ".png", ".bmp" };
    }

    public static class Program
    {
        // 单实例互斥名称
        private const string MutexName = "Global\\ArtfulWallSingleton";

        [STAThread]
        private static void Main()
        {
            // 单实例检查
            using var mutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show("ArtfulWall 已在运行中。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // WinForms 高 DPI 与样式配置
            ApplicationConfiguration.Initialize();

            // 创建统一的取消令牌源，后续所有后台任务都关联此令牌
            var cts = new CancellationTokenSource();

            // 立即启动消息循环，后续初始化全部在 TrayAppContext 内部异步完成
            Application.Run(new TrayAppContext(cts));
        }
    }

    // 托盘应用上下文，负责：显示托盘图标 → 异步初始化 → 初始化失败退出或继续加载菜单/业务 → 退出时统一取消 & 清理
    internal sealed class TrayAppContext : ApplicationContext
    {
        private readonly CancellationTokenSource _cts;
        private readonly string _configPath;
        private readonly string _backupPath;
        private NotifyIcon?      _trayIcon;
        private WallpaperUpdater? _updater;

        public TrayAppContext(CancellationTokenSource cts)
        {
            _cts = cts;

            // 配置文件路径（Roaming 下固定位置）
            string exeDir   = AppDomain.CurrentDomain.BaseDirectory;
            string roaming  = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ArtfulWall");
            Directory.CreateDirectory(roaming);

            _configPath = Path.Combine(roaming, "config.json");
            _backupPath = Path.Combine(roaming, Constants.BackupFileName);

            // 1.立即创建一个「正在启动」的托盘图标，确保 UI 可响应
            InitializeTrayIconLoading(exeDir);

            // 2.异步跑真正的初始化流程，绝不阻塞 UI 线程
            _ = InitializeAsync(exeDir);
        }

        // 创建托盘图标并显示「启动中…」提示
        private void InitializeTrayIconLoading(string exeDir)
        {
            _trayIcon = new NotifyIcon
            {
                Icon    = SystemIcons.Application,
                Text    = "ArtfulWall 启动中…",
                Visible = true
            };
        }

        // 真正的初始化流程：配置准备 → 读取 & 校验 → 业务启动 → 菜单创建
        private async Task InitializeAsync(string exeDir)
        {
            try
            {
                // 2.1 配置文件准备（异步拷贝，零阻塞）
                await EnsureConfigFilePresentAsync(_cts.Token).ConfigureAwait(false);

                // 2.2 读取 & 校验配置（包含原子写入 DestFolder）
                var cfg = await LoadAndValidateConfigAsync(_cts.Token).ConfigureAwait(false);

                // 2.3 异步检查无图片警告
                _ = ScanAndWarnIfNoImagesAsync(cfg.FolderPath, _cts.Token);

                // 2.4 启动 WallpaperUpdater，并异步备份配置
                if (!StartUpdater(cfg))
                    throw new InvalidOperationException("启动壁纸更新失败");

                // 2.5 切换托盘图标到正常状态，并创建菜单
                CreateContextMenu(exeDir);
                _trayIcon!.Icon = new Icon(Path.Combine(exeDir, "appicon.ico"));
                _trayIcon.Text = "ArtfulWall";
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                // 用户主动退出，不弹错误
                ExitThread();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"启动失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                ExitThread();
            }
        }

        #region 配置准备

        private static async Task EnsureConfigFilePresentAsync(CancellationToken token)
        {
            string exeDir   = AppDomain.CurrentDomain.BaseDirectory;
            string roaming  = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ArtfulWall");
            string config   = Path.Combine(roaming, "config.json");
            string backup   = Path.Combine(roaming, Constants.BackupFileName);
            string baseCfg  = Path.Combine(exeDir, "config.json");

            if (File.Exists(config))
                return;

            string src = File.Exists(backup) ? backup
                       : File.Exists(baseCfg) ? baseCfg
                       : throw new FileNotFoundException("未找到任何有效配置文件！");
            await CopyFileAsync(src, config, token).ConfigureAwait(false);
        }

        private static async Task CopyFileAsync(string src, string dst, CancellationToken token, bool overwrite = false)
        {
            const int bufSize = 81920;
            await using var srcFs = new FileStream(
                src, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufSize, useAsync: true);
            await using var dstFs = new FileStream(
                dst,
                overwrite ? FileMode.Create : FileMode.CreateNew,
                FileAccess.Write, FileShare.None,
                bufSize, useAsync: true);
            await srcFs.CopyToAsync(dstFs, bufSize, token).ConfigureAwait(false);
        }

        #endregion

        #region 读取 & 校验配置（含原子写入）

        private async Task<Configuration> LoadAndValidateConfigAsync(CancellationToken token)
        {
            // 读取
            string json = await Task.Run(() => File.ReadAllText(_configPath), token)
                                   .ConfigureAwait(false);
            var cfg = JsonSerializer.Deserialize<Configuration>(json)
                      ?? throw new JsonException("反序列化返回 null");

            // 自动补全 DestFolder
            if (string.IsNullOrWhiteSpace(cfg.DestFolder) || !Directory.Exists(cfg.DestFolder))
            {
                cfg.DestFolder = Path.Combine(cfg.FolderPath, "my_wallpaper");
                Directory.CreateDirectory(cfg.DestFolder);

                string tmp = _configPath + ".tmp";
                var opts = new JsonSerializerOptions { WriteIndented = true };
                await Task.Run(() =>
                {
                    File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, opts));
                    AtomicReplace(tmp, _configPath);
                }, token).ConfigureAwait(false);
            }

            // 验证
            ValidateConfiguration(cfg);
            return cfg;
        }

        private static void AtomicReplace(string tmpPath, string targetPath)
        {
            try
            {
                File.Replace(tmpPath, targetPath, null);
            }
            catch (PlatformNotSupportedException)
            {
                File.Delete(targetPath);
                File.Move(tmpPath, targetPath);
            }
        }

        private static void ValidateConfiguration(Configuration c)
        {
            if (!Directory.Exists(c.FolderPath))
                throw new DirectoryNotFoundException($"源文件夹 '{c.FolderPath}' 不存在。");
            if (c.Width  <= 0 || c.Height <= 0)
                throw new ArgumentException("壁纸宽高必须为正数。");
            if (c.Rows   <= 0 || c.Cols   <= 0)
                throw new ArgumentException("行列数必须为正数。");
            if (c.MinInterval <= 0)
                throw new ArgumentException("最小间隔必须为正数。");
            if (c.MaxInterval < c.MinInterval)
                throw new ArgumentException("最大间隔不能小于最小间隔。");
        }

        #endregion

        #region 无图片异步扫描

        private static async Task ScanAndWarnIfNoImagesAsync(string folder, CancellationToken token)
        {
            bool hasImages = await Task.Run(() =>
                Directory.Exists(folder) &&
                Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                         .Any(f => Constants.AllowedExt.Contains(Path.GetExtension(f).ToLowerInvariant())
                                && !f.EndsWith("wallpaper.jpg", StringComparison.OrdinalIgnoreCase))
            , token).ConfigureAwait(false);

            if (!hasImages)
            {
                MessageBox.Show(
                    $"目录 \"{folder}\" 中未找到任何图片（jpg/png/bmp）。",
                    "未找到图片", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        #endregion

        #region 启动 WallpaperUpdater

        private bool StartUpdater(Configuration c)
        {
            try
            {
                _updater = new WallpaperUpdater(
                    c.FolderPath, c.DestFolder,
                    c.Width, c.Height,
                    c.Rows, c.Cols,
                    new ImageManager(Constants.DefaultImageCacheSize),
                    c.MinInterval, c.MaxInterval,
                    c);

                _updater.Start();

                // 异步备份配置
                _ = Task.Run(() =>
                {
                    try { File.Copy(_configPath, _backupPath, overwrite: true); }
                    catch (Exception ex) { Debug.WriteLine($"[ArtfulWall] 备份配置失败: {ex}"); }
                }, _cts.Token);

                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region 托盘菜单（含自启动）

        private void CreateContextMenu(string exeDir)
        {
            if (_trayIcon == null) return;

            var menu    = new ContextMenuStrip();
            var autoItem = new ToolStripMenuItem("开机自启动", null, ToggleAutoStart)
            {
                Checked = IsAutoStartEnabled()
            };
            menu.Items.Add(autoItem);
            menu.Items.Add(new ToolStripMenuItem("配置编辑器",     null, OpenConfigEditor));
            menu.Items.Add(new ToolStripMenuItem("打开图片文件夹", null, OpenImageFolder));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("退出",           null, ExitApp));

            // 每次打开前刷新状态
            menu.Opening += (s, e) => autoItem.Checked = IsAutoStartEnabled();

            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.Visible          = true;
        }

        private void ToggleAutoStart(object? sender, EventArgs e)
        {
            if (sender is not ToolStripMenuItem item) return;

            bool wantEnable    = !item.Checked;
            bool previousState = item.Checked;

            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(Constants.AutoStartKey)
                             ?? throw new InvalidOperationException("无法创建或打开注册表项");

                var existingVal        = key.GetValue(Constants.AutoStartValue) as string;
                var existingNormalized = existingVal?.Trim('"') ?? string.Empty;
                var existingFullPath   = string.IsNullOrEmpty(existingNormalized)
                    ? string.Empty
                    : Path.GetFullPath(existingNormalized);

                var exePath     = Process.GetCurrentProcess().MainModule?.FileName
                                      ?? Application.ExecutablePath;
                var exeFullPath = Path.GetFullPath(exePath);
                var valueToSet  = exeFullPath.Contains(" ") ? $"\"{exeFullPath}\"" : exeFullPath;

                // 幂等判断
                if (wantEnable && existingFullPath.Equals(exeFullPath, StringComparison.OrdinalIgnoreCase))
                    return;
                if (!wantEnable && existingVal == null)
                    return;

                if (wantEnable)
                    key.SetValue(Constants.AutoStartValue, valueToSet);
                else
                    key.DeleteValue(Constants.AutoStartValue, throwOnMissingValue: false);

                item.Checked = wantEnable;
            }
            catch (Exception ex)
            {
                item.Checked = previousState;
                MessageBox.Show($"设置开机自启动失败：{ex.Message}",
                                "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static bool IsAutoStartEnabled()
        {
            using var key = Registry.CurrentUser.OpenSubKey(Constants.AutoStartKey);
            var val = key?.GetValue(Constants.AutoStartValue) as string;
            if (string.IsNullOrEmpty(val)) return false;

            var normalized = val.Trim('"');
            try
            {
                var full = Path.GetFullPath(normalized);
                var exe  = Process.GetCurrentProcess().MainModule?.FileName
                           ?? Application.ExecutablePath;
                return string.Equals(full, Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void OpenConfigEditor(object? sender, EventArgs e)
        {
            using var dlg = new ConfigEditorForm(_configPath);
            if (_updater != null) dlg.SetWallpaperUpdater(_updater);

            dlg.ShowDialog();
            if (dlg.ConfigChanged && _updater == null)
                Application.Restart();
        }

        private void OpenImageFolder(object? sender, EventArgs e)
        {
            try
            {
                var cfg = JsonSerializer.Deserialize<Configuration>(
                              File.ReadAllText(_configPath));
                if (cfg is null || !Directory.Exists(cfg.FolderPath))
                    throw new DirectoryNotFoundException();

                Process.Start("explorer.exe", cfg.FolderPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开图片文件夹：{ex.Message}",
                                "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ExitApp(object? sender, EventArgs e) => ExitThread();

        #endregion

        // 退出时取消所有异步操作并释放资源
        protected override void ExitThreadCore()
        {
            _cts.Cancel();

            _updater?.Dispose();
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }

            base.ExitThreadCore();
        }
    }
}
