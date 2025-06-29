using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ArtfulWall.Services
{
    public class ImageManager : IDisposable
    {
        // 缓存条目
        private class CacheItem
        {
            public Image<Rgba32> Image { get; set; } = default!;
            public LinkedListNode<string> Node { get; set; } = default!;
            public DateTime LastAccessTime { get; set; }
        }

        private readonly ConcurrentDictionary<string, CacheItem> _imageCache = new();
        private readonly LinkedList<string> _lruList = new();
        private readonly object _lruLock = new object();
        private readonly int _maxCacheItems;
        private readonly Timer _cacheCleanupTimer;

        // 构造 ImageManager 实例，指定最大缓存条目数及清理间隔
        // 若传入 TimeSpan.Zero，则禁用周期性清理，仅在添加新项时驱逐
        public ImageManager(
            int maxCacheItems = 150,
            TimeSpan? cacheCleanupInterval = null)
        {
            _maxCacheItems = maxCacheItems;
            var interval = cacheCleanupInterval ?? TimeSpan.FromMinutes(30);

            if (interval > TimeSpan.Zero)
            {
                _cacheCleanupTimer = new Timer(CleanupCache, null, interval, interval);
            }
            else
            {
                // 禁用周期性清理
                _cacheCleanupTimer = new Timer(_ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }

        // 获取或添加图像。若在缓存中，更新 LRU 并返回克隆；否则加载、缓存并返回新实例
        public async Task<Image<Rgba32>?> GetOrAddImageAsync(string path, Size size)
        {
            string key;
            try
            {
                key = GetCacheKey(path);
            }
            catch
            {
                // 文件不存在或路径无效，返回 null
                return null;
            }

            if (_imageCache.TryGetValue(key, out var existing))
            {
                UpdateLru(existing);
                return existing.Image.Clone();
            }

            try
            {
                var loaded = await LoadAndResizeImageAsync(path, size).ConfigureAwait(false);
                // 只缓存副本，返回原始实例，确保两者互不影响
                var cacheImage = loaded.Clone();
                var node = new LinkedListNode<string>(key);
                var item = new CacheItem
                {
                    Image = cacheImage,
                    Node = node,
                    LastAccessTime = DateTime.UtcNow
                };

                if (_imageCache.TryAdd(key, item))
                {
                    lock (_lruLock)
                    {
                        _lruList.AddLast(node);
                        EvictIfNeeded_NoLock();
                    }
                }

                return loaded;
            }
            catch
            {
                // 加载或处理图像失败，返回 null
                return null;
            }
        }

        private string GetCacheKey(string path)
        {
            var fi = new FileInfo(path);
            if (!fi.Exists)
                throw new FileNotFoundException($"文件不存在: {path}");
            return $"{path}_{fi.Length}_{fi.LastWriteTimeUtc.Ticks}";
        }

        private Task<Image<Rgba32>> LoadAndResizeImageAsync(string path, Size targetSize)
            => Task.Run(() =>
            {
                using var img = Image.Load<Rgba32>(path);
                if (img.Width != img.Height)
                {
                    int square = Math.Min(img.Width, img.Height);
                    int x = (img.Width - square) / 2;
                    int y = (img.Height - square) / 2;
                    img.Mutate(ctx => ctx.Crop(new Rectangle(x, y, square, square)));
                }
                img.Mutate(ctx => ctx.Resize(targetSize.Width, targetSize.Height));
                return img.Clone();
            });

        private void UpdateLru(CacheItem item)
        {
            lock (_lruLock)
            {
                _lruList.Remove(item.Node);
                _lruList.AddLast(item.Node);
                item.LastAccessTime = DateTime.UtcNow;
            }
        }

        private void CleanupCache(object? state)
        {
            lock (_lruLock)
            {
                EvictIfNeeded_NoLock();
            }
        }

        private void EvictIfNeeded_NoLock()
        {
            while (_imageCache.Count > _maxCacheItems)
            {
                var keyToRemove = _lruList.First?.Value;
                if (keyToRemove == null) break;
                if (_imageCache.TryRemove(keyToRemove, out var removed))
                {
                    removed.Image.Dispose();
                    _lruList.RemoveFirst();
                }
                else
                {
                    _lruList.RemoveFirst();
                }
            }
        }

        public void ClearCache()
        {
            lock (_lruLock)
            {
                foreach (var item in _imageCache.Values)
                    item.Image.Dispose();
                _imageCache.Clear();
                _lruList.Clear();
            }
        }

        public void Dispose()
        {
            ClearCache();
            _cacheCleanupTimer.Dispose();
        }
    }
}
