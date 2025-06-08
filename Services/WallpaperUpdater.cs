// WallpaperUpdater.cs
using System;
using System.IO;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ArtfulWall.Models;
using ArtfulWall.Utils;
using Timer = System.Threading.Timer;
using Point = SixLabors.ImageSharp.Point;
using PointF = SixLabors.ImageSharp.PointF;
using Size = SixLabors.ImageSharp.Size;
using SizeF = SixLabors.ImageSharp.SizeF;

// 为了解决 Configuration 冲突，给 ArtfulWall.Models.Configuration 起别名
using AWConfig = ArtfulWall.Models.Configuration;

namespace ArtfulWall.Services
{
    public class WallpaperUpdater : IDisposable
    {
        private const int MaxLoadRetries = 3;
        private const double GridUpdateIntervalSeconds = 10;

        private readonly HashSet<string> _allowedExtensions = new HashSet<string> { ".jpg", ".jpeg", ".png", ".bmp" };

        private int _minInterval;
        private int _maxInterval;
        private string _folderPath;
        private string _destFolder;
        private int _width;
        private int _height;
        private int _rows;
        private int _cols;
        private AWConfig.WallpaperMode _wallpaperMode;
        private bool _autoAdjustToDisplayChanges;
        private bool _adaptToDpiScaling;

        private readonly ImageManager _imageManager;
        private readonly SemaphoreSlim _updateLock = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<Grid, DateTime> _lastUpdateTimes;

        private List<string> _coverPaths;
        private List<Grid> _grids;
        private Image<Rgba32>? _wallpaper;
        private Timer? _timer;
        private bool _isFirstUpdate = true;
        private bool _disposed = false;

        // 多显示器支持字段
        private Dictionary<string, List<Grid>> _monitorGrids;
        private Dictionary<string, Image<Rgba32>> _monitorWallpapers;
        private List<DisplayInfo> _displays;
        private Dictionary<string, string> _monitorToDevicePathMap;

        // 存储当前配置
        private AWConfig? _currentConfig;
        private CancellationTokenSource? _displaySettingsChangeCts;

        public WallpaperUpdater(
            string folderPath,
            string destFolder,
            int width,
            int height,
            int rows,
            int cols,
            ImageManager imageManager,
            int minInterval,
            int maxInterval,
            AWConfig? config = null
        )
        {
            _folderPath   = folderPath   ?? throw new ArgumentNullException(nameof(folderPath));
            _destFolder   = destFolder   ?? throw new ArgumentNullException(nameof(destFolder));
            _width        = width;
            _height       = height;
            _rows         = rows;
            _cols         = cols;
            _imageManager = imageManager ?? throw new ArgumentNullException(nameof(imageManager));
            _minInterval  = minInterval;
            _maxInterval  = maxInterval;
            _wallpaperMode = AWConfig.WallpaperMode.PerMonitor; // 默认为每显示器模式
            _autoAdjustToDisplayChanges = true;
            _adaptToDpiScaling = true;
            _currentConfig = config;

            _lastUpdateTimes      = new ConcurrentDictionary<Grid, DateTime>();
            _coverPaths           = new List<string>();
            _grids                = new List<Grid>();
            _monitorGrids         = new Dictionary<string, List<Grid>>();
            _monitorWallpapers    = new Dictionary<string, Image<Rgba32>>();
            _displays             = new List<DisplayInfo>();
            _monitorToDevicePathMap = new Dictionary<string, string>();
        }

        public void Start()
        {
            if (string.IsNullOrWhiteSpace(_folderPath) || !Directory.Exists(_folderPath))
            {
                Console.WriteLine("文件夹路径无效或不存在。");
                return;
            }

            LoadAlbumCovers();
            DetectDisplays();
            InitializeWallpaperAndGrids();

            if (_autoAdjustToDisplayChanges)
            {
                DisplayManager.DisplaySettingsChanged += OnDisplaySettingsChanged;
            }

            ScheduleUpdate();
        }

        public void UpdateConfig(AWConfig cfg)
        {
            _folderPath  = cfg.FolderPath   ?? throw new ArgumentNullException(nameof(cfg.FolderPath));
            _destFolder  = cfg.DestFolder   ?? throw new ArgumentNullException(nameof(cfg.DestFolder));
            _width       = cfg.Width;
            _height      = cfg.Height;
            _rows        = cfg.Rows;
            _cols        = cfg.Cols;
            _minInterval = cfg.MinInterval;
            _maxInterval = cfg.MaxInterval;
            _wallpaperMode = cfg.Mode;
            _autoAdjustToDisplayChanges = cfg.AutoAdjustToDisplayChanges;
            _adaptToDpiScaling = cfg.AdaptToDpiScaling;
            
            _currentConfig = cfg.Clone(); // 克隆配置避免外部修改

            Console.WriteLine($"配置更新 - 已设置适配DPI: {_adaptToDpiScaling}, 壁纸模式: {_wallpaperMode}");

            LoadAlbumCovers();

            if (_autoAdjustToDisplayChanges)
            {
                DisplayManager.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                DisplayManager.DisplaySettingsChanged += OnDisplaySettingsChanged;
            }
            else
            {
                DisplayManager.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            }

            DetectDisplays();
            InitializeWallpaperAndGrids();

            _lastUpdateTimes.Clear();
            _isFirstUpdate = true;
            _timer?.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }

        private void DetectDisplays()
        {
            try
            {
                var task = Task.Run(() => DisplayManager.GetDisplays());
                if (task.Wait(3000))
                {
                    _displays = task.Result;
                }
                else
                {
                    Console.WriteLine("获取显示器信息超时，使用默认显示器信息");
                    _displays = GetDefaultDisplays();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"检测显示器时出错: {ex.Message}");
                _displays = GetDefaultDisplays();
            }

            if (_displays == null || _displays.Count == 0)
            {
                _displays = GetDefaultDisplays();
            }

            UpdateDisplayConfigInfo();
            UpdateMonitorToDevicePathMap();
        }

        private void UpdateDisplayConfigInfo()
        {
            if (_currentConfig?.MonitorConfigurations == null)
                return;

            foreach (var display in _displays)
            {
                var monitorConfig = _currentConfig.MonitorConfigurations
                    .FirstOrDefault(mc => mc.DisplayNumber == display.DisplayNumber);

                if (monitorConfig != null)
                {
                    monitorConfig.Width       = display.Width;
                    monitorConfig.Height      = display.Height;
                    monitorConfig.DpiScaling  = display.DpiScaling;
                    monitorConfig.MonitorId   = display.DeviceName;
                    monitorConfig.IsPortrait  = display.Orientation == DisplayInfo.OrientationType.Portrait ||
                                                display.Orientation == DisplayInfo.OrientationType.PortraitFlipped;

                    Console.WriteLine($"更新显示器 {display.DisplayNumber} 配置信息: 分辨率={display.Width}x{display.Height}, 行={monitorConfig.Rows}, 列={monitorConfig.Cols}");
                }
            }
        }

        private void UpdateMonitorToDevicePathMap()
        {
            _monitorToDevicePathMap.Clear();
            if (!DesktopWallpaperApi.IsPerMonitorWallpaperSupported())
                return;

            try
            {
                var wpInstance = (DesktopWallpaperApi.IDesktopWallpaper)new DesktopWallpaperApi.DesktopWallpaper();
                uint monitorCount = wpInstance.GetMonitorDevicePathCount();
                if (monitorCount == 0)
                    return;

                var monitorInfo = DesktopWallpaperApi.GetAllMonitorInfo();
                var mapped = new HashSet<string>();

                // 矩形匹配
                foreach (var d in _displays)
                {
                    foreach (var kv in monitorInfo)
                    {
                        var rect = kv.Value;
                        if (rect.Left == d.Bounds.Left && rect.Top == d.Bounds.Top &&
                            rect.Right == d.Bounds.Right && rect.Bottom == d.Bounds.Bottom)
                        {
                            _monitorToDevicePathMap[d.DeviceName] = kv.Key;
                            mapped.Add(d.DeviceName);
                            Console.WriteLine($"矩形匹配: 显示器 {d.DisplayNumber} -> {kv.Key}");
                            break;
                        }
                    }
                }

                // 序号匹配
                for (int i = 0; i < Math.Min(_displays.Count, (int)monitorCount); i++)
                {
                    var d = _displays[i];
                    if (mapped.Contains(d.DeviceName))
                        continue;

                    try
                    {
                        string path = wpInstance.GetMonitorDevicePathAt((uint)i);
                        if (!string.IsNullOrEmpty(path))
                        {
                            _monitorToDevicePathMap[d.DeviceName] = path;
                            Console.WriteLine($"序号匹配: 显示器 {i} -> {path}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"序号映射时出错: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"映射设备路径时出错: {ex.Message}");
            }

            Console.WriteLine($"成功映射 {_monitorToDevicePathMap.Count} 个显示器");
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            _displaySettingsChangeCts?.Cancel();
            _displaySettingsChangeCts = new CancellationTokenSource();
            var token = _displaySettingsChangeCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    if (!await _updateLock.WaitAsync(TimeSpan.FromSeconds(5), token))
                    {
                        Console.WriteLine("无法获取更新锁，放弃变更处理");
                        return;
                    }

                    try
                    {
                        if (token.IsCancellationRequested) return;
                        Console.WriteLine("检测到显示设置变更，正在重新配置壁纸...");
                        DetectDisplays();
                        if (token.IsCancellationRequested) return;
                        InitializeWallpaperAndGrids();
                        if (token.IsCancellationRequested) return;
                        _lastUpdateTimes.Clear();
                        _isFirstUpdate = true;
                        await UpdateWallpaper();
                    }
                    finally
                    {
                        _updateLock.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("变更处理被取消");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"变更处理时出错: {ex.Message}");
                }
            }, token);
        }

        private void LoadAlbumCovers()
        {
            int retries = 0;
            while (retries < MaxLoadRetries)
            {
                try
                {
                    _coverPaths = Directory
                        .EnumerateFiles(_folderPath, "*.*", SearchOption.TopDirectoryOnly)
                        .Where(file =>
                            _allowedExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()) &&
                            !file.EndsWith("wallpaper.jpg", StringComparison.OrdinalIgnoreCase) &&
                            !file.Contains("_monitor_", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (_coverPaths.Count == 0)
                    {
                        MessageBox.Show(
                            $"在指定的文件夹 \"{_folderPath}\" 中未找到任何图片文件。\n\n请确保该文件夹包含JPG、PNG或BMP格式的图片。",
                            "未找到图片",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                    }
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"加载封面路径时出错：{ex.Message}");
                    retries++;
                    if (retries >= MaxLoadRetries)
                    {
                        Console.WriteLine("无法加载封面图片，将跳过加载。");
                    }
                }
            }
        }

        private void InitializeWallpaperAndGrids()
        {
            try
            {
                DisposeWallpapers();
                _grids.Clear();
                _monitorGrids.Clear();

                if (_wallpaperMode == AWConfig.WallpaperMode.Single)
                {
                    InitializeSingleWallpaper();
                }
                else
                {
                    InitializePerMonitorWallpapers();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"初始化时出错: {ex.Message}");
                FallbackInitialize();
            }
        }

        private void InitializeSingleWallpaper()
        {
            _wallpaper = new Image<Rgba32>(_width, _height);
            _grids = CreateGrids(_width, _height, _rows, _cols);
        }

        private void InitializePerMonitorWallpapers()
        {
            foreach (var display in _displays)
            {
                int rows = _rows, cols = _cols;
                var monCfg = _currentConfig?.MonitorConfigurations?
                    .FirstOrDefault(mc => mc.DisplayNumber == display.DisplayNumber);
                if (monCfg != null)
                {
                    Console.WriteLine($"使用显示器 {display.DisplayNumber} 特定配置: 行数={monCfg.Rows}, 列数={monCfg.Cols}");
                    rows = monCfg.Rows;
                    cols = monCfg.Cols;
                }

                int width = _adaptToDpiScaling ? display.Width : (int)(display.Width / display.DpiScaling);
                int height = _adaptToDpiScaling ? display.Height : (int)(display.Height / display.DpiScaling);
                Console.WriteLine($"显示器 {display.DisplayNumber} 分辨率: {width}x{height}, DPI缩放: {display.DpiScaling:F2}");

                var wallpaper = new Image<Rgba32>(width, height);
                _monitorWallpapers[display.DeviceName] = wallpaper;
                _monitorGrids[display.DeviceName] = CreateGrids(width, height, rows, cols);
            }
        }

        private void ScheduleUpdate()
        {
            _timer = new Timer(async _ => await UpdateWallpaper(), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }

        private async Task UpdateWallpaper()
        {
            bool lockAcquired = false;
            try
            {
                lockAcquired = await _updateLock.WaitAsync(TimeSpan.FromSeconds(3));
                if (!lockAcquired)
                {
                    Console.WriteLine("无法获取更新锁，跳过本次更新");
                    return;
                }

                var now = DateTime.Now;
                try
                {
                    if (_wallpaperMode == AWConfig.WallpaperMode.Single)
                    {
                        await UpdateSingleWallpaper(now);
                    }
                    else
                    {
                        await UpdatePerMonitorWallpapers(now);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"更新壁纸时出错: {ex.Message}");
                }
            }
            finally
            {
                if (lockAcquired) _updateLock.Release();

                if (!_disposed)
                {
                    try
                    {
                        int nextSec = Random.Shared.Next(_minInterval, _maxInterval + 1);
                        _timer?.Change(TimeSpan.FromSeconds(nextSec), Timeout.InfiniteTimeSpan);
                    }
                    catch
                    {
                        _timer?.Change(TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
                    }
                }
            }
        }

        private async Task UpdateSingleWallpaper(DateTime now)
        {
            if (_wallpaper == null) return;

            var candidates = GetUpdateCandidates(_grids, now);
            var used       = _grids.Select(g => g.CurrentCoverPath).ToList();
            var available  = _coverPaths.Except(used).ToList();

            foreach (var grid in candidates)
            {
                if (available.Count == 0)
                {
                    Console.WriteLine("无更多封面可用。");
                    break;
                }
                var path = available[Random.Shared.Next(available.Count)];
                available.Remove(path);
                await grid.UpdateCoverAsync(path, _wallpaper);
                _lastUpdateTimes[grid] = now;
            }

            string outPath = Path.Combine(_destFolder, "wallpaper.jpg");
            await _wallpaper.SaveAsJpegAsync(outPath);
            ApplySingleWallpaper(outPath);
            _isFirstUpdate = false;
        }

        private async Task UpdatePerMonitorWallpapers(DateTime now)
        {
            bool supportPerMonitor = DesktopWallpaperApi.IsPerMonitorWallpaperSupported();
            var wallpaperPaths = new Dictionary<string, string>();

            Console.WriteLine($"开始更新每显示器壁纸，共 {_displays.Count} 个显示器，支持每显示器: {supportPerMonitor}");

            foreach (var display in _displays)
            {
                if (!_monitorGrids.TryGetValue(display.DeviceName, out var grids) ||
                    !_monitorWallpapers.TryGetValue(display.DeviceName, out var wallpaper))
                {
                    Console.WriteLine($"跳过显示器 {display.DisplayNumber}：未找到网格或壁纸");
                    continue;
                }

                Console.WriteLine($"更新显示器 {display.DisplayNumber}，网格数: {grids.Count}");
                var candidates = GetUpdateCandidates(grids, now);
                var used       = grids.Select(g => g.CurrentCoverPath).ToList();
                var available  = _coverPaths.Except(used).ToList();

                foreach (var grid in candidates)
                {
                    if (available.Count == 0)
                    {
                        Console.WriteLine($"显示器 {display.DisplayNumber} 无更多封面可用。");
                        break;
                    }
                    var path = available[Random.Shared.Next(available.Count)];
                    available.Remove(path);
                    await grid.UpdateCoverAsync(path, wallpaper);
                    _lastUpdateTimes[grid] = now;
                }

                string outPath = Path.Combine(_destFolder, $"wallpaper_monitor_{display.DisplayNumber}.jpg");
                await wallpaper.SaveAsJpegAsync(outPath);

                if (supportPerMonitor && _monitorToDevicePathMap.TryGetValue(display.DeviceName, out var devicePath))
                {
                    wallpaperPaths[devicePath] = outPath;
                }
                else if (display.IsPrimary)
                {
                    WallpaperSetter.Set(outPath);
                    break;
                }
            }

            if (supportPerMonitor && wallpaperPaths.Count > 0)
            {
                try
                {
                    DesktopWallpaperApi.SetWallpaperForAllMonitors(wallpaperPaths);
                    Console.WriteLine("每显示器壁纸设置成功");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"批量设置失败: {ex.Message}，尝试逐一设置");
                    bool anySuccess = false;
                    foreach (var kv in wallpaperPaths)
                    {
                        try
                        {
                            DesktopWallpaperApi.SetWallpaperForAllMonitors(new Dictionary<string, string> { { kv.Key, kv.Value } });
                            anySuccess = true;
                        }
                        catch (Exception iex)
                        {
                            Console.WriteLine($"单独设置 {kv.Key} 失败: {iex.Message}");
                        }
                    }
                    if (!anySuccess)
                    {
                        var primary = _displays.FirstOrDefault(d => d.IsPrimary);
                        if (primary != null
                            && _monitorToDevicePathMap.TryGetValue(primary.DeviceName, out var pd)
                            && wallpaperPaths.TryGetValue(pd, out var ppath))
                        {
                            WallpaperSetter.Set(ppath);
                            Console.WriteLine("回退到主显示器壁纸");
                        }
                    }
                }
            }
            else if (wallpaperPaths.Count == 0)
            {
                Console.WriteLine("警告：未生成任何壁纸路径");
            }

            _isFirstUpdate = false;
        }

        private void DisposeWallpapers()
        {
            try
            {
                if (_wallpaper != null)
                {
                    var temp = _wallpaper;
                    _wallpaper = null;
                    temp.Dispose();
                }

                var copy = new Dictionary<string, Image<Rgba32>>(_monitorWallpapers);
                _monitorWallpapers.Clear();
                foreach (var wp in copy.Values)
                {
                    try { wp.Dispose(); }
                    catch (Exception ex) { Console.WriteLine($"释放壁纸时出错: {ex.Message}"); }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"释放资源时出错: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _displaySettingsChangeCts?.Cancel();
            _displaySettingsChangeCts?.Dispose();
            _displaySettingsChangeCts = null;

            _timer?.Dispose();
            _timer = null;

            DisplayManager.DisplaySettingsChanged -= OnDisplaySettingsChanged;

            Task.Run(() =>
            {
                DisposeWallpapers();

                foreach (var g in _grids.ToList())
                {
                    try { g.CurrentCover?.Dispose(); }
                    catch (Exception ex) { Console.WriteLine($"释放网格资源时出错: {ex.Message}"); }
                }
                _grids.Clear();

                foreach (var list in _monitorGrids.Values)
                {
                    foreach (var g in list.ToList())
                    {
                        try { g.CurrentCover?.Dispose(); }
                        catch (Exception ex) { Console.WriteLine($"释放监视器网格资源时出错: {ex.Message}"); }
                    }
                }
                _monitorGrids.Clear();

                _lastUpdateTimes.Clear();
                _monitorToDevicePathMap.Clear();
            });
        }

        // ==== 私有共通方法 ====

        private List<Grid> CreateGrids(int width, int height, int rows, int cols)
        {
            var grids = new List<Grid>();
            int baseSize = Math.Min(width / cols, height / rows);
            int totalW = baseSize * cols;
            int totalH = baseSize * rows;
            int remW = width - totalW;
            int remH = height - totalH;
            if (remW < 0 || remH < 0)
            {
                baseSize = Math.Max(1, baseSize - 1);
                totalW = baseSize * cols;
                totalH = baseSize * rows;
                remW = width - totalW;
                remH = height - totalH;
            }
            int gapW = cols > 1 ? remW / (cols - 1) : 0;
            int gapH = rows > 1 ? remH / (rows - 1) : 0;

            for (int i = 0; i < rows * cols; i++)
            {
                int c = i % cols, r = i / cols;
                float x = c * (baseSize + gapW);
                float y = r * (baseSize + gapH);
                grids.Add(new Grid(new PointF(x, y), new SizeF(baseSize, baseSize), _imageManager));
            }
            return grids;
        }

        private List<DisplayInfo> GetDefaultDisplays()
        {
            return new List<DisplayInfo>
            {
                new DisplayInfo
                {
                    DisplayNumber = 0,
                    Bounds = new System.Drawing.Rectangle(0, 0, _width, _height),
                    IsPrimary = true,
                    Orientation = DisplayInfo.OrientationType.Landscape,
                    DpiScaling = 1.0f
                }
            };
        }

        private List<Grid> GetUpdateCandidates(List<Grid> grids, DateTime now)
        {
            if (_isFirstUpdate)
                return new List<Grid>(grids);

            var due = grids.Where(g =>
                        !_lastUpdateTimes.ContainsKey(g) ||
                        (now - _lastUpdateTimes[g]).TotalSeconds >= GridUpdateIntervalSeconds
                      ).ToList();
            int maxCnt = Math.Min(due.Count, grids.Count / 4) + 1;
            // ensure maxCnt is at least 4 before using it as an upper bound
            int safeMax = maxCnt < 4 ? 3 : maxCnt;
            int cnt;
            if (due.Count <= 3)
            {
                cnt = due.Count;
            }
            else
            {
                // defensive: never pass a smaller upper bound than the lower bound
                cnt = Random.Shared.Next(3, safeMax);
            }
            return due.OrderBy(_ => Guid.NewGuid()).Take(cnt).ToList();
        }

        private void ApplySingleWallpaper(string path)
        {
            try { DesktopWallpaperApi.SetSingleWallpaper(path); }
            catch { WallpaperSetter.Set(path); }
        }

        private void FallbackInitialize()
        {
            if (_wallpaperMode == AWConfig.WallpaperMode.Single)
            {
                _wallpaper = new Image<Rgba32>(Math.Max(1920, _width), Math.Max(1080, _height));
                if (_grids.Count == 0)
                {
                    _grids = CreateGrids(_wallpaper.Width, _wallpaper.Height, _rows, _cols);
                }
            }
            else
            {
                var primary = _displays.FirstOrDefault(d => d.IsPrimary) ?? _displays.FirstOrDefault();
                if (primary != null)
                {
                    int w = primary.Bounds.Width;
                    int h = primary.Bounds.Height;
                    var wp = new Image<Rgba32>(w, h);
                    _monitorWallpapers[primary.DeviceName] = wp;
                    if (!_monitorGrids.ContainsKey(primary.DeviceName))
                    {
                        _monitorGrids[primary.DeviceName] = CreateGrids(w, h, _rows, _cols);
                    }
                }
            }
        }
    }
}
