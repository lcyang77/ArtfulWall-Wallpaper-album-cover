//  ImageManager.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.PixelFormats;

namespace ArtfulWall.Services
{
    public class ImageManager : IDisposable
    {
        // 定义内部类 CacheItem 用于表示缓存中的一个项
        private class CacheItem
        {
            public Image<Rgba32>? Image { get; set; } // 图像对象
            public LinkedListNode<string>? Node { get; set; } // 链表节点，用于实现LRU缓存机制
            public long Size { get; set; } // 图像占用的字节大小
            public DateTime LastAccessTime { get; set; } // 图像最后一次被访问的时间
        }

        // 字典，用于存储图像的缓存
        private readonly ConcurrentDictionary<string, CacheItem> _imageCache;
        // 链表，用于实现最近最少使用（LRU）缓存淘汰策略
        private readonly LinkedList<string> _lruList;
        // 锁对象，用于同步访问 _lruList
        private readonly object _lruLock = new object();
        // 缓存最大容量
        private readonly int _maxCacheSize;
        // 定时器，用于定期清理缓存
        private readonly Timer _cacheCleanupTimer;

        // 构造函数
        public ImageManager(int maxCacheSize = 150, TimeSpan? cacheCleanupInterval = null)
        {
            _imageCache = new ConcurrentDictionary<string, CacheItem>();
            _lruList = new LinkedList<string>();
            _maxCacheSize = maxCacheSize;
            // 初始化定时器，定期调用 CleanupCache 方法来清理缓存
            _cacheCleanupTimer = new Timer(CleanupCache, null, cacheCleanupInterval ?? TimeSpan.FromMinutes(30), cacheCleanupInterval ?? Timeout.InfiniteTimeSpan);
        }

        // 计算图像占用字节大小的方法
        private long CalculateImageSize(Image<Rgba32> image)
        {
            int bytesPerPixel = image.PixelType.BitsPerPixel / 8;
            return image.Width * image.Height * bytesPerPixel;
        }

        // 获取或添加图像的异步方法
        public async Task<Image<Rgba32>?> GetOrAddImageAsync(string path, Size size)
        {
            string key = await GetCacheKeyAsync(path).ConfigureAwait(false);
            // 尝试从缓存中获取图像
            if (_imageCache.TryGetValue(key, out CacheItem cacheItem))
            {
                lock (_lruLock)
                {
                    // 更新链表，将访问的节点移动到链表尾部
                    _lruList.Remove(cacheItem.Node);
                    _lruList.AddLast(cacheItem.Node);
                    cacheItem.LastAccessTime = DateTime.Now;
                }
                return cacheItem.Image;
            }

            // 如果缓存中没有找到，加载新图像
            try
            {
                var image = await LoadAndResizeImageAsync(path, size).ConfigureAwait(false);
                var imageSize = CalculateImageSize(image);

                var node = new LinkedListNode<string>(key);
                cacheItem = new CacheItem
                {
                    Image = image,
                    Node = node,
                    Size = imageSize,
                    LastAccessTime = DateTime.Now
                };

                if (_imageCache.TryAdd(key, cacheItem))
                {
                    lock (_lruLock)
                    {
                        _lruList.AddLast(node);
                        // 如果缓存大小超过限制，则移除最少使用的项
                        if (_imageCache.Count > _maxCacheSize)
                        {
                            RemoveLeastRecentlyUsedItem();
                        }
                    }
                }

                return image;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading image at {path}: {ex.Message}");
                return null;
            }
        }

        // 生成缓存键的异步方法
        private async Task<string> GetCacheKeyAsync(string filePath)
        {
            using (var stream = File.OpenRead(filePath))
            {
                var sha = System.Security.Cryptography.SHA256.Create();
                byte[] checksum = await Task.Run(() => sha.ComputeHash(stream)).ConfigureAwait(false);
                string hash = BitConverter.ToString(checksum).Replace("-", String.Empty);
                long fileSize = new FileInfo(filePath).Length;
                return $"{filePath}_{hash}_{fileSize}";
            }
        }

        // 加载和调整图像大小的异步方法
        private async Task<Image<Rgba32>> LoadAndResizeImageAsync(string path, Size targetSize)
        {
            return await Task.Run(() =>
            {
                using var image = Image.Load<Rgba32>(path);

                // 如果图像不是正方形，先裁剪成正方形
                if (image.Width != image.Height)
                {
                    // 计算需要裁剪的尺寸，取宽和高中的较小值
                    int squareSize = Math.Min(image.Width, image.Height);
                    // 计算裁剪区域的左上角坐标，以确保裁剪区域以图像中心为中心
                    int cropX = (image.Width - squareSize) / 2;
                    int cropY = (image.Height - squareSize) / 2;
                    // 创建裁剪区域
                    var cropRectangle = new Rectangle(cropX, cropY, squareSize, squareSize);
                    // 执行裁剪
                    image.Mutate(x => x.Crop(cropRectangle));
                }

                // 调整图像大小为目标尺寸
                image.Mutate(x => x.Resize(targetSize.Width, targetSize.Height));
                return image.Clone();
            }).ConfigureAwait(false);
        }

        // 移除最少使用的缓存项的方法
        private void RemoveLeastRecentlyUsedItem()
        {
            lock (_lruLock)
            {
                if (_lruList.First != null)
                {
                    var key = _lruList.First.Value;
                    if (_imageCache.TryRemove(key, out CacheItem? cacheItem))
                    {
                        cacheItem.Image?.Dispose(); // 释放图像资源
                        _lruList.RemoveFirst();
                    }
                }
            }
        }

        // 清理缓存的方法
        private void CleanupCache(object state)
        {
            lock (_lruLock)
            {
                int overflowCount = _imageCache.Count - _maxCacheSize;
                if (overflowCount <= 0) return;

                var itemsToRemove = _imageCache.Values
                    .OrderByDescending(item => item.Size) // 先按大小排序
                    .ThenBy(item => item.LastAccessTime) // 再按访问时间排序
                    .Take(overflowCount) // 只移除超出部分
                    .Select(item => item.Node?.Value)
                    .Where(v => v != null)
                    .ToList();

                foreach (var key in itemsToRemove)
                {
                    if (key != null && _imageCache.TryRemove(key, out CacheItem? cacheItem))
                    {
                        cacheItem.Image?.Dispose(); // 释放图像资源
                        _lruList.Remove(cacheItem.Node);
                    }
                }
            }
        }

        // 清空缓存的方法
        public void ClearCache()
        {
            lock (_lruLock)
            {
                foreach (var item in _imageCache.Values)
                {
                    item.Image?.Dispose(); // 释放所有图像资源
                }
                _imageCache.Clear();
                _lruList.Clear();
            }
        }

        // 实现 IDisposable 接口的 Dispose 方法，用于释放资源
        public void Dispose()
        {
            ClearCache(); // 清空缓存
            _cacheCleanupTimer.Dispose(); // 停止并释放定时器资源
        }
    }
} 