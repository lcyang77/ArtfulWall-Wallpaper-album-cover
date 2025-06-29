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

using AWConfig = ArtfulWall.Models.Configuration;

namespace ArtfulWall.Services
{
    // 壁纸更新器 - 负责动态更新桌面壁纸
    // 支持单显示器和多显示器模式，可以根据配置自动更新壁纸内容
    public class WallpaperUpdater : IDisposable
    {
        // 最大加载重试次数
        private const int MaxLoadRetries = 3;
        
        // 网格更新间隔时间（秒）
        private const double GridUpdateIntervalSeconds = 10;

        // 允许的图片文件扩展名
        private readonly HashSet<string> _allowedExtensions = new HashSet<string> { ".jpg", ".jpeg", ".png", ".bmp" };

        // 更新间隔最小值（秒）
        private int _minInterval;
        
        // 更新间隔最大值（秒）
        private int _maxInterval;
        
        // 图片源文件夹路径
        private string _folderPath;
        
        // 壁纸输出文件夹路径
        private string _destFolder;
        
        // 壁纸宽度
        private int _width;
        
        // 壁纸高度
        private int _height;
        
        // 网格行数
        private int _rows;
        
        // 网格列数
        private int _cols;
        
        // 壁纸模式（单显示器/多显示器）
        private AWConfig.WallpaperMode _wallpaperMode;
        
        // 是否自动适应显示设置变化
        private bool _autoAdjustToDisplayChanges;
        
        // 是否适应DPI缩放
        private bool _adaptToDpiScaling;

        // 图片管理器
        private readonly ImageManager _imageManager;
        
        // 更新操作的信号量锁，确保同时只有一个更新操作
        private readonly SemaphoreSlim _updateLock = new SemaphoreSlim(1, 1);
        
        // 记录每个网格的最后更新时间
        private readonly ConcurrentDictionary<Grid, DateTime> _lastUpdateTimes;

        // 图片文件路径列表
        private List<string> _coverPaths;
        
        // 单显示器模式下的网格列表
        private List<Grid> _grids;
        
        // 单显示器模式下的壁纸图像
        private Image<Rgba32>? _wallpaper;
        
        // 定时器，用于定期更新壁纸
        private Timer? _timer;
        
        // 是否为首次更新
        private bool _isFirstUpdate = true;
        
        // 是否已释放资源
        private bool _disposed = false;

        // 多显示器支持相关字段
        // 每个显示器对应的网格列表
        private Dictionary<string, List<Grid>> _monitorGrids;
        
        // 每个显示器对应的壁纸图像
        private Dictionary<string, Image<Rgba32>> _monitorWallpapers;
        
        // 显示器信息列表
        private List<DisplayInfo> _displays;
        
        // 显示器名称到设备路径的映射
        private Dictionary<string, string> _monitorToDevicePathMap;

        // 当前配置信息
        private AWConfig? _currentConfig;
        
        // 显示设置变更的取消令牌源
        private CancellationTokenSource? _displaySettingsChangeCts;

        // 构造函数 - 初始化壁纸更新器
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
            // 参数验证和赋值
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

            // 初始化集合
            _lastUpdateTimes      = new ConcurrentDictionary<Grid, DateTime>();
            _coverPaths           = new List<string>();
            _grids                = new List<Grid>();
            _monitorGrids         = new Dictionary<string, List<Grid>>();
            _monitorWallpapers    = new Dictionary<string, Image<Rgba32>>();
            _displays             = new List<DisplayInfo>();
            _monitorToDevicePathMap = new Dictionary<string, string>();
        }

        // 启动壁纸更新器
        public void Start()
        {
            // 验证文件夹路径
            if (string.IsNullOrWhiteSpace(_folderPath) || !Directory.Exists(_folderPath))
            {
                Console.WriteLine("文件夹路径无效或不存在。");
                return;
            }

            // 执行初始化步骤
            LoadAlbumCovers();           // 加载图片文件
            DetectDisplays();            // 检测显示器
            InitializeWallpaperAndGrids(); // 初始化壁纸和网格

            // 如果启用了自动适应显示设置变化，则监听显示设置变更事件
            if (_autoAdjustToDisplayChanges)
            {
                DisplayManager.DisplaySettingsChanged += OnDisplaySettingsChanged;
            }

            // 开始定时更新
            ScheduleUpdate();
        }

        // 更新配置
        public void UpdateConfig(AWConfig cfg)
        {
            // 更新配置参数
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

            // 重新加载图片
            LoadAlbumCovers();

            // 重新设置显示设置变更监听
            if (_autoAdjustToDisplayChanges)
            {
                DisplayManager.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                DisplayManager.DisplaySettingsChanged += OnDisplaySettingsChanged;
            }
            else
            {
                DisplayManager.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            }

            // 重新初始化
            DetectDisplays();
            InitializeWallpaperAndGrids();

            // 清理并重新开始更新
            _lastUpdateTimes.Clear();
            _isFirstUpdate = true;
            _timer?.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }

        // 检测显示器信息
        private void DetectDisplays()
        {
            try
            {
                // 异步获取显示器信息，设置3秒超时
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

            // 确保有显示器信息
            if (_displays == null || _displays.Count == 0)
            {
                _displays = GetDefaultDisplays();
            }

            // 更新显示器配置信息
            UpdateDisplayConfigInfo();
            
            // 更新显示器到设备路径的映射
            UpdateMonitorToDevicePathMap();
        }

        // 更新显示器配置信息
        private void UpdateDisplayConfigInfo()
        {
            if (_currentConfig?.MonitorConfigurations == null)
                return;

            // 遍历每个显示器，更新其配置信息
            foreach (var display in _displays)
            {
                var monitorConfig = _currentConfig.MonitorConfigurations
                    .FirstOrDefault(mc => mc.DisplayNumber == display.DisplayNumber);

                if (monitorConfig != null)
                {
                    // 更新显示器的实际参数
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

        // 更新显示器名称到设备路径的映射关系
        private void UpdateMonitorToDevicePathMap()
        {
            _monitorToDevicePathMap.Clear();
            
            // 检查是否支持每显示器壁纸设置
            if (!DesktopWallpaperApi.IsPerMonitorWallpaperSupported())
                return;

            try
            {
                // 获取桌面壁纸API实例
                var wpInstance = (DesktopWallpaperApi.IDesktopWallpaper)new DesktopWallpaperApi.DesktopWallpaper();
                uint monitorCount = wpInstance.GetMonitorDevicePathCount();
                if (monitorCount == 0)
                    return;

                // 获取所有显示器信息
                var monitorInfo = DesktopWallpaperApi.GetAllMonitorInfo();
                var mapped = new HashSet<string>();

                // 通过矩形位置匹配显示器
                foreach (var d in _displays)
                {
                    foreach (var kv in monitorInfo)
                    {
                        var rect = kv.Value;
                        // 比较显示器的边界矩形
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

                // 通过序号匹配未匹配的显示器
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

        // 显示设置变更事件处理
        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            // 取消之前的变更处理
            _displaySettingsChangeCts?.Cancel();
            _displaySettingsChangeCts = new CancellationTokenSource();
            var token = _displaySettingsChangeCts.Token;

            // 异步处理显示设置变更
            Task.Run(async () =>
            {
                try
                {
                    // 尝试获取更新锁，超时5秒
                    if (!await _updateLock.WaitAsync(TimeSpan.FromSeconds(5), token))
                    {
                        Console.WriteLine("无法获取更新锁，放弃变更处理");
                        return;
                    }

                    try
                    {
                        if (token.IsCancellationRequested) return;
                        Console.WriteLine("检测到显示设置变更，正在重新配置壁纸...");
                        
                        // 重新检测显示器并初始化
                        DetectDisplays();
                        if (token.IsCancellationRequested) return;
                        
                        InitializeWallpaperAndGrids();
                        if (token.IsCancellationRequested) return;
                        
                        // 清理历史记录并立即更新
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

        // 加载专辑封面图片路径
        private void LoadAlbumCovers()
        {
            int retries = 0;
            while (retries < MaxLoadRetries)
            {
                try
                {
                    // 扫描文件夹中的图片文件
                    _coverPaths = Directory
                        .EnumerateFiles(_folderPath, "*.*", SearchOption.TopDirectoryOnly)
                        .Where(file =>
                            // 过滤允许的文件扩展名
                            _allowedExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()) &&
                            // 排除生成的壁纸文件
                            !file.EndsWith("wallpaper.jpg", StringComparison.OrdinalIgnoreCase) &&
                            !file.Contains("_monitor_", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    // 检查是否找到图片文件
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

        // 初始化壁纸和网格
        private void InitializeWallpaperAndGrids()
        {
            try
            {
                // 释放现有资源
                DisposeWallpapers();
                _grids.Clear();
                _monitorGrids.Clear();

                // 根据壁纸模式进行不同的初始化
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
                FallbackInitialize(); // 回退初始化
            }
        }

        // 初始化单显示器模式壁纸
        private void InitializeSingleWallpaper()
        {
            _wallpaper = new Image<Rgba32>(_width, _height);
            _grids = CreateGrids(_width, _height, _rows, _cols);
        }

        // 初始化多显示器模式壁纸
        private void InitializePerMonitorWallpapers()
        {
            foreach (var display in _displays)
            {
                // 获取显示器特定的行列配置
                int rows = _rows, cols = _cols;
                var monCfg = _currentConfig?.MonitorConfigurations?
                    .FirstOrDefault(mc => mc.DisplayNumber == display.DisplayNumber);
                if (monCfg != null)
                {
                    Console.WriteLine($"使用显示器 {display.DisplayNumber} 特定配置: 行数={monCfg.Rows}, 列数={monCfg.Cols}");
                    rows = monCfg.Rows;
                    cols = monCfg.Cols;
                }

                // 根据DPI适配设置计算实际分辨率
                int width = _adaptToDpiScaling ? display.Width : (int)(display.Width / display.DpiScaling);
                int height = _adaptToDpiScaling ? display.Height : (int)(display.Height / display.DpiScaling);
                Console.WriteLine($"显示器 {display.DisplayNumber} 分辨率: {width}x{height}, DPI缩放: {display.DpiScaling:F2}");

                // 为每个显示器创建壁纸和网格
                var wallpaper = new Image<Rgba32>(width, height);
                _monitorWallpapers[display.DeviceName] = wallpaper;
                _monitorGrids[display.DeviceName] = CreateGrids(width, height, rows, cols);
            }
        }

        // 安排定时更新
        private void ScheduleUpdate()
        {
            _timer = new Timer(async _ => await UpdateWallpaper(), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }

        // 更新壁纸的主方法
        private async Task UpdateWallpaper()
        {
            bool lockAcquired = false;
            try
            {
                // 尝试获取更新锁，超时3秒
                lockAcquired = await _updateLock.WaitAsync(TimeSpan.FromSeconds(3));
                if (!lockAcquired)
                {
                    Console.WriteLine("无法获取更新锁，跳过本次更新");
                    return;
                }

                var now = DateTime.Now;
                try
                {
                    // 根据壁纸模式选择更新方法
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

                // 安排下次更新
                if (!_disposed)
                {
                    try
                    {
                        // 随机选择下次更新的时间间隔
                        int nextSec = Random.Shared.Next(_minInterval, _maxInterval + 1);
                        _timer?.Change(TimeSpan.FromSeconds(nextSec), Timeout.InfiniteTimeSpan);
                    }
                    catch
                    {
                        // 如果出错，使用默认30秒间隔
                        _timer?.Change(TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
                    }
                }
            }
        }

        // 更新单显示器壁纸
        private async Task UpdateSingleWallpaper(DateTime now)
        {
            if (_wallpaper == null) return;

            // 获取需要更新的网格
            var candidates = GetUpdateCandidates(_grids, now);
            var used       = _grids.Select(g => g.CurrentCoverPath).ToList(); // 已使用的图片
            var available  = _coverPaths.Except(used).ToList();                // 可用的图片

            // 为每个候选网格更新封面
            foreach (var grid in candidates)
            {
                if (available.Count == 0)
                {
                    Console.WriteLine("无更多封面可用。");
                    break;
                }
                // 随机选择一张图片
                var path = available[Random.Shared.Next(available.Count)];
                available.Remove(path);
                await grid.UpdateCoverAsync(path, _wallpaper);
                _lastUpdateTimes[grid] = now;
            }

            // 保存并应用壁纸
            string outPath = Path.Combine(_destFolder, "wallpaper.jpg");
            await _wallpaper.SaveAsJpegAsync(outPath);
            ApplySingleWallpaper(outPath);
            _isFirstUpdate = false;
        }

        // 更新多显示器壁纸
        private async Task UpdatePerMonitorWallpapers(DateTime now)
        {
            bool supportPerMonitor = DesktopWallpaperApi.IsPerMonitorWallpaperSupported();
            var wallpaperPaths = new Dictionary<string, string>();

            Console.WriteLine($"开始更新每显示器壁纸，共 {_displays.Count} 个显示器，支持每显示器: {supportPerMonitor}");

            // 为每个显示器生成壁纸
            foreach (var display in _displays)
            {
                // 获取该显示器的网格和壁纸对象
                if (!_monitorGrids.TryGetValue(display.DeviceName, out var grids) ||
                    !_monitorWallpapers.TryGetValue(display.DeviceName, out var wallpaper))
                {
                    Console.WriteLine($"跳过显示器 {display.DisplayNumber}：未找到网格或壁纸");
                    continue;
                }

                Console.WriteLine($"更新显示器 {display.DisplayNumber}，网格数: {grids.Count}");
                
                // 获取需要更新的网格并分配图片
                var candidates = GetUpdateCandidates(grids, now);
                var used       = grids.Select(g => g.CurrentCoverPath).ToList();
                var available  = _coverPaths.Except(used).ToList();

                // 为每个候选网格更新封面
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

                // 保存该显示器的壁纸
                string outPath = Path.Combine(_destFolder, $"wallpaper_monitor_{display.DisplayNumber}.jpg");
                await wallpaper.SaveAsJpegAsync(outPath);

                // 记录壁纸路径用于后续设置
                if (supportPerMonitor && _monitorToDevicePathMap.TryGetValue(display.DeviceName, out var devicePath))
                {
                    wallpaperPaths[devicePath] = outPath;
                }
                else if (display.IsPrimary)
                {
                    // 如果不支持每显示器设置，只设置主显示器
                    WallpaperSetter.Set(outPath);
                    break;
                }
            }

            // 应用多显示器壁纸
            if (supportPerMonitor && wallpaperPaths.Count > 0)
            {
                try
                {
                    // 批量设置所有显示器壁纸
                    DesktopWallpaperApi.SetWallpaperForAllMonitors(wallpaperPaths);
                    Console.WriteLine("每显示器壁纸设置成功");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"批量设置失败: {ex.Message}，尝试逐一设置");
                    bool anySuccess = false;
                    
                    // 逐个设置显示器壁纸
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
                    
                    // 如果全部失败，回退到主显示器
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

        // 释放壁纸资源
        private void DisposeWallpapers()
        {
            try
            {
                // 释放单显示器壁纸
                if (_wallpaper != null)
                {
                    var temp = _wallpaper;
                    _wallpaper = null;
                    temp.Dispose();
                }

                // 释放多显示器壁纸
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

        // 释放所有资源
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 取消显示设置变更监听
            _displaySettingsChangeCts?.Cancel();
            _displaySettingsChangeCts?.Dispose();
            _displaySettingsChangeCts = null;

            // 停止定时器
            _timer?.Dispose();
            _timer = null;

            // 取消事件订阅
            DisplayManager.DisplaySettingsChanged -= OnDisplaySettingsChanged;

            // 异步释放资源避免阻塞
            Task.Run(() =>
            {
                // 释放壁纸资源
                DisposeWallpapers();

                // 释放单显示器网格资源
                foreach (var g in _grids.ToList())
                {
                    try { g.CurrentCover?.Dispose(); }
                    catch (Exception ex) { Console.WriteLine($"释放网格资源时出错: {ex.Message}"); }
                }
                _grids.Clear();

                // 释放多显示器网格资源
                foreach (var list in _monitorGrids.Values)
                {
                    foreach (var g in list.ToList())
                    {
                        try { g.CurrentCover?.Dispose(); }
                        catch (Exception ex) { Console.WriteLine($"释放监视器网格资源时出错: {ex.Message}"); }
                    }
                }
                _monitorGrids.Clear();

                // 清理字典
                _lastUpdateTimes.Clear();
                _monitorToDevicePathMap.Clear();
            });
        }

        // ==== 私有辅助方法 ====

        // 创建网格布局
        private List<Grid> CreateGrids(int width, int height, int rows, int cols)
        {
            var grids = new List<Grid>();
            
            // 计算基础网格大小
            int baseSize = Math.Min(width / cols, height / rows);
            int totalW = baseSize * cols;
            int totalH = baseSize * rows;
            int remW = width - totalW;   // 剩余宽度
            int remH = height - totalH;  // 剩余高度
            
            // 如果剩余空间为负，减小基础大小
            if (remW < 0 || remH < 0)
            {
                baseSize = Math.Max(1, baseSize - 1);
                totalW = baseSize * cols;
                totalH = baseSize * rows;
                remW = width - totalW;
                remH = height - totalH;
            }
            
            // 计算网格间距
            int gapW = cols > 1 ? remW / (cols - 1) : 0;
            int gapH = rows > 1 ? remH / (rows - 1) : 0;

            // 创建每个网格
            for (int i = 0; i < rows * cols; i++)
            {
                int c = i % cols, r = i / cols; // 列索引和行索引
                float x = c * (baseSize + gapW);
                float y = r * (baseSize + gapH);
                grids.Add(new Grid(new PointF(x, y), new SizeF(baseSize, baseSize), _imageManager));
            }
            return grids;
        }

        // 获取默认显示器信息
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

        // 获取需要更新的网格候选列表
        private List<Grid> GetUpdateCandidates(List<Grid> grids, DateTime now)
        {
            // 如果是首次更新，返回所有网格
            if (_isFirstUpdate)
                return new List<Grid>(grids);

            // 筛选出到期需要更新的网格
            var due = grids.Where(g =>
                        !_lastUpdateTimes.ContainsKey(g) ||
                        (now - _lastUpdateTimes[g]).TotalSeconds >= GridUpdateIntervalSeconds
                      ).ToList();
                      
            // 计算本次更新的网格数量（最少3个，最多1/4）
            int maxCnt = Math.Min(due.Count, grids.Count / 4) + 1;
            int safeMax = maxCnt < 4 ? 3 : maxCnt; // 确保上限不小于下限
            int cnt;
            
            if (due.Count <= 3)
            {
                cnt = due.Count;
            }
            else
            {
                // 随机选择更新数量，防止传入无效范围
                cnt = Random.Shared.Next(3, safeMax);
            }
            
            // 随机排序并取指定数量
            return due.OrderBy(_ => Guid.NewGuid()).Take(cnt).ToList();
        }

        // 应用单显示器壁纸
        private void ApplySingleWallpaper(string path)
        {
            try { DesktopWallpaperApi.SetSingleWallpaper(path); }
            catch { WallpaperSetter.Set(path); } // 回退方法
        }

        // 回退初始化方法
        private void FallbackInitialize()
        {
            if (_wallpaperMode == AWConfig.WallpaperMode.Single)
            {
                // 单显示器模式回退
                _wallpaper = new Image<Rgba32>(Math.Max(1920, _width), Math.Max(1080, _height));
                if (_grids.Count == 0)
                {
                    _grids = CreateGrids(_wallpaper.Width, _wallpaper.Height, _rows, _cols);
                }
            }
            else
            {
                // 多显示器模式回退到主显示器
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
