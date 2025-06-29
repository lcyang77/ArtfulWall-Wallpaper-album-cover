using System;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;
using System.Linq;
using System.Collections.Generic;
using ArtfulWall.Models; // 使用分离的模型
using ArtfulWall.Services; // 用于WallpaperUpdater
using ArtfulWall.Utils; // 用于DisplayManager
using Size = System.Drawing.Size; // System.Drawing.Size的别名

namespace ArtfulWall.UI
{
    // 提供用于编辑应用程序配置的窗体
    public class ConfigEditorForm : Form
    {
        private Configuration originalConfig; // 存储初始配置用于比较
        private Configuration currentConfig;  // 存储正在编辑的配置
        private readonly string configPath;
        private WallpaperUpdater? wallpaperUpdater; // 可选：用于立即应用更改
        private List<DisplayInfo>? displayInfo; // 存储显示器信息

        // UI控件
        private readonly TextBox folderPathTextBox = new TextBox();
        private readonly Button browseButton = new Button { Text = "浏览..." };
        private readonly NumericUpDown widthNumericUpDown = new NumericUpDown { Maximum = 10000, Minimum = 1, Value = 1920 };
        private readonly NumericUpDown heightNumericUpDown = new NumericUpDown { Maximum = 10000, Minimum = 1, Value = 1080 };
        private readonly NumericUpDown rowsNumericUpDown = new NumericUpDown { Maximum = 100, Minimum = 1, Value = 1 };
        private readonly NumericUpDown colsNumericUpDown = new NumericUpDown { Maximum = 100, Minimum = 1, Value = 1 };
        private readonly NumericUpDown minIntervalNumericUpDown = new NumericUpDown { Maximum = 3600, Minimum = 1, Value = 3 };
        private readonly NumericUpDown maxIntervalNumericUpDown = new NumericUpDown { Maximum = 3600, Minimum = 1, Value = 10 };
        private readonly ComboBox wallpaperModeComboBox = new ComboBox(); // 壁纸模式选择
        private readonly CheckBox adaptToDpiCheckBox = new CheckBox { Text = "自动适应DPI缩放", Checked = true }; // DPI调整选项
        private readonly CheckBox autoAdjustDisplayCheckBox = new CheckBox { Text = "显示设置变更时自动调整壁纸", Checked = true }; // 自动调整选项
        private readonly TabControl monitorTabControl = new TabControl(); // 多显示器配置标签页
        private readonly Button confirmButton = new Button { Text = "确认" };
        private readonly Button cancelButton = new Button { Text = "取消" };
        private readonly CheckBox applyWithoutRestartCheckBox = new CheckBox { Text = "保存后立即应用更改（无需重启应用）", Checked = true };

        // 多显示器配置控件集合
        private Dictionary<int, NumericUpDown> monitorRowsControls = new Dictionary<int, NumericUpDown>();
        private Dictionary<int, NumericUpDown> monitorColsControls = new Dictionary<int, NumericUpDown>();

        // 指示配置自加载以来是否已更改并成功保存
        public bool ConfigChanged { get; private set; } = false;

        // 初始化ConfigEditorForm类的新实例
        public ConfigEditorForm(string configFilePath)
        {
            this.configPath = configFilePath;
            // 使用默认或空配置初始化，避免加载前的空值问题
            this.currentConfig = new Configuration();
            this.originalConfig = this.currentConfig.Clone();

            // 获取显示器信息
            try
            {
                displayInfo = DisplayManager.GetDisplays();
            }
            catch
            {
                // 如果无法获取显示器信息，使用空列表
                displayInfo = new List<DisplayInfo>();
            }

            InitializeFormControls();
            LoadConfiguration();
        }

        // 设置WallpaperUpdater实例，用于在保存后立即应用配置更改
        public void SetWallpaperUpdater(WallpaperUpdater updater)
        {
            this.wallpaperUpdater = updater;
        }

        private void InitializeFormControls()
        {
            this.Text = "配置编辑器";
            this.ClientSize = new Size(850, 550); // 增加窗体大小以适应更多控件
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen; // 在父窗体或屏幕中央显示
            this.Padding = new Padding(10);

            var tableLayoutPanel = new TableLayoutPanel
            {
                ColumnCount = 3,
                RowCount = 12, // 增加行数以容纳新控件
                Dock = DockStyle.Fill,
                AutoSize = true,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None, // 更简洁的外观
            };

            // 定义列样式以获得更好的控制
            tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160F)); // 增加标签宽度
            tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));  // 输入控件
            tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));       // 浏览按钮

            // 定义行样式
            for (int i = 0; i < 10; i++) // 增加输入行数
                tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
            tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 220F)); // 多显示器标签页占用更多空间
            tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 35F)); // 应用选项
            tableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 按钮行

            // 添加控件的辅助方法
            void AddControlRow(string labelText, Control control, int rowIndex, bool spanInput = true)
            {
                var label = new Label { Text = labelText, TextAlign = System.Drawing.ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, AutoSize = false };
                tableLayoutPanel.Controls.Add(label, 0, rowIndex);
                control.Dock = DockStyle.Fill;
                tableLayoutPanel.Controls.Add(control, 1, rowIndex);
                if (spanInput)
                {
                    tableLayoutPanel.SetColumnSpan(control, 2); // 如果此行没有特定按钮，则跨越输入和按钮列
                }
            }

            // 封面图片路径
            AddControlRow("封面图片路径:", folderPathTextBox, 0, false); // 不跨列，浏览按钮在下一个位置
            browseButton.Dock = DockStyle.Fill;
            browseButton.Margin = new Padding(3, 0, 0, 0); // 为浏览按钮添加一些边距
            browseButton.Click += BrowseButton_Click;
            tableLayoutPanel.Controls.Add(browseButton, 2, 0);

            // 基本控件
            AddControlRow("宽度 (像素):", widthNumericUpDown, 1);
            AddControlRow("高度 (像素):", heightNumericUpDown, 2);
            AddControlRow("行数:", rowsNumericUpDown, 3);
            AddControlRow("列数:", colsNumericUpDown, 4);
            AddControlRow("最小间隔 (秒):", minIntervalNumericUpDown, 5);
            AddControlRow("最大间隔 (秒):", maxIntervalNumericUpDown, 6);

            // 壁纸模式选择
            wallpaperModeComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            wallpaperModeComboBox.Items.AddRange(new object[] { 
                "每显示器独立壁纸", 
                "单一壁纸" 
            });
            wallpaperModeComboBox.SelectedIndex = 0;
            wallpaperModeComboBox.SelectedIndexChanged += WallpaperMode_Changed;
            AddControlRow("壁纸模式:", wallpaperModeComboBox, 7);

            // DPI和自动调整选项
            AddControlRow("DPI适配:", adaptToDpiCheckBox, 8);
            AddControlRow("显示器变更:", autoAdjustDisplayCheckBox, 9);
            
            // 多显示器配置标签页
            InitializeMonitorTabs();
            AddControlRow("显示器特定设置:", monitorTabControl, 10);

            // 应用选项
            applyWithoutRestartCheckBox.Dock = DockStyle.Fill;
            applyWithoutRestartCheckBox.Padding = new Padding(0, 5, 0, 0);
            tableLayoutPanel.Controls.Add(applyWithoutRestartCheckBox, 0, 11);
            tableLayoutPanel.SetColumnSpan(applyWithoutRestartCheckBox, 3);

            // 按钮面板
            var buttonPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Bottom, // 停靠到窗体底部
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(0, 10, 0, 0) // 顶部填充用于分隔
            };
            confirmButton.Size = new Size(80, 28);
            cancelButton.Size = new Size(80, 28);
            confirmButton.Click += ConfirmButton_Click;
            cancelButton.Click += CancelButton_Click;

            buttonPanel.Controls.Add(cancelButton); // 由于RightToLeft，先添加取消按钮
            buttonPanel.Controls.Add(confirmButton);

            this.Controls.Add(tableLayoutPanel);
            this.Controls.Add(buttonPanel); // 最后添加按钮面板，使其位于底部
        }

        private void InitializeMonitorTabs()
        {
            monitorTabControl.SizeMode = TabSizeMode.FillToRight;
            monitorTabControl.Dock = DockStyle.Fill;
            monitorTabControl.Height = 200;
            monitorRowsControls.Clear();
            monitorColsControls.Clear();

            if (displayInfo == null || displayInfo.Count == 0)
            {
                // 如果没有显示器信息，添加一个说明标签页
                var noDisplayTab = new TabPage("无法检测显示器");
                var infoLabel = new Label
                {
                    Text = "无法检测系统显示器信息，将使用基本配置。",
                    Dock = DockStyle.Fill,
                    TextAlign = System.Drawing.ContentAlignment.MiddleCenter
                };
                noDisplayTab.Controls.Add(infoLabel);
                monitorTabControl.TabPages.Add(noDisplayTab);
                return;
            }

            // 为每个显示器创建标签页
            foreach (var display in displayInfo)
            {
                // 创建显示器标签页
                string orientation = display.Orientation.ToString();
                string title = $"显示器 {display.DisplayNumber + 1}: {display.Width}x{display.Height}";
                if (display.IsPrimary)
                    title += " (主显示器)";
                title += $" - {orientation}";

                var tabPage = new TabPage(title);
                
                // 配置标签页布局
                var tabLayout = new TableLayoutPanel
                {
                    ColumnCount = 2,
                    RowCount = 3,
                    Dock = DockStyle.Fill,
                    Padding = new Padding(10),
                };
                
                tabLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100F));
                tabLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                
                for (int i = 0; i < 3; i++)
                    tabLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
                
                // 显示器信息标签
                var infoLabel = new Label
                {
                    Text = $"DPI缩放: {display.DpiScaling:F2}x   分辨率: {display.Width}x{display.Height}",
                    Dock = DockStyle.Fill,
                    TextAlign = System.Drawing.ContentAlignment.MiddleLeft
                };
                tabLayout.Controls.Add(infoLabel, 0, 0);
                tabLayout.SetColumnSpan(infoLabel, 2);

                // 行数控件
                var rowsLabel = new Label
                {
                    Text = "行数:",
                    Dock = DockStyle.Fill,
                    TextAlign = System.Drawing.ContentAlignment.MiddleLeft
                };
                
                var rowsUpDown = new NumericUpDown
                {
                    Minimum = 1,
                    Maximum = 100,
                    Value = rowsNumericUpDown.Value, // 默认使用全局值
                    Dock = DockStyle.Fill
                };
                
                tabLayout.Controls.Add(rowsLabel, 0, 1);
                tabLayout.Controls.Add(rowsUpDown, 1, 1);
                monitorRowsControls[display.DisplayNumber] = rowsUpDown;
                
                // 列数控件
                var colsLabel = new Label
                {
                    Text = "列数:",
                    Dock = DockStyle.Fill,
                    TextAlign = System.Drawing.ContentAlignment.MiddleLeft
                };
                
                var colsUpDown = new NumericUpDown
                {
                    Minimum = 1,
                    Maximum = 100,
                    Value = colsNumericUpDown.Value, // 默认使用全局值
                    Dock = DockStyle.Fill
                };
                
                tabLayout.Controls.Add(colsLabel, 0, 2);
                tabLayout.Controls.Add(colsUpDown, 1, 2);
                monitorColsControls[display.DisplayNumber] = colsUpDown;
                
                // 添加布局到标签页
                tabPage.Controls.Add(tabLayout);
                monitorTabControl.TabPages.Add(tabPage);
            }
        }

        private void WallpaperMode_Changed(object? sender, EventArgs e)
        {
            // 根据壁纸模式启用/禁用多显示器配置
            bool isPerMonitor = wallpaperModeComboBox.SelectedIndex == 0;
            monitorTabControl.Enabled = isPerMonitor;
            adaptToDpiCheckBox.Enabled = isPerMonitor;
            autoAdjustDisplayCheckBox.Enabled = isPerMonitor;
        }

        private void BrowseButton_Click(object? sender, EventArgs e)
        {
            using (var folderBrowserDialog = new FolderBrowserDialog())
            {
                folderBrowserDialog.Description = "请选择封面图片所在的文件夹";
                folderBrowserDialog.ShowNewFolderButton = true;
                if (!string.IsNullOrWhiteSpace(folderPathTextBox.Text) && Directory.Exists(folderPathTextBox.Text))
                {
                    folderBrowserDialog.SelectedPath = folderPathTextBox.Text;
                }

                if (folderBrowserDialog.ShowDialog(this) == DialogResult.OK)
                {
                    folderPathTextBox.Text = folderBrowserDialog.SelectedPath;
                }
            }
        }

        private void LoadConfiguration()
        {
            try
            {
                if (!File.Exists(configPath))
                {
                    // 配置文件不存在，使用默认设置并允许用户保存新文件
                    MessageBox.Show("配置文件不存在，将使用默认设置。您可以在保存时创建新的配置文件。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    currentConfig = new Configuration(); // 确保是新的默认实例
                    originalConfig = currentConfig.Clone();
                    PopulateUIFromConfig(currentConfig);
                    return;
                }

                var configText = File.ReadAllText(configPath);
                var loadedConfig = JsonSerializer.Deserialize<Configuration>(configText);

                if (loadedConfig == null)
                {
                    MessageBox.Show("配置文件格式不正确或为空。将使用默认设置。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    currentConfig = new Configuration();
                }
                else
                {
                    currentConfig = loadedConfig;
                }

                originalConfig = currentConfig.Clone(); // 存储原始加载配置的副本
                PopulateUIFromConfig(currentConfig);
            }
            catch (JsonException jsonEx)
            {
                MessageBox.Show($"解析配置文件时发生错误：{jsonEx.Message}\n将使用默认设置。", "配置加载错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                currentConfig = new Configuration();
                originalConfig = currentConfig.Clone();
                PopulateUIFromConfig(currentConfig);
            }
            catch (UnauthorizedAccessException uae)
            {
                MessageBox.Show($"无法读取配置文件，请检查文件权限：{uae.Message}\n将使用默认设置。", "权限错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                currentConfig = new Configuration();
                originalConfig = currentConfig.Clone();
                PopulateUIFromConfig(currentConfig);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载配置时发生未知错误：{ex.Message}\n将使用默认设置。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                currentConfig = new Configuration();
                originalConfig = currentConfig.Clone();
                PopulateUIFromConfig(currentConfig);
            }
        }

        private void PopulateUIFromConfig(Configuration configToDisplay)
        {
            // 填充基本设置
            folderPathTextBox.Text = configToDisplay.FolderPath;
            widthNumericUpDown.Value = Math.Max(widthNumericUpDown.Minimum, Math.Min(widthNumericUpDown.Maximum, configToDisplay.Width));
            heightNumericUpDown.Value = Math.Max(heightNumericUpDown.Minimum, Math.Min(heightNumericUpDown.Maximum, configToDisplay.Height));
            rowsNumericUpDown.Value = Math.Max(rowsNumericUpDown.Minimum, Math.Min(rowsNumericUpDown.Maximum, configToDisplay.Rows));
            colsNumericUpDown.Value = Math.Max(colsNumericUpDown.Minimum, Math.Min(colsNumericUpDown.Maximum, configToDisplay.Cols));
            minIntervalNumericUpDown.Value = Math.Max(minIntervalNumericUpDown.Minimum, Math.Min(minIntervalNumericUpDown.Maximum, configToDisplay.MinInterval));
            maxIntervalNumericUpDown.Value = Math.Max(maxIntervalNumericUpDown.Minimum, Math.Min(maxIntervalNumericUpDown.Maximum, configToDisplay.MaxInterval));
            
            // 填充新增设置
            wallpaperModeComboBox.SelectedIndex = configToDisplay.Mode == Configuration.WallpaperMode.PerMonitor ? 0 : 1;
            adaptToDpiCheckBox.Checked = configToDisplay.AdaptToDpiScaling;
            autoAdjustDisplayCheckBox.Checked = configToDisplay.AutoAdjustToDisplayChanges;
            
            // 多显示器设置
            if (displayInfo != null && displayInfo.Count > 0)
            {
                foreach (var display in displayInfo)
                {
                    // 查找此显示器的配置
                    var monitorConfig = configToDisplay.MonitorConfigurations
                        .FirstOrDefault(mc => mc.DisplayNumber == display.DisplayNumber);
                    
                    if (monitorConfig != null)
                    {
                        // 找到配置，填充对应的UI控件
                        if (monitorRowsControls.TryGetValue(display.DisplayNumber, out var rowsControl))
                        {
                            rowsControl.Value = Math.Max(rowsControl.Minimum, 
                                Math.Min(rowsControl.Maximum, monitorConfig.Rows));
                        }
                        
                        if (monitorColsControls.TryGetValue(display.DisplayNumber, out var colsControl))
                        {
                            colsControl.Value = Math.Max(colsControl.Minimum, 
                                Math.Min(colsControl.Maximum, monitorConfig.Cols));
                        }
                    }
                    else
                    {
                        // 没有找到配置，使用全局默认值
                        if (monitorRowsControls.TryGetValue(display.DisplayNumber, out var rowsControl))
                        {
                            rowsControl.Value = configToDisplay.Rows;
                        }
                        
                        if (monitorColsControls.TryGetValue(display.DisplayNumber, out var colsControl))
                        {
                            colsControl.Value = configToDisplay.Cols;
                        }
                    }
                }
            }
            
            // 根据壁纸模式启用/禁用多显示器配置
            bool isPerMonitor = wallpaperModeComboBox.SelectedIndex == 0;
            monitorTabControl.Enabled = isPerMonitor;
            adaptToDpiCheckBox.Enabled = isPerMonitor;
            autoAdjustDisplayCheckBox.Enabled = isPerMonitor;
        }

        private Configuration GetCurrentConfigFromUI()
        {
            var uiConfig = new Configuration
            {
                FolderPath = folderPathTextBox.Text.Trim(),
                Width = (int)widthNumericUpDown.Value,
                Height = (int)heightNumericUpDown.Value,
                Rows = (int)rowsNumericUpDown.Value,
                Cols = (int)colsNumericUpDown.Value,
                MinInterval = (int)minIntervalNumericUpDown.Value,
                MaxInterval = (int)maxIntervalNumericUpDown.Value,
                Mode = wallpaperModeComboBox.SelectedIndex == 0 ? 
                    Configuration.WallpaperMode.PerMonitor : 
                    Configuration.WallpaperMode.Single,
                AdaptToDpiScaling = adaptToDpiCheckBox.Checked,
                AutoAdjustToDisplayChanges = autoAdjustDisplayCheckBox.Checked,
                MonitorConfigurations = new List<MonitorConfiguration>()
            };

            if (!string.IsNullOrWhiteSpace(uiConfig.FolderPath))
            {
                uiConfig.DestFolder = Path.Combine(uiConfig.FolderPath, "my_wallpaper");
            }
            else
            {
                uiConfig.DestFolder = null;
            }
            
            // 添加多显示器配置
            if (displayInfo != null && displayInfo.Count > 0 && uiConfig.Mode == Configuration.WallpaperMode.PerMonitor)
            {
                foreach (var display in displayInfo)
                {
                    if (monitorRowsControls.TryGetValue(display.DisplayNumber, out var rowsControl) &&
                        monitorColsControls.TryGetValue(display.DisplayNumber, out var colsControl))
                    {
                        var monitorConfig = new MonitorConfiguration
                        {
                            DisplayNumber = display.DisplayNumber,
                            MonitorId = display.DeviceName,
                            Width = display.Width,
                            Height = display.Height,
                            DpiScaling = display.DpiScaling,
                            Rows = (int)rowsControl.Value,
                            Cols = (int)colsControl.Value,
                            IsPortrait = display.Orientation == DisplayInfo.OrientationType.Portrait || 
                                         display.Orientation == DisplayInfo.OrientationType.PortraitFlipped
                        };
                        
                        uiConfig.MonitorConfigurations.Add(monitorConfig);
                    }
                }
            }
            
            return uiConfig;
        }

        private bool CheckForConfigurationChanges()
        {
            var configFromUI = GetCurrentConfigFromUI();
            return !configFromUI.Equals(originalConfig);
        }

        private bool ValidateAndSaveChanges()
        {
            var updatedConfig = GetCurrentConfigFromUI();

            if (updatedConfig.MinInterval > updatedConfig.MaxInterval)
            {
                MessageBox.Show("最小间隔时间不能大于最大间隔时间。", "验证错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                minIntervalNumericUpDown.Focus();
                return false;
            }

            if (string.IsNullOrWhiteSpace(updatedConfig.FolderPath))
            {
                MessageBox.Show("封面图片路径不能为空。", "验证错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                folderPathTextBox.Focus();
                return false;
            }

            if (!Directory.Exists(updatedConfig.FolderPath))
            {
                MessageBox.Show($"指定的封面图片路径 \"{updatedConfig.FolderPath}\" 不存在。", "验证错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                folderPathTextBox.Focus();
                return false;
            }

            var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff" };
            bool hasImageFiles = false;
            try
            {
                hasImageFiles = Directory
                   .EnumerateFiles(updatedConfig.FolderPath, "*.*", SearchOption.TopDirectoryOnly)
                   .Any(file =>
                       allowedExtensions.Contains(Path.GetExtension(file)) &&
                       !Path.GetFileName(file).Equals("wallpaper.jpg", StringComparison.OrdinalIgnoreCase)
                   );
            }
            catch (Exception ex)
            {
                MessageBox.Show($"检查图片文件时出错: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            if (!hasImageFiles)
            {
                var result = MessageBox.Show(
                    $"警告：在指定的文件夹 \"{updatedConfig.FolderPath}\" 中似乎未找到任何符合条件的图片文件。\n\n" +
                    "这可能会导致壁纸应用无法正常工作。您确定要继续保存此路径吗？",
                    "未找到图片",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (result == DialogResult.No)
                {
                    folderPathTextBox.Focus();
                    return false;
                }
            }

            if (string.IsNullOrWhiteSpace(updatedConfig.DestFolder))
            {
                updatedConfig.DestFolder = Path.Combine(updatedConfig.FolderPath, "my_wallpaper");
            }

            try
            {
                Directory.CreateDirectory(updatedConfig.DestFolder!);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法创建目标文件夹 \"{updatedConfig.DestFolder}\"：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                var configJson = JsonSerializer.Serialize(updatedConfig, options);
                File.WriteAllText(configPath, configJson);

                currentConfig = updatedConfig;
                originalConfig = currentConfig.Clone();
                ConfigChanged = true;

                return true;
            }
            catch (UnauthorizedAccessException uae)
            {
                MessageBox.Show($"无法保存配置文件，请检查文件权限：{uae.Message}", "权限错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存配置时发生错误：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return false;
        }

        private void ConfirmButton_Click(object? sender, EventArgs e)
        {
            if (CheckForConfigurationChanges())
            {
                if (ValidateAndSaveChanges())
                {
                    MessageBox.Show("配置已成功保存。", "保存成功", MessageBoxButtons.OK, MessageBoxIcon.Information);

                    bool appliedImmediately = false;
                    if (applyWithoutRestartCheckBox.Checked && wallpaperUpdater != null)
                    {
                        try
                        {
                            wallpaperUpdater.UpdateConfig(currentConfig.Clone());
                            appliedImmediately = true;
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"尝试立即应用配置时出错: {ex.Message}\n更改已保存，但可能需要重启应用程序才能完全生效。", "应用错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    }

                    if (applyWithoutRestartCheckBox.Checked && wallpaperUpdater == null && ConfigChanged)
                    {
                        var restartResult = MessageBox.Show(
                            "配置已保存。部分更改可能需要重启应用程序才能生效。\n是否立即重启应用程序？",
                            "重启提示",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question);
                        if (restartResult == DialogResult.Yes)
                        {
                            this.DialogResult = DialogResult.Yes;
                            Application.Restart();
                            Environment.Exit(0);
                            return;
                        }
                        else
                        {
                            this.DialogResult = DialogResult.OK;
                        }
                    }
                    else if (appliedImmediately || !ConfigChanged)
                    {
                        this.DialogResult = DialogResult.OK;
                    }
                    else
                    {
                        this.DialogResult = DialogResult.OK;
                        MessageBox.Show("配置已保存。更改将在下次应用程序启动时生效。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    this.Close();
                }
            }
            else
            {
                MessageBox.Show("未检测到配置变更。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                this.DialogResult = DialogResult.Cancel;
                this.Close();
            }
        }

        private void CancelButton_Click(object? sender, EventArgs e)
        {
            this.DialogResult = DialogResult.Cancel;
            this.Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && this.DialogResult == DialogResult.None && CheckForConfigurationChanges())
            {
                var result = MessageBox.Show("配置已更改但未保存。是否放弃更改？", "未保存的更改", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (result == DialogResult.No)
                {
                    e.Cancel = true;
                }
            }
            base.OnFormClosing(e);
        }
    }
}
