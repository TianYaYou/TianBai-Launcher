using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.System;

namespace TianBai_Launcher;

/// <summary>
/// 启动器主窗口。
/// 负责主页面交互、设置页切换，以及把 Unity StreamingAssets 里的配置文件可视化编辑。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly LauncherConfigPaths _configPaths;
    private bool _settingsAnimationRunning;
    private bool _isStartingGame;
    private bool _suppressEditableChangeTracking;
    private bool _suppressNavigationHandling;
    private bool _suppressLauncherSettingsChange;
    private Process? _gameProcess;
    private LauncherSettings _launcherSettings = LauncherSettings.CreateDefault();
    private readonly DispatcherTimer _unityLogRefreshTimer = new();
    private FileSystemWatcher? _unityLogWatcher;
    private SettingsPage _activeSettingsPage = SettingsPage.Launch;
    private NavigationViewItem? _activeNavigationItem;
    private readonly Dictionary<SettingsPage, string> _editablePageSnapshots = new();
    private readonly List<SettingsSearchEntry> _settingsSearchEntries = new();

    public MainWindow()
    {
        InitializeComponent();
        SetLauncherIcon();

        _launcherSettings = LauncherSettingsService.Load();
        _configPaths = LauncherConfigService.FindPaths(_launcherSettings.GameExecutablePath);
        InitializeSettingsSearch();
        _activeNavigationItem = LaunchSettingsItem;
        SettingsNavigationView.SelectedItem = LaunchSettingsItem;

        LoadLauncherSettingsToUi();
        LoadEditableSettingsPages();
        DetectGamePath();
        UpdateLaunchControls(GameLaunchState.Stopped);
        StartUnityLogRefreshTimer();
    }

    private void SetLauncherIcon()
    {
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Asscess", "Photos", "icon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSettingsOverlay();
    }

    private async void SettingsNavigationView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        await HideSettingsOverlayWithConfirmationAsync();
    }

    private async void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && SettingsOverlay.Visibility == Visibility.Visible)
        {
            await HideSettingsOverlayWithConfirmationAsync();
            e.Handled = true;
        }
    }

    private void DetectButton_Click(object sender, RoutedEventArgs e)
    {
        DetectGamePath();
    }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsGameRunning())
        {
            StopGame();
            return;
        }

        StartGame();
    }

    private void GamePathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateLaunchPathPreview(GamePathTextBox.Text);
        SaveLauncherSettingsFromUi(autoSave: true);
    }

    private void LauncherSettingsControl_Changed(object sender, TextChangedEventArgs e)
    {
        SaveLauncherSettingsFromUi(autoSave: true);
    }

    private void LauncherSettingsNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        SaveLauncherSettingsFromUi(autoSave: true);
        RefreshUnityLogViews();
    }

    private void ShowLogOnMainPageSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        SaveLauncherSettingsFromUi(autoSave: true);
        ApplyMainLogPanelVisibility();
    }

    private void SaveLauncherSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SaveLauncherSettingsFromUi(autoSave: false);
        ShowStatus(InfoBarSeverity.Success, "启动器配置已保存", LauncherSettingsService.SettingsPath);
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateMainLogPanelSize();
    }

    private async void SettingsNavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressNavigationHandling || args.SelectedItem is not NavigationViewItem selectedItem)
        {
            return;
        }

        SettingsPage targetPage = GetPageFromNavigationItem(selectedItem);
        if (targetPage == _activeSettingsPage)
        {
            return;
        }

        if (!await ConfirmLeaveEditablePageAsync())
        {
            RestoreNavigationSelection();
            return;
        }

        ShowSettingsPage(targetPage, selectedItem);
    }

    private async void SaveChangesButton_Click(object sender, RoutedEventArgs e)
    {
        if (await SaveCurrentEditablePageAsync(showSuccessDialog: true))
        {
            SetPageDirty(_activeSettingsPage, false);
        }
    }

    private void DiscardChangesButton_Click(object sender, RoutedEventArgs e)
    {
        DiscardCurrentEditablePage();
    }

    private void EditableTextChanged(object sender, TextChangedEventArgs e) => MarkCurrentEditablePageDirty();
    private void EditableRoutedChanged(object sender, RoutedEventArgs e) => MarkCurrentEditablePageDirty();
    private void EditableSelectionChanged(object sender, SelectionChangedEventArgs e) => MarkCurrentEditablePageDirty();
    private void EditableNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => MarkCurrentEditablePageDirty();

    private void SettingsSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        string query = sender.Text.Trim();
        sender.ItemsSource = string.IsNullOrWhiteSpace(query)
            ? _settingsSearchEntries.Take(8).ToList()
            : _settingsSearchEntries
                .Where(entry => entry.Matches(query))
                .Take(8)
                .ToList();
    }

    private void SettingsSearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is SettingsSearchEntry entry)
        {
            sender.Text = entry.Title;
            NavigateToSettingsSearchEntry(entry);
        }
    }

    private void SettingsSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        SettingsSearchEntry? entry = args.ChosenSuggestion as SettingsSearchEntry
            ?? FindBestSettingsSearchEntry(args.QueryText);
        if (entry != null)
        {
            NavigateToSettingsSearchEntry(entry);
        }
    }

    private void StartGame()
    {
        if (_isStartingGame)
        {
            return;
        }

        _isStartingGame = true;
        UpdateLaunchControls(GameLaunchState.Starting);

        SaveLauncherSettingsFromUi(autoSave: true);
        TianBaiLaunchResult result = TianBaiLaunchService.Launch(new TianBaiLaunchOptions
        {
            ExecutablePath = GamePathTextBox.Text.Trim(),
            ExtraArguments = LaunchArgumentsTextBox.Text.Trim(),
            LogFilePath = _launcherSettings.UnityLogFilePath,
            SourceStreamingAssetsPath = _configPaths.StreamingAssetsPath
        });

        _isStartingGame = false;
        if (!result.Success || result.Process == null)
        {
            _gameProcess = null;
            UpdateLaunchControls(GameLaunchState.Stopped);
            ShowStatus(InfoBarSeverity.Error, "启动失败", result.Message);
            ShowSettingsOverlay();
            return;
        }

        _gameProcess = result.Process;
        WatchGameProcess(_gameProcess);
        UpdateLaunchControls(GameLaunchState.Running);
        ShowStatus(InfoBarSeverity.Success, "启动成功", result.Message);
    }

    private void StopGame()
    {
        Process? process = _gameProcess;
        if (process == null || process.HasExited)
        {
            _gameProcess = null;
            UpdateLaunchControls(GameLaunchState.Stopped);
            return;
        }

        try
        {
            process.CloseMainWindow();
            if (!process.WaitForExit(1500))
            {
                process.Kill(entireProcessTree: true);
            }

            _gameProcess = null;
            UpdateLaunchControls(GameLaunchState.Stopped);
            ShowStatus(InfoBarSeverity.Informational, "已停止游戏", "天白进程已关闭。");
        }
        catch (Exception e)
        {
            ShowStatus(InfoBarSeverity.Error, "停止失败", $"停止天白进程失败：{e.Message}");
        }
    }

    private void WatchGameProcess(Process process)
    {
        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_gameProcess != null && _gameProcess.Id == process.Id)
                    {
                        _gameProcess = null;
                        UpdateLaunchControls(GameLaunchState.Stopped);
                        ShowStatus(InfoBarSeverity.Informational, "游戏已退出", "检测到天白进程已经结束。");
                    }
                });
            };
        }
        catch (Exception e)
        {
            ShowStatus(InfoBarSeverity.Warning, "监听失败", $"启动成功，但进程监听失败：{e.Message}");
        }
    }

    private bool IsGameRunning()
    {
        return _gameProcess != null && !_gameProcess.HasExited;
    }

    private void UpdateLaunchControls(GameLaunchState state)
    {
        switch (state)
        {
            case GameLaunchState.Starting:
                LaunchButton.IsEnabled = false;
                LaunchButtonIcon.Glyph = "\uE768";
                LaunchButtonTextBlock.Text = "启动中";
                LaunchProgressBar.Visibility = Visibility.Visible;
                break;

            case GameLaunchState.Running:
                LaunchButton.IsEnabled = true;
                LaunchButtonIcon.Glyph = "\uE71A";
                LaunchButtonTextBlock.Text = "停止游戏";
                LaunchProgressBar.Visibility = Visibility.Collapsed;
                break;

            default:
                LaunchButton.IsEnabled = true;
                LaunchButtonIcon.Glyph = "\uE768";
                LaunchButtonTextBlock.Text = "开始游戏";
                LaunchProgressBar.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void DetectGamePath()
    {
        if (!string.IsNullOrWhiteSpace(GamePathTextBox.Text) && File.Exists(GamePathTextBox.Text.Trim()))
        {
            UpdateLaunchPathPreview(GamePathTextBox.Text);
            return;
        }

        string? path = TianBaiLaunchService.FindDefaultExecutable();
        if (string.IsNullOrWhiteSpace(path))
        {
            UpdateLaunchPathPreview("");
            ShowStatus(
                InfoBarSeverity.Warning,
                "没有找到天白程序",
                "请确认大仓库 Release 目录里存在 Tian Bai Aic.exe，或者在设置里手动填写 exe 路径。");
            return;
        }

        GamePathTextBox.Text = path;
        UpdateLaunchPathPreview(path);
        SaveLauncherSettingsFromUi(autoSave: true);
        ShowStatus(InfoBarSeverity.Informational, "已检测到天白程序", path);
    }

    private void ShowSettingsOverlay()
    {
        if (_settingsAnimationRunning)
        {
            return;
        }

        SettingsOverlay.Visibility = Visibility.Visible;
        SettingsOverlay.IsHitTestVisible = true;
        RootGrid.Focus(FocusState.Programmatic);
        SettingsNavigationView.SelectedItem ??= LaunchSettingsItem;

        double width = GetOverlayAnimationWidth();
        SettingsOverlayTransform.X = width;
        SettingsOverlay.Opacity = 0;
        StartSettingsOverlayAnimation(width, 0, 0, 1, collapseWhenDone: false);
    }

    private async Task HideSettingsOverlayWithConfirmationAsync()
    {
        if (!await ConfirmLeaveEditablePageAsync())
        {
            return;
        }

        HideSettingsOverlay();
    }

    private void HideSettingsOverlay()
    {
        if (_settingsAnimationRunning || SettingsOverlay.Visibility != Visibility.Visible)
        {
            return;
        }

        SettingsOverlay.IsHitTestVisible = false;
        StartSettingsOverlayAnimation(0, GetOverlayAnimationWidth(), 1, 0, collapseWhenDone: true);
    }

    private void StartSettingsOverlayAnimation(double fromX, double toX, double fromOpacity, double toOpacity, bool collapseWhenDone)
    {
        _settingsAnimationRunning = true;
        SettingsOverlayTransform.X = fromX;
        SettingsOverlay.Opacity = fromOpacity;

        var slideAnimation = CreateSystemSplineAnimation(fromX, toX, TimeSpan.FromMilliseconds(280));
        Storyboard.SetTarget(slideAnimation, SettingsOverlayTransform);
        Storyboard.SetTargetProperty(slideAnimation, nameof(SettingsOverlayTransform.X));

        var opacityAnimation = CreateSystemSplineAnimation(fromOpacity, toOpacity, TimeSpan.FromMilliseconds(180));
        Storyboard.SetTarget(opacityAnimation, SettingsOverlay);
        Storyboard.SetTargetProperty(opacityAnimation, nameof(SettingsOverlay.Opacity));

        var storyboard = new Storyboard();
        storyboard.Children.Add(slideAnimation);
        storyboard.Children.Add(opacityAnimation);
        storyboard.Completed += (_, _) =>
        {
            SettingsOverlayTransform.X = toX;
            SettingsOverlay.Opacity = toOpacity;

            if (collapseWhenDone)
            {
                SettingsOverlay.Visibility = Visibility.Collapsed;
            }

            SettingsOverlay.IsHitTestVisible = SettingsOverlay.Visibility == Visibility.Visible;
            _settingsAnimationRunning = false;
        };

        storyboard.Begin();
    }

    private static DoubleAnimationUsingKeyFrames CreateSystemSplineAnimation(double from, double to, TimeSpan duration)
    {
        // WinUI 常用的 fast-out-slow-in 曲线：进入快，停下柔和，比普通 EaseOut 更接近系统动效。
        var animation = new DoubleAnimationUsingKeyFrames
        {
            EnableDependentAnimation = true
        };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = from
        });
        animation.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(duration),
            Value = to,
            KeySpline = new KeySpline
            {
                ControlPoint1 = new Point(0.1, 0.9),
                ControlPoint2 = new Point(0.2, 1.0)
            }
        });

        return animation;
    }

    private double GetOverlayAnimationWidth() => Math.Max(420, RootGrid.ActualWidth);

    private void ShowSettingsPage(SettingsPage page, NavigationViewItem item)
    {
        _activeSettingsPage = page;
        _activeNavigationItem = item;

        LaunchSettingsPanel.Visibility = page == SettingsPage.Launch ? Visibility.Visible : Visibility.Collapsed;
        DownloadSettingsPanel.Visibility = page == SettingsPage.Download ? Visibility.Visible : Visibility.Collapsed;
        ChatSettingsPanel.Visibility = page == SettingsPage.Chat ? Visibility.Visible : Visibility.Collapsed;
        WhisperSettingsPanel.Visibility = page == SettingsPage.Whisper ? Visibility.Visible : Visibility.Collapsed;
        TtsSettingsPanel.Visibility = page == SettingsPage.Tts ? Visibility.Visible : Visibility.Collapsed;
        MemorySettingsPanel.Visibility = page == SettingsPage.Memory ? Visibility.Visible : Visibility.Collapsed;
        AboutSettingsPanel.Visibility = page == SettingsPage.About ? Visibility.Visible : Visibility.Collapsed;
        ExtraModuleSettingsPanel.Visibility = page == SettingsPage.ExtraModule ? Visibility.Visible : Visibility.Collapsed;
        EditableActionBar.Visibility = IsEditablePage(page) ? Visibility.Visible : Visibility.Collapsed;

        (SettingsTitleTextBlock.Text, SettingsDescriptionTextBlock.Text) = page switch
        {
            SettingsPage.Launch => ("启动设置", "配置天白 Unity 程序的启动路径。"),
            SettingsPage.Download => ("下载", "下载与更新之后再开发，先保留入口。"),
            SettingsPage.Chat => ("对话", "编辑 AI 对话配置和 A/B 通道使用的 prompt 文件。"),
            SettingsPage.Whisper => ("Wisper", "编辑语音唤醒和识别参数，之后 Unity 侧可读取同一份配置。"),
            SettingsPage.Tts => ("TTS", "配置远端 API 或本地 sherpa-onnx 的语音合成参数。"),
            SettingsPage.Memory => ("记忆", "编辑私有记忆目录、长期记忆文件和好感度数据。"),
            SettingsPage.About => ("关于", "启动器与当前配置路径信息。"),
            SettingsPage.ExtraModule => ("额外模块", "额外模块之后再开发，先保留入口。"),
            _ => ("设置", "")
        };

        AnimateSettingsPanelIn(GetSettingsPanel(page));
    }

    private void InitializeSettingsSearch()
    {
        _settingsSearchEntries.Clear();
        _settingsSearchEntries.AddRange(new[]
        {
            new SettingsSearchEntry("启动", SettingsPage.Launch, "开始 游戏 路径 exe release 启动项 参数 日志 log debug"),
            new SettingsSearchEntry("下载", SettingsPage.Download, "更新 补丁 模型 下载"),
            new SettingsSearchEntry("对话", SettingsPage.Chat, "AI API chat prompt system_prompt dialogue control 模型 key"),
            new SettingsSearchEntry("对话 Prompt", SettingsPage.Chat, "dialogue_prompt control_planner_prompt system_prompt"),
            new SettingsSearchEntry("Wisper", SettingsPage.Whisper, "Whisper 语音 唤醒词 麦克风 模型 静音"),
            new SettingsSearchEntry("TTS", SettingsPage.Tts, "语音 合成 mimo sherpa onnx 本地 远端 voice"),
            new SettingsSearchEntry("记忆", SettingsPage.Memory, "Memory 好感度 长期记忆 jsonl favorability"),
            new SettingsSearchEntry("关于", SettingsPage.About, "版本 路径 StreamingAssets"),
            new SettingsSearchEntry("额外模块", SettingsPage.ExtraModule, "插件 mod tool 工具 模块")
        });

        SettingsSearchBox.ItemsSource = _settingsSearchEntries.Take(8).ToList();
    }

    private SettingsSearchEntry? FindBestSettingsSearchEntry(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        return _settingsSearchEntries.FirstOrDefault(entry => string.Equals(entry.Title, query.Trim(), StringComparison.CurrentCultureIgnoreCase))
               ?? _settingsSearchEntries.FirstOrDefault(entry => entry.Matches(query));
    }

    private void NavigateToSettingsSearchEntry(SettingsSearchEntry entry)
    {
        NavigationViewItem? item = GetNavigationItemFromPage(entry.Page);
        if (item == null)
        {
            return;
        }

        SettingsNavigationView.SelectedItem = item;
        if (entry.Page == _activeSettingsPage)
        {
            ShowSettingsPage(entry.Page, item);
        }
    }

    private NavigationViewItem? GetNavigationItemFromPage(SettingsPage page)
    {
        return page switch
        {
            SettingsPage.Launch => LaunchSettingsItem,
            SettingsPage.Download => DownloadSettingsItem,
            SettingsPage.Chat => ChatSettingsItem,
            SettingsPage.Whisper => WhisperSettingsItem,
            SettingsPage.Tts => TtsSettingsItem,
            SettingsPage.Memory => MemorySettingsItem,
            SettingsPage.About => AboutSettingsItem,
            SettingsPage.ExtraModule => ExtraModuleSettingsItem,
            _ => null
        };
    }

    private FrameworkElement GetSettingsPanel(SettingsPage page)
    {
        return page switch
        {
            SettingsPage.Launch => LaunchSettingsPanel,
            SettingsPage.Download => DownloadSettingsPanel,
            SettingsPage.Chat => ChatSettingsPanel,
            SettingsPage.Whisper => WhisperSettingsPanel,
            SettingsPage.Tts => TtsSettingsPanel,
            SettingsPage.Memory => MemorySettingsPanel,
            SettingsPage.About => AboutSettingsPanel,
            SettingsPage.ExtraModule => ExtraModuleSettingsPanel,
            _ => LaunchSettingsPanel
        };
    }

    private void AnimateSettingsPanelIn(FrameworkElement panel)
    {
        // 栏目切换时只让新内容进入，避免 NavigationView 菜单本身跟着抖动。
        var transform = new TranslateTransform { Y = 18 };
        panel.RenderTransform = transform;
        panel.Opacity = 0;

        var storyboard = new Storyboard();
        var slideAnimation = CreateSystemSplineAnimation(18, 0, TimeSpan.FromMilliseconds(220));
        Storyboard.SetTarget(slideAnimation, transform);
        Storyboard.SetTargetProperty(slideAnimation, nameof(TranslateTransform.Y));

        var opacityAnimation = CreateSystemSplineAnimation(0, 1, TimeSpan.FromMilliseconds(160));
        Storyboard.SetTarget(opacityAnimation, panel);
        Storyboard.SetTargetProperty(opacityAnimation, nameof(UIElement.Opacity));

        storyboard.Children.Add(slideAnimation);
        storyboard.Children.Add(opacityAnimation);
        storyboard.Begin();
    }

    private void LoadEditableSettingsPages()
    {
        _suppressEditableChangeTracking = true;
        try
        {
            LoadChatSettings();
            LoadWhisperSettings();
            LoadTtsSettings();
            LoadMemorySettings();
            ConfigRootTextBlock.Text = $"StreamingAssets：{_configPaths.StreamingAssetsPath}";
        }
        finally
        {
            _suppressEditableChangeTracking = false;
        }

        SetPageDirty(SettingsPage.Chat, false);
        SetPageDirty(SettingsPage.Whisper, false);
        SetPageDirty(SettingsPage.Tts, false);
        SetPageDirty(SettingsPage.Memory, false);
        CaptureEditablePageSnapshots();
    }

    private void LoadLauncherSettingsToUi()
    {
        _suppressLauncherSettingsChange = true;
        try
        {
            GamePathTextBox.Text = _launcherSettings.GameExecutablePath;
            LaunchArgumentsTextBox.Text = _launcherSettings.ExtraLaunchArguments;
            LaunchLogLineCountNumberBox.Value = _launcherSettings.LogLineCount;
            ShowLogOnMainPageSwitch.IsOn = _launcherSettings.ShowLogOnMainPage;
            UnityLogPathTextBlock.Text = $"Unity 日志文件：{_launcherSettings.UnityLogFilePath}";
            UpdateLaunchPathPreview(_launcherSettings.GameExecutablePath);
            ApplyMainLogPanelVisibility();
            UpdateMainLogPanelSize();
        }
        finally
        {
            _suppressLauncherSettingsChange = false;
        }
    }

    private void SaveLauncherSettingsFromUi(bool autoSave)
    {
        if (_suppressLauncherSettingsChange)
        {
            return;
        }

        _launcherSettings.GameExecutablePath = GamePathTextBox.Text.Trim();
        _launcherSettings.ExtraLaunchArguments = LaunchArgumentsTextBox.Text.Trim();
        _launcherSettings.LogLineCount = (int)Math.Clamp(NumberOr(LaunchLogLineCountNumberBox.Value, 120), 10, 2000);
        _launcherSettings.ShowLogOnMainPage = ShowLogOnMainPageSwitch.IsOn;
        _launcherSettings.EnsureDefaults();
        LauncherSettingsService.Save(_launcherSettings);

        UnityLogPathTextBlock.Text = $"Unity 日志文件：{_launcherSettings.UnityLogFilePath}";
        if (!autoSave)
        {
            RefreshUnityLogViews();
        }
    }

    private void StartUnityLogRefreshTimer()
    {
        ConfigureUnityLogWatcher();
        _unityLogRefreshTimer.Interval = TimeSpan.FromSeconds(1);
        _unityLogRefreshTimer.Tick += (_, _) => RefreshUnityLogViews();
        _unityLogRefreshTimer.Start();
        RefreshUnityLogViews();
    }

    private void ConfigureUnityLogWatcher()
    {
        _unityLogWatcher?.Dispose();
        _unityLogWatcher = null;

        string logPath = _launcherSettings.UnityLogFilePath;
        string? directory = Path.GetDirectoryName(logPath);
        string fileName = Path.GetFileName(logPath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        _unityLogWatcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        // Unity 写入日志时会触发 Changed/Created；通过 DispatcherQueue 切回 UI 线程刷新日志窗口。
        _unityLogWatcher.Changed += (_, _) => QueueUnityLogRefresh();
        _unityLogWatcher.Created += (_, _) => QueueUnityLogRefresh();
        _unityLogWatcher.Renamed += (_, _) => QueueUnityLogRefresh();
    }

    private void QueueUnityLogRefresh()
    {
        DispatcherQueue.TryEnqueue(RefreshUnityLogViews);
    }

    private void RefreshUnityLogViews()
    {
        int lineCount = (int)Math.Clamp(NumberOr(LaunchLogLineCountNumberBox.Value, _launcherSettings.LogLineCount), 10, 2000);
        string logText = UnityLogReader.ReadLastLines(_launcherSettings.UnityLogFilePath, lineCount);
        bool shouldScrollLaunchLog = IsScrollViewerNearBottom(LaunchLogScrollViewer);
        bool shouldScrollMainLog = IsScrollViewerNearBottom(MainLogScrollViewer);

        LaunchLogTextBlock.Text = logText;
        MainLogTextBlock.Text = logText;
        ScrollLogsToEnd(shouldScrollLaunchLog, shouldScrollMainLog);
    }

    private static bool IsScrollViewerNearBottom(ScrollViewer scrollViewer)
    {
        // 用户正在翻历史时不自动拉到底；只有已经接近底部时才跟随新日志。
        const double bottomTolerance = 8;
        return scrollViewer.ScrollableHeight <= bottomTolerance
               || scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - bottomTolerance;
    }

    private void ScrollLogsToEnd(bool launchLog, bool mainLog)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (launchLog)
            {
                LaunchLogScrollViewer.ChangeView(null, LaunchLogScrollViewer.ScrollableHeight, null, disableAnimation: true);
            }

            if (mainLog)
            {
                MainLogScrollViewer.ChangeView(null, MainLogScrollViewer.ScrollableHeight, null, disableAnimation: true);
            }
        });
    }

    private void ApplyMainLogPanelVisibility()
    {
        MainLogPanel.Visibility = ShowLogOnMainPageSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
        UpdateMainLogPanelSize();
    }

    private void UpdateMainLogPanelSize()
    {
        if (MainLogPanel == null || RootGrid == null)
        {
            return;
        }

        double availableHeight = Math.Max(160, RootGrid.ActualHeight - 152);
        MainLogPanel.Height = Math.Min(availableHeight, Math.Max(160, RootGrid.ActualHeight * 0.48));
    }

    private void LoadChatSettings()
    {
        LauncherConfigService.TryReadJsonObject(_configPaths.AiConfigPath, out JsonObject config, out string? error);
        if (error != null)
        {
            ShowStatus(InfoBarSeverity.Warning, "对话配置读取失败", error);
        }

        ChatEnabledSwitch.IsOn = LauncherConfigService.GetBool(config, "Enabled", false);
        ChatNameTextBox.Text = LauncherConfigService.GetString(config, "Name", "Launcher_AI");
        ChatApiHostTextBox.Text = LauncherConfigService.GetString(config, "ApiHost", "https://api.openai.com/v1");
        ChatApiPathTextBox.Text = LauncherConfigService.GetString(config, "ApiPath", "/chat/completions");
        ChatApiKeyPasswordBox.Password = LauncherConfigService.GetString(config, "ApiKey");
        ChatModelTextBox.Text = LauncherConfigService.GetString(config, "DefaultModel", "gpt-4o-mini");
        ChatModelListTextBox.Text = string.Join(", ", GetStringArray(config, "ModelList", new[] { ChatModelTextBox.Text }));
        ChatTemperatureNumberBox.Value = LauncherConfigService.GetDouble(config, "DefaultTemperature", 0.7);
        ChatStreamSwitch.IsOn = LauncherConfigService.GetBool(config, "DefaultStream", false);
        ChatPassHistorySwitch.IsOn = LauncherConfigService.GetBool(config, "DefaultPassHistory", true);
        ChatUsePromptFileSwitch.IsOn = LauncherConfigService.GetBool(config, "UseSystemPromptFile", true);
        ChatSystemPromptFileTextBox.Text = LauncherConfigService.GetString(config, "SystemPromptFile", "AI/system_prompt.txt");
        ChatLegacyJsonSwitch.IsOn = LauncherConfigService.GetBool(config, "UseLegacyJsonResponse", false);
        ChatUserDisplayNameTextBox.Text = LauncherConfigService.GetString(config, "UserDisplayName", "Ink_bai");
        ChatDefaultSystemPromptTextBox.Text = LauncherConfigService.GetString(config, "DefaultSystemPrompt", "You are TianBai, a gentle desktop companion. Reply briefly and warmly.");
        DialoguePromptTextBox.Text = LauncherConfigService.ReadTextOrEmpty(_configPaths.DialoguePromptPath);
        ControlPlannerPromptTextBox.Text = LauncherConfigService.ReadTextOrEmpty(_configPaths.ControlPlannerPromptPath);
    }

    private void LoadWhisperSettings()
    {
        LauncherConfigService.TryReadJsonObject(_configPaths.WhisperConfigPath, out JsonObject config, out _);

        WhisperEnabledSwitch.IsOn = LauncherConfigService.GetBool(config, "Enabled", true);
        WhisperWakeWordTextBox.Text = LauncherConfigService.GetString(config, "WakeWord", "天白");
        WhisperModelFileTextBox.Text = LauncherConfigService.GetString(config, "ModelFile", "Whisper/ggml-tiny.bin");
        WhisperWakeSilenceNumberBox.Value = LauncherConfigService.GetDouble(config, "WakeWordSilenceSeconds", 0.6);
        WhisperCommandSilenceNumberBox.Value = LauncherConfigService.GetDouble(config, "CommandSilenceSeconds", 2.0);
        WhisperSubmitTailSwitch.IsOn = LauncherConfigService.GetBool(config, "SubmitCommandInWakePhrase", true);
        WhisperRestartDelayNumberBox.Value = LauncherConfigService.GetDouble(config, "RestartWakeListenDelaySeconds", 0.2);
    }

    private void LoadTtsSettings()
    {
        bool parsed = LauncherConfigService.TryReadJsonObject(_configPaths.TtsConfigPath, out JsonObject config, out string? error);
        string raw = LauncherConfigService.ReadTextOrEmpty(_configPaths.TtsConfigPath);

        TtsParseWarningInfoBar.IsOpen = !parsed && File.Exists(_configPaths.TtsConfigPath);
        TtsParseWarningInfoBar.Message = error ?? string.Empty;

        TtsEnabledSwitch.IsOn = parsed ? LauncherConfigService.GetBool(config, "Enabled", false) : LauncherConfigService.ExtractBoolFromPossiblyBrokenJson(raw, "Enabled", false);
        TtsNameTextBox.Text = parsed ? LauncherConfigService.GetString(config, "Name", "Launcher_TTS") : LauncherConfigService.ExtractStringFromPossiblyBrokenJson(raw, "Name", "Launcher_TTS");
        SelectComboBoxByTag(TtsModeComboBox, (parsed ? LauncherConfigService.GetInt(config, "Mode", 0) : LauncherConfigService.ExtractIntFromPossiblyBrokenJson(raw, "Mode", 0)).ToString(CultureInfo.InvariantCulture));
        TtsApiKeyPasswordBox.Password = parsed ? LauncherConfigService.GetString(config, "ApiKey") : LauncherConfigService.ExtractStringFromPossiblyBrokenJson(raw, "ApiKey");
        TtsApiHostTextBox.Text = parsed ? LauncherConfigService.GetString(config, "ApiHost", "https://token-plan-cn.xiaomimimo.com/v1") : LauncherConfigService.ExtractStringFromPossiblyBrokenJson(raw, "ApiHost", "https://token-plan-cn.xiaomimimo.com/v1");
        TtsApiPathTextBox.Text = parsed ? LauncherConfigService.GetString(config, "ApiPath", "/chat/completions") : LauncherConfigService.ExtractStringFromPossiblyBrokenJson(raw, "ApiPath", "/chat/completions");
        TtsModelTextBox.Text = parsed ? LauncherConfigService.GetString(config, "Model", "mimo-v2.5-tts-voicedesign") : LauncherConfigService.ExtractStringFromPossiblyBrokenJson(raw, "Model", "mimo-v2.5-tts-voicedesign");
        TtsAudioFormatTextBox.Text = parsed ? LauncherConfigService.GetString(config, "AudioFormat", "wav") : LauncherConfigService.ExtractStringFromPossiblyBrokenJson(raw, "AudioFormat", "wav");
        TtsFallbackSampleRateNumberBox.Value = parsed ? LauncherConfigService.GetInt(config, "FallbackSampleRate", 24000) : LauncherConfigService.ExtractIntFromPossiblyBrokenJson(raw, "FallbackSampleRate", 24000);
        TtsFallbackChannelsNumberBox.Value = parsed ? LauncherConfigService.GetInt(config, "FallbackChannels", 1) : LauncherConfigService.ExtractIntFromPossiblyBrokenJson(raw, "FallbackChannels", 1);
        TtsBaseVoiceDescriptionTextBox.Text = parsed ? LauncherConfigService.GetString(config, "BaseVoiceDescription", "一个温柔、清澈、略带亲近感的少女声音，语速自然。") : LauncherConfigService.ExtractStringFromPossiblyBrokenJson(raw, "BaseVoiceDescription", "一个温柔、清澈、略带亲近感的少女声音，语速自然。");
        TtsDefaultEmotionTextBox.Text = parsed ? LauncherConfigService.GetString(config, "DefaultEmotion", "自然") : LauncherConfigService.ExtractStringFromPossiblyBrokenJson(raw, "DefaultEmotion", "自然");
        TtsAppendEmotionSwitch.IsOn = parsed ? LauncherConfigService.GetBool(config, "AppendEmotionToDescription", true) : LauncherConfigService.ExtractBoolFromPossiblyBrokenJson(raw, "AppendEmotionToDescription", true);
        TtsSherpaModelRootTextBox.Text = parsed ? LauncherConfigService.GetString(config, "SherpaModelRoot", "sherpa-onnx/models/speech-synthesis/vits-melo-tts-zh_en") : LauncherConfigService.ExtractStringFromPossiblyBrokenJson(raw, "SherpaModelRoot", "sherpa-onnx/models/speech-synthesis/vits-melo-tts-zh_en");
        TtsSherpaModelFileTextBox.Text = parsed ? LauncherConfigService.GetString(config, "SherpaModelFile", "model.onnx") : LauncherConfigService.ExtractStringFromPossiblyBrokenJson(raw, "SherpaModelFile", "model.onnx");
        TtsSherpaTokensFileTextBox.Text = parsed ? LauncherConfigService.GetString(config, "SherpaTokensFile", "tokens.txt") : LauncherConfigService.ExtractStringFromPossiblyBrokenJson(raw, "SherpaTokensFile", "tokens.txt");
        TtsSherpaLexiconFileTextBox.Text = parsed ? LauncherConfigService.GetString(config, "SherpaLexiconFile", "lexicon.txt") : LauncherConfigService.ExtractStringFromPossiblyBrokenJson(raw, "SherpaLexiconFile", "lexicon.txt");
        TtsSherpaDictDirTextBox.Text = parsed ? LauncherConfigService.GetString(config, "SherpaDictDir", "dict") : LauncherConfigService.ExtractStringFromPossiblyBrokenJson(raw, "SherpaDictDir", "dict");
        TtsSherpaRuleFstsTextBox.Text = string.Join(", ", parsed ? GetStringArray(config, "SherpaRuleFsts", DefaultSherpaRuleFsts()) : LauncherConfigService.ExtractStringArrayFromPossiblyBrokenJson(raw, "SherpaRuleFsts", DefaultSherpaRuleFsts()));
        TtsSherpaProviderTextBox.Text = parsed ? LauncherConfigService.GetString(config, "SherpaProvider", "cpu") : LauncherConfigService.ExtractStringFromPossiblyBrokenJson(raw, "SherpaProvider", "cpu");
        TtsSherpaThreadsNumberBox.Value = parsed ? LauncherConfigService.GetInt(config, "SherpaNumThreads", 2) : LauncherConfigService.ExtractIntFromPossiblyBrokenJson(raw, "SherpaNumThreads", 2);
        TtsSherpaDebugSwitch.IsOn = parsed ? LauncherConfigService.GetBool(config, "SherpaDebug", false) : LauncherConfigService.ExtractBoolFromPossiblyBrokenJson(raw, "SherpaDebug", false);
        TtsSherpaSpeakerNumberBox.Value = parsed ? LauncherConfigService.GetInt(config, "SherpaSpeakerId", 0) : LauncherConfigService.ExtractIntFromPossiblyBrokenJson(raw, "SherpaSpeakerId", 0);
        TtsSherpaSpeedNumberBox.Value = parsed ? LauncherConfigService.GetDouble(config, "SherpaSpeed", 1) : LauncherConfigService.ExtractDoubleFromPossiblyBrokenJson(raw, "SherpaSpeed", 1);
        TtsSherpaMaxSentencesNumberBox.Value = parsed ? LauncherConfigService.GetInt(config, "SherpaMaxNumSentences", 2) : LauncherConfigService.ExtractIntFromPossiblyBrokenJson(raw, "SherpaMaxNumSentences", 2);
        TtsSherpaSilenceScaleNumberBox.Value = parsed ? LauncherConfigService.GetDouble(config, "SherpaSilenceScale", 0.2) : LauncherConfigService.ExtractDoubleFromPossiblyBrokenJson(raw, "SherpaSilenceScale", 0.2);
        TtsSherpaNoiseScaleNumberBox.Value = parsed ? LauncherConfigService.GetDouble(config, "SherpaNoiseScale", 0.667) : LauncherConfigService.ExtractDoubleFromPossiblyBrokenJson(raw, "SherpaNoiseScale", 0.667);
        TtsSherpaNoiseScaleWNumberBox.Value = parsed ? LauncherConfigService.GetDouble(config, "SherpaNoiseScaleW", 0.8) : LauncherConfigService.ExtractDoubleFromPossiblyBrokenJson(raw, "SherpaNoiseScaleW", 0.8);
        TtsSherpaLengthScaleNumberBox.Value = parsed ? LauncherConfigService.GetDouble(config, "SherpaLengthScale", 1) : LauncherConfigService.ExtractDoubleFromPossiblyBrokenJson(raw, "SherpaLengthScale", 1);
    }

    private void LoadMemorySettings()
    {
        LauncherConfigService.TryReadJsonObject(_configPaths.FavorabilityPath, out JsonObject favorability, out _);

        MemoryDirectoryTextBox.Text = _configPaths.MemoryDirectory;
        MemoryFileTextBox.Text = _configPaths.MemoryJsonlPath;
        FavorabilityFileTextBox.Text = _configPaths.FavorabilityPath;
        FavorabilityValueNumberBox.Value = LauncherConfigService.GetDouble(favorability, "value", 0);
        FavorabilitySourceTextBox.Text = LauncherConfigService.GetString(favorability, "source", "launcher");
        MemoryPreviewTextBox.Text = BuildMemoryPreview();
    }

    private async Task<bool> ConfirmLeaveEditablePageAsync()
    {
        if (!IsPageDirty(_activeSettingsPage))
        {
            return true;
        }

        ContentDialog dialog = CreateDialog(
            "有未保存的更改",
            "当前设置页有未保存的修改，要先保存吗？",
            primaryText: "保存更改",
            secondaryText: "放弃保存",
            closeText: "继续编辑");

        ContentDialogResult result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            return await SaveCurrentEditablePageAsync(showSuccessDialog: false);
        }

        if (result == ContentDialogResult.Secondary)
        {
            DiscardCurrentEditablePage();
            return true;
        }

        return false;
    }

    private async Task<bool> SaveCurrentEditablePageAsync(bool showSuccessDialog)
    {
        try
        {
            switch (_activeSettingsPage)
            {
                case SettingsPage.Chat:
                    SaveChatSettings();
                    break;
                case SettingsPage.Whisper:
                    SaveWhisperSettings();
                    break;
                case SettingsPage.Tts:
                    SaveTtsSettings();
                    break;
                case SettingsPage.Memory:
                    SaveMemorySettings();
                    break;
                default:
                    return true;
            }
        }
        catch (SettingsValidationException e)
        {
            await CreateDialog("设置检查没有通过", e.Message, closeText: "我知道了").ShowAsync();
            return false;
        }
        catch (Exception e)
        {
            await CreateDialog("保存失败", e.Message, closeText: "我知道了").ShowAsync();
            return false;
        }

        SetPageDirty(_activeSettingsPage, false);
        CaptureEditablePageSnapshot(_activeSettingsPage);
        ShowStatus(InfoBarSeverity.Success, "保存成功", "更改已成功保存。");

        if (showSuccessDialog)
        {
            await CreateDialog("保存成功", "更改已成功保存。", closeText: "好").ShowAsync();
        }

        return true;
    }

    private void SaveChatSettings()
    {
        ValidateChatSettings();

        var config = new JsonObject
        {
            ["Enabled"] = ChatEnabledSwitch.IsOn,
            ["Name"] = ChatNameTextBox.Text.Trim(),
            ["Mode"] = 0,
            ["ApiKey"] = ChatApiKeyPasswordBox.Password.Trim(),
            ["ApiHost"] = ChatApiHostTextBox.Text.Trim(),
            ["ApiPath"] = NormalizeApiPath(ChatApiPathTextBox.Text),
            ["ModelsPath"] = "/models",
            ["DefaultModel"] = ChatModelTextBox.Text.Trim(),
            ["DefaultTemperature"] = NumberOr(ChatTemperatureNumberBox.Value, 0.7),
            ["DefaultStream"] = ChatStreamSwitch.IsOn,
            ["DefaultPassHistory"] = ChatPassHistorySwitch.IsOn,
            ["DefaultSystemPrompt"] = ChatDefaultSystemPromptTextBox.Text,
            ["UseSystemPromptFile"] = ChatUsePromptFileSwitch.IsOn,
            ["SystemPromptFile"] = ChatSystemPromptFileTextBox.Text.Trim(),
            ["UseLegacyJsonResponse"] = ChatLegacyJsonSwitch.IsOn,
            ["UserDisplayName"] = ChatUserDisplayNameTextBox.Text.Trim(),
            ["ModelList"] = new JsonArray(ChatModelListTextBox.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(value => JsonValue.Create(value)).ToArray())
        };

        LauncherConfigService.WriteJsonObject(_configPaths.AiConfigPath, config);
        LauncherConfigService.WriteText(_configPaths.DialoguePromptPath, DialoguePromptTextBox.Text);
        LauncherConfigService.WriteText(_configPaths.ControlPlannerPromptPath, ControlPlannerPromptTextBox.Text);
    }

    private void SaveWhisperSettings()
    {
        ValidateWhisperSettings();

        var config = new JsonObject
        {
            ["Enabled"] = WhisperEnabledSwitch.IsOn,
            ["WakeWord"] = WhisperWakeWordTextBox.Text.Trim(),
            ["ModelFile"] = WhisperModelFileTextBox.Text.Trim(),
            ["WakeWordSilenceSeconds"] = NumberOr(WhisperWakeSilenceNumberBox.Value, 0.6),
            ["CommandSilenceSeconds"] = NumberOr(WhisperCommandSilenceNumberBox.Value, 2.0),
            ["SubmitCommandInWakePhrase"] = WhisperSubmitTailSwitch.IsOn,
            ["RestartWakeListenDelaySeconds"] = NumberOr(WhisperRestartDelayNumberBox.Value, 0.2)
        };

        LauncherConfigService.WriteJsonObject(_configPaths.WhisperConfigPath, config);
    }

    private void SaveTtsSettings()
    {
        ValidateTtsSettings();

        var config = new JsonObject
        {
            ["Enabled"] = TtsEnabledSwitch.IsOn,
            ["Name"] = TtsNameTextBox.Text.Trim(),
            ["Mode"] = GetSelectedComboBoxInt(TtsModeComboBox, 0),
            ["ApiKey"] = TtsApiKeyPasswordBox.Password.Trim(),
            ["ApiHost"] = TtsApiHostTextBox.Text.Trim(),
            ["ApiPath"] = NormalizeApiPath(TtsApiPathTextBox.Text),
            ["Model"] = TtsModelTextBox.Text.Trim(),
            ["AudioFormat"] = TtsAudioFormatTextBox.Text.Trim(),
            ["FallbackSampleRate"] = (int)NumberOr(TtsFallbackSampleRateNumberBox.Value, 24000),
            ["FallbackChannels"] = (int)NumberOr(TtsFallbackChannelsNumberBox.Value, 1),
            ["BaseVoiceDescription"] = TtsBaseVoiceDescriptionTextBox.Text,
            ["DefaultEmotion"] = TtsDefaultEmotionTextBox.Text.Trim(),
            ["AppendEmotionToDescription"] = TtsAppendEmotionSwitch.IsOn,
            ["RemoteProtocol"] = 0,
            ["SherpaModelRoot"] = TtsSherpaModelRootTextBox.Text.Trim(),
            ["SherpaModelFile"] = TtsSherpaModelFileTextBox.Text.Trim(),
            ["SherpaTokensFile"] = TtsSherpaTokensFileTextBox.Text.Trim(),
            ["SherpaLexiconFile"] = TtsSherpaLexiconFileTextBox.Text.Trim(),
            ["SherpaDictDir"] = TtsSherpaDictDirTextBox.Text.Trim(),
            ["SherpaRuleFsts"] = new JsonArray(TtsSherpaRuleFstsTextBox.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(value => JsonValue.Create(value)).ToArray()),
            ["SherpaProvider"] = TtsSherpaProviderTextBox.Text.Trim(),
            ["SherpaNumThreads"] = (int)NumberOr(TtsSherpaThreadsNumberBox.Value, 2),
            ["SherpaDebug"] = TtsSherpaDebugSwitch.IsOn,
            ["SherpaSpeakerId"] = (int)NumberOr(TtsSherpaSpeakerNumberBox.Value, 0),
            ["SherpaSpeed"] = NumberOr(TtsSherpaSpeedNumberBox.Value, 1),
            ["SherpaMaxNumSentences"] = (int)NumberOr(TtsSherpaMaxSentencesNumberBox.Value, 2),
            ["SherpaSilenceScale"] = NumberOr(TtsSherpaSilenceScaleNumberBox.Value, 0.2),
            ["SherpaNoiseScale"] = NumberOr(TtsSherpaNoiseScaleNumberBox.Value, 0.667),
            ["SherpaNoiseScaleW"] = NumberOr(TtsSherpaNoiseScaleWNumberBox.Value, 0.8),
            ["SherpaLengthScale"] = NumberOr(TtsSherpaLengthScaleNumberBox.Value, 1)
        };

        LauncherConfigService.WriteJsonObject(_configPaths.TtsConfigPath, config);
        TtsParseWarningInfoBar.IsOpen = false;
    }

    private void SaveMemorySettings()
    {
        ValidateMemorySettings();

        var favorability = new JsonObject
        {
            ["value"] = NumberOr(FavorabilityValueNumberBox.Value, 0),
            ["updatedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["source"] = FavorabilitySourceTextBox.Text.Trim()
        };

        LauncherConfigService.WriteJsonObject(FavorabilityFileTextBox.Text.Trim(), favorability);
    }

    private void DiscardCurrentEditablePage()
    {
        _suppressEditableChangeTracking = true;
        try
        {
            switch (_activeSettingsPage)
            {
                case SettingsPage.Chat:
                    LoadChatSettings();
                    break;
                case SettingsPage.Whisper:
                    LoadWhisperSettings();
                    break;
                case SettingsPage.Tts:
                    LoadTtsSettings();
                    break;
                case SettingsPage.Memory:
                    LoadMemorySettings();
                    break;
            }
        }
        finally
        {
            _suppressEditableChangeTracking = false;
        }

        SetPageDirty(_activeSettingsPage, false);
        CaptureEditablePageSnapshot(_activeSettingsPage);
        ShowStatus(InfoBarSeverity.Informational, "已放弃更改", "页面内容已恢复为配置文件中的值。");
    }

    private void ValidateChatSettings()
    {
        if (ChatEnabledSwitch.IsOn && !LauncherConfigService.IsHttpUrl(ChatApiHostTextBox.Text.Trim()))
        {
            throw new SettingsValidationException("对话 API Host 必须是 http 或 https 开头的完整地址。");
        }

        if (ChatEnabledSwitch.IsOn && string.IsNullOrWhiteSpace(ChatApiKeyPasswordBox.Password))
        {
            throw new SettingsValidationException("启用对话 API 时必须填写 API Key。");
        }

        if (string.IsNullOrWhiteSpace(ChatModelTextBox.Text))
        {
            throw new SettingsValidationException("默认模型不能为空。");
        }

        if (ChatUsePromptFileSwitch.IsOn && string.IsNullOrWhiteSpace(ChatSystemPromptFileTextBox.Text))
        {
            throw new SettingsValidationException("启用 Prompt 文件时，System Prompt 文件路径不能为空。");
        }
    }

    private void ValidateWhisperSettings()
    {
        if (WhisperEnabledSwitch.IsOn && string.IsNullOrWhiteSpace(WhisperWakeWordTextBox.Text))
        {
            throw new SettingsValidationException("启用 Wisper 时，唤醒词不能为空。");
        }

        if (NumberOr(WhisperWakeSilenceNumberBox.Value, -1) <= 0 || NumberOr(WhisperCommandSilenceNumberBox.Value, -1) <= 0)
        {
            throw new SettingsValidationException("静音判定时间必须大于 0 秒。");
        }

        string modelPath = LauncherConfigService.ResolveStreamingAssetsPath(_configPaths, WhisperModelFileTextBox.Text.Trim());
        if (WhisperEnabledSwitch.IsOn && !File.Exists(modelPath))
        {
            throw new SettingsValidationException($"没有找到 Wisper 模型文件：{modelPath}");
        }
    }

    private void ValidateTtsSettings()
    {
        int mode = GetSelectedComboBoxInt(TtsModeComboBox, 0);
        if (!TtsEnabledSwitch.IsOn)
        {
            return;
        }

        if (mode == 0)
        {
            if (!LauncherConfigService.IsHttpUrl(TtsApiHostTextBox.Text.Trim()))
            {
                throw new SettingsValidationException("远端 TTS API Host 必须是 http 或 https 开头的完整地址。");
            }

            if (string.IsNullOrWhiteSpace(TtsApiKeyPasswordBox.Password))
            {
                throw new SettingsValidationException("启用远端 TTS 时必须填写 API Key。");
            }

            if (string.IsNullOrWhiteSpace(TtsModelTextBox.Text))
            {
                throw new SettingsValidationException("启用远端 TTS 时模型不能为空。");
            }
        }
        else
        {
            string modelRoot = LauncherConfigService.ResolveStreamingAssetsPath(_configPaths, TtsSherpaModelRootTextBox.Text.Trim());
            string modelFile = Path.Combine(modelRoot, TtsSherpaModelFileTextBox.Text.Trim());
            string tokensFile = Path.Combine(modelRoot, TtsSherpaTokensFileTextBox.Text.Trim());
            if (!File.Exists(modelFile) || !File.Exists(tokensFile))
            {
                throw new SettingsValidationException($"本地 sherpa-onnx 模型或 tokens 文件不存在：{modelRoot}");
            }
        }
    }

    private void ValidateMemorySettings()
    {
        if (!Directory.Exists(MemoryDirectoryTextBox.Text.Trim()))
        {
            throw new SettingsValidationException("Memory 文件夹不存在。");
        }

        double value = NumberOr(FavorabilityValueNumberBox.Value, -1);
        if (value < 0 || value > 1)
        {
            throw new SettingsValidationException("好感度数值需要在 0 到 1 之间。");
        }

        if (string.IsNullOrWhiteSpace(FavorabilityFileTextBox.Text))
        {
            throw new SettingsValidationException("好感度文件路径不能为空。");
        }
    }

    private void MarkCurrentEditablePageDirty()
    {
        if (_suppressEditableChangeTracking || !IsEditablePage(_activeSettingsPage))
        {
            return;
        }

        bool isDirty = !_editablePageSnapshots.TryGetValue(_activeSettingsPage, out string? snapshot)
                       || !string.Equals(snapshot, BuildEditablePageSnapshot(_activeSettingsPage), StringComparison.Ordinal);
        SetPageDirty(_activeSettingsPage, isDirty);
    }

    private void CaptureEditablePageSnapshots()
    {
        CaptureEditablePageSnapshot(SettingsPage.Chat);
        CaptureEditablePageSnapshot(SettingsPage.Whisper);
        CaptureEditablePageSnapshot(SettingsPage.Tts);
        CaptureEditablePageSnapshot(SettingsPage.Memory);
    }

    private void CaptureEditablePageSnapshot(SettingsPage page)
    {
        if (IsEditablePage(page))
        {
            _editablePageSnapshots[page] = BuildEditablePageSnapshot(page);
        }
    }

    private string BuildEditablePageSnapshot(SettingsPage page)
    {
        return page switch
        {
            SettingsPage.Chat => string.Join('\u001f',
                ChatEnabledSwitch.IsOn,
                ChatNameTextBox.Text,
                ChatApiHostTextBox.Text,
                ChatApiPathTextBox.Text,
                ChatApiKeyPasswordBox.Password,
                ChatModelTextBox.Text,
                ChatModelListTextBox.Text,
                NumberOr(ChatTemperatureNumberBox.Value, 0.7).ToString(CultureInfo.InvariantCulture),
                ChatStreamSwitch.IsOn,
                ChatPassHistorySwitch.IsOn,
                ChatUsePromptFileSwitch.IsOn,
                ChatSystemPromptFileTextBox.Text,
                ChatLegacyJsonSwitch.IsOn,
                ChatUserDisplayNameTextBox.Text,
                ChatDefaultSystemPromptTextBox.Text,
                DialoguePromptTextBox.Text,
                ControlPlannerPromptTextBox.Text),

            SettingsPage.Whisper => string.Join('\u001f',
                WhisperEnabledSwitch.IsOn,
                WhisperWakeWordTextBox.Text,
                WhisperModelFileTextBox.Text,
                NumberOr(WhisperWakeSilenceNumberBox.Value, 0.6).ToString(CultureInfo.InvariantCulture),
                NumberOr(WhisperCommandSilenceNumberBox.Value, 2.0).ToString(CultureInfo.InvariantCulture),
                WhisperSubmitTailSwitch.IsOn,
                NumberOr(WhisperRestartDelayNumberBox.Value, 0.2).ToString(CultureInfo.InvariantCulture)),

            SettingsPage.Tts => string.Join('\u001f',
                TtsEnabledSwitch.IsOn,
                TtsNameTextBox.Text,
                GetSelectedComboBoxInt(TtsModeComboBox, 0),
                TtsApiKeyPasswordBox.Password,
                TtsApiHostTextBox.Text,
                TtsApiPathTextBox.Text,
                TtsModelTextBox.Text,
                TtsAudioFormatTextBox.Text,
                NumberOr(TtsFallbackSampleRateNumberBox.Value, 24000).ToString(CultureInfo.InvariantCulture),
                NumberOr(TtsFallbackChannelsNumberBox.Value, 1).ToString(CultureInfo.InvariantCulture),
                TtsBaseVoiceDescriptionTextBox.Text,
                TtsDefaultEmotionTextBox.Text,
                TtsAppendEmotionSwitch.IsOn,
                TtsSherpaModelRootTextBox.Text,
                TtsSherpaModelFileTextBox.Text,
                TtsSherpaTokensFileTextBox.Text,
                TtsSherpaLexiconFileTextBox.Text,
                TtsSherpaDictDirTextBox.Text,
                TtsSherpaRuleFstsTextBox.Text,
                TtsSherpaProviderTextBox.Text,
                NumberOr(TtsSherpaThreadsNumberBox.Value, 2).ToString(CultureInfo.InvariantCulture),
                TtsSherpaDebugSwitch.IsOn,
                NumberOr(TtsSherpaSpeakerNumberBox.Value, 0).ToString(CultureInfo.InvariantCulture),
                NumberOr(TtsSherpaSpeedNumberBox.Value, 1).ToString(CultureInfo.InvariantCulture),
                NumberOr(TtsSherpaMaxSentencesNumberBox.Value, 2).ToString(CultureInfo.InvariantCulture),
                NumberOr(TtsSherpaSilenceScaleNumberBox.Value, 0.2).ToString(CultureInfo.InvariantCulture),
                NumberOr(TtsSherpaNoiseScaleNumberBox.Value, 0.667).ToString(CultureInfo.InvariantCulture),
                NumberOr(TtsSherpaNoiseScaleWNumberBox.Value, 0.8).ToString(CultureInfo.InvariantCulture),
                NumberOr(TtsSherpaLengthScaleNumberBox.Value, 1).ToString(CultureInfo.InvariantCulture)),

            SettingsPage.Memory => string.Join('\u001f',
                MemoryDirectoryTextBox.Text,
                MemoryFileTextBox.Text,
                FavorabilityFileTextBox.Text,
                NumberOr(FavorabilityValueNumberBox.Value, 0).ToString(CultureInfo.InvariantCulture),
                FavorabilitySourceTextBox.Text),

            _ => string.Empty
        };
    }

    private static bool IsEditablePage(SettingsPage page)
    {
        return page is SettingsPage.Chat or SettingsPage.Whisper or SettingsPage.Tts or SettingsPage.Memory;
    }

    private bool IsPageDirty(SettingsPage page)
    {
        return page switch
        {
            SettingsPage.Chat => ChatSettingsPanel.Tag as string == "Dirty",
            SettingsPage.Whisper => WhisperSettingsPanel.Tag as string == "Dirty",
            SettingsPage.Tts => TtsSettingsPanel.Tag as string == "Dirty",
            SettingsPage.Memory => MemorySettingsPanel.Tag as string == "Dirty",
            _ => false
        };
    }

    private void SetPageDirty(SettingsPage page, bool dirty)
    {
        string? value = dirty ? "Dirty" : null;
        switch (page)
        {
            case SettingsPage.Chat:
                ChatSettingsPanel.Tag = value;
                break;
            case SettingsPage.Whisper:
                WhisperSettingsPanel.Tag = value;
                break;
            case SettingsPage.Tts:
                TtsSettingsPanel.Tag = value;
                break;
            case SettingsPage.Memory:
                MemorySettingsPanel.Tag = value;
                break;
        }
    }

    private SettingsPage GetPageFromNavigationItem(NavigationViewItem item)
    {
        if (ReferenceEquals(item, LaunchSettingsItem)) return SettingsPage.Launch;
        if (ReferenceEquals(item, DownloadSettingsItem)) return SettingsPage.Download;
        if (ReferenceEquals(item, ChatSettingsItem)) return SettingsPage.Chat;
        if (ReferenceEquals(item, WhisperSettingsItem)) return SettingsPage.Whisper;
        if (ReferenceEquals(item, TtsSettingsItem)) return SettingsPage.Tts;
        if (ReferenceEquals(item, MemorySettingsItem)) return SettingsPage.Memory;
        if (ReferenceEquals(item, AboutSettingsItem)) return SettingsPage.About;
        if (ReferenceEquals(item, ExtraModuleSettingsItem)) return SettingsPage.ExtraModule;
        return SettingsPage.Launch;
    }

    private void RestoreNavigationSelection()
    {
        _suppressNavigationHandling = true;
        SettingsNavigationView.SelectedItem = _activeNavigationItem ?? LaunchSettingsItem;
        _suppressNavigationHandling = false;
    }

    private ContentDialog CreateDialog(string title, string content, string? primaryText = null, string? secondaryText = null, string? closeText = null)
    {
        return new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText ?? string.Empty,
            SecondaryButtonText = secondaryText ?? string.Empty,
            CloseButtonText = closeText ?? string.Empty,
            DefaultButton = ContentDialogButton.Primary
        };
    }

    private static string[] GetStringArray(JsonObject json, string name, string[] fallback)
    {
        if (!json.TryGetPropertyValue(name, out JsonNode? node) || node is not JsonArray array)
        {
            return fallback;
        }

        string[] values = array.Select(item => item?.GetValue<string>() ?? string.Empty).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
        return values.Length > 0 ? values : fallback;
    }

    private static string[] DefaultSherpaRuleFsts()
    {
        return new[] { "phone.fst", "date.fst", "number.fst", "new_heteronym.fst" };
    }

    private static void SelectComboBoxByTag(ComboBox comboBox, string tag)
    {
        foreach (object item in comboBox.Items)
        {
            if (item is ComboBoxItem comboBoxItem && string.Equals(comboBoxItem.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = comboBoxItem;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private static int GetSelectedComboBoxInt(ComboBox comboBox, int fallback)
    {
        if (comboBox.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int value))
        {
            return value;
        }

        return fallback;
    }

    private static double NumberOr(double value, double fallback)
    {
        return double.IsNaN(value) || double.IsInfinity(value) ? fallback : value;
    }

    private static string NormalizeApiPath(string path)
    {
        string value = string.IsNullOrWhiteSpace(path) ? "/chat/completions" : path.Trim();
        return value.StartsWith('/') ? value : "/" + value;
    }

    private string BuildMemoryPreview()
    {
        if (!File.Exists(_configPaths.MemoryJsonlPath))
        {
            return "memory.jsonl 暂时不存在。";
        }

        return string.Join(Environment.NewLine, File.ReadLines(_configPaths.MemoryJsonlPath).Take(12));
    }

    private void UpdateLaunchPathPreview(string path)
    {
        LaunchPathTextBlock.Text = string.IsNullOrWhiteSpace(path)
            ? "未设置启动路径"
            : path.Trim();
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }
}

internal enum GameLaunchState
{
    Stopped,
    Starting,
    Running
}

internal enum SettingsPage
{
    Launch,
    Download,
    Chat,
    Whisper,
    Tts,
    Memory,
    About,
    ExtraModule
}

internal sealed class SettingsValidationException : Exception
{
    public SettingsValidationException(string message) : base(message)
    {
    }
}

internal sealed class SettingsSearchEntry
{
    public SettingsSearchEntry(string title, SettingsPage page, string keywords)
    {
        Title = title;
        Page = page;
        Keywords = keywords;
    }

    public string Title { get; }
    public SettingsPage Page { get; }
    private string Keywords { get; }

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
               || Keywords.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }
}
