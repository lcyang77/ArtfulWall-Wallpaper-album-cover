using System;
using System.IO; // 用于 Path.Combine
using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace ArtfulWall.Models
{
    // 代表应用程序的配置信息
    // Implements IEquatable for proper comparison
    public class Configuration : IEquatable<Configuration>
    {
        // 包含源图片的文件夹路径
        public string? FolderPath { get; set; }

        // 生成的壁纸图片的目标文件夹路径
        // 通常是 FolderPath 下的 "my_wallpaper" 子目录
        public string? DestFolder { get; set; }

        // 生成壁纸的宽度（像素）
        // 在多显示器模式下，此值用作单一壁纸模式的默认值
        public int Width { get; set; } = 1920; // Default width

        // 生成壁纸的高度（像素）
        // 在多显示器模式下，此值用作单一壁纸模式的默认值
        public int Height { get; set; } = 1080; // Default height

        // 壁纸网格的行数
        // 在多显示器模式下，此值用作默认行数
        public int Rows { get; set; } = 1; // Default rows

        // 壁纸网格的列数
        // 在多显示器模式下，此值用作默认列数
        public int Cols { get; set; } = 1; // Default columns

        // 壁纸切换的最小间隔时间（秒）
        public int MinInterval { get; set; } = 3;

        // 壁纸切换的最大间隔时间（秒）
        public int MaxInterval { get; set; } = 10;

        // 壁纸模式：单一壁纸或每显示器壁纸
        public WallpaperMode Mode { get; set; } = WallpaperMode.PerMonitor;

        // 是否监听显示设置变更并自动调整壁纸
        public bool AutoAdjustToDisplayChanges { get; set; } = true;

        // 是否自动适应DPI缩放
        public bool AdaptToDpiScaling { get; set; } = true;

        // 每个显示器的特定配置
        public List<MonitorConfiguration> MonitorConfigurations { get; set; } = new List<MonitorConfiguration>();

        // 壁纸模式枚举
        public enum WallpaperMode
        {
            // 为每个显示器生成独立壁纸
            PerMonitor,
            
            // 使用单一壁纸适配所有显示器
            Single
        }

        // 创建当前配置对象的浅拷贝
        // 返回当前配置对象的副本
        public Configuration Clone()
        {
            var clone = (Configuration)this.MemberwiseClone();
            
            // 深拷贝MonitorConfigurations
            clone.MonitorConfigurations = new List<MonitorConfiguration>();
            foreach (var monConfig in this.MonitorConfigurations)
            {
                clone.MonitorConfigurations.Add(monConfig.Clone());
            }
            
            return clone;
        }

        // 确定指定的 Configuration 对象是否等于当前对象
        // 如果指定的对象等于当前对象，则为 true；否则为 false
        public bool Equals(Configuration? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;

            // 基本属性比较
            bool basicEquals = string.Equals(FolderPath, other.FolderPath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(DestFolder, other.DestFolder, StringComparison.OrdinalIgnoreCase) &&
                   Width == other.Width &&
                   Height == other.Height &&
                   Rows == other.Rows &&
                   Cols == other.Cols &&
                   MinInterval == other.MinInterval &&
                   MaxInterval == other.MaxInterval &&
                   Mode == other.Mode &&
                   AutoAdjustToDisplayChanges == other.AutoAdjustToDisplayChanges &&
                   AdaptToDpiScaling == other.AdaptToDpiScaling;

            if (!basicEquals) return false;

            // 检查MonitorConfigurations
            if (MonitorConfigurations.Count != other.MonitorConfigurations.Count)
                return false;

            for (int i = 0; i < MonitorConfigurations.Count; i++)
            {
                if (!MonitorConfigurations[i].Equals(other.MonitorConfigurations[i]))
                    return false;
            }

            return true;
        }

        // 确定指定的对象是否等于当前对象
        // 如果指定的对象等于当前对象，则为 true；否则为 false
        public override bool Equals(object? obj)
        {
            return Equals(obj as Configuration);
        }

        // 返回此 Configuration 实例的哈希代码
        // 返回32位有符号整数哈希代码
        public override int GetHashCode()
        {
            // 基本属性哈希
            HashCode hash = new HashCode();
            hash.Add(FolderPath?.ToLowerInvariant());
            hash.Add(DestFolder?.ToLowerInvariant());
            hash.Add(Width);
            hash.Add(Height);
            hash.Add(Rows);
            hash.Add(Cols);
            hash.Add(MinInterval);
            hash.Add(MaxInterval);
            hash.Add(Mode);
            hash.Add(AutoAdjustToDisplayChanges);
            hash.Add(AdaptToDpiScaling);
            
            // 添加MonitorConfigurations哈希
            foreach (var config in MonitorConfigurations)
            {
                hash.Add(config);
            }
            
            return hash.ToHashCode();
        }

        // 返回表示当前对象的字符串
        public override string ToString()
        {
            return $"Path: {FolderPath}, Size: {Width}x{Height}, Grid: {Rows}x{Cols}, Interval: {MinInterval}-{MaxInterval}s, Mode: {Mode}";
        }
    }

    // 存储每个显示器的特定配置
    public class MonitorConfiguration : IEquatable<MonitorConfiguration>
    {
        // 显示器ID（设备路径）
        public string MonitorId { get; set; } = "";
        
        // 显示器序号
        public int DisplayNumber { get; set; }
        
        // 显示器特定的宽度
        public int Width { get; set; }
        
        // 显示器特定的高度
        public int Height { get; set; }
        
        // DPI缩放因子
        public float DpiScaling { get; set; } = 1.0f;
        
        // 显示器特定的网格行数
        public int Rows { get; set; } = 1;
        
        // 显示器特定的网格列数
        public int Cols { get; set; } = 1;
        
        // 显示器是否为纵向模式
        public bool IsPortrait { get; set; }

        // 创建当前对象的浅拷贝
        public MonitorConfiguration Clone()
        {
            return (MonitorConfiguration)this.MemberwiseClone();
        }

        // 确定指定的对象是否等于当前对象
        public bool Equals(MonitorConfiguration? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;

            return string.Equals(MonitorId, other.MonitorId) &&
                   DisplayNumber == other.DisplayNumber &&
                   Width == other.Width &&
                   Height == other.Height &&
                   DpiScaling == other.DpiScaling &&
                   Rows == other.Rows &&
                   Cols == other.Cols &&
                   IsPortrait == other.IsPortrait;
        }

        // 确定指定的对象是否等于当前对象
        public override bool Equals(object? obj)
        {
            return Equals(obj as MonitorConfiguration);
        }

        // 返回此实例的哈希代码
        public override int GetHashCode()
        {
            HashCode hash = new HashCode();
            hash.Add(MonitorId);
            hash.Add(DisplayNumber);
            hash.Add(Width);
            hash.Add(Height);
            hash.Add(DpiScaling);
            hash.Add(Rows);
            hash.Add(Cols);
            hash.Add(IsPortrait);
            return hash.ToHashCode();
        }
    }
}
