using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SunshineWindowFollow
{
    /// <summary>
    /// 插件设置界面。
    ///
    /// 这里刻意不使用 XAML，而是在代码里构造 WPF 控件：
    ///   - 插件工程因此不需要 XAML 编译工具链（PresentationBuildTasks），
    ///     可以在只装了 MSBuild/Roslyn 的机器上直接编译验证；
    ///   - 控件数量少，代码方式更直观，也避免 XAML 命名空间/资源查找的坑。
    /// 如果需要改成 XAML，只需要把 BuildUi 的内容搬到 .xaml 并保留相同的事件接线即可。
    /// </summary>
    public class SunshineSettingsView : UserControl
    {
        private readonly SunshineWindowFollowPlugin plugin;
        private bool loading;
        private bool settingsViewModelSubscribed;

        private CheckBox enabledCheckBox;
        private TextBox serverUrlTextBox;
        private TextBox usernameTextBox;
        private PasswordBox passwordBox;
        private CheckBox debugLoggingCheckBox;
        private TextBox startDelayTextBox;
        private ComboBox captureModeComboBox;
        private TextBox windowTitleTextBox;
        private TextBox processIdTextBox;
        private TextBlock modeHintTextBlock;
        private TextBlock testStatusTextBlock;
        private Button testButton;

        public SunshineSettingsView(SunshineWindowFollowPlugin plugin)
        {
            this.plugin = plugin;
            BuildUi();
            LoadFromSettings();

            // 只在控件加载到可视化树期间订阅，避免 Playnite 每次打开设置页都累积事件处理器。
            Loaded += SettingsView_Loaded;
            Unloaded += SettingsView_Unloaded;
        }

        private void SettingsView_Loaded(object sender, RoutedEventArgs args)
        {
            var settingsViewModel = plugin.SettingsViewModel;
            if (settingsViewModel != null && !settingsViewModelSubscribed)
            {
                // 取消编辑时 Playnite 会用 clone 替换 Settings 对象，这里同步刷新界面。
                settingsViewModel.PropertyChanged += SettingsViewModel_PropertyChanged;
                settingsViewModelSubscribed = true;
            }

            LoadFromSettings();
        }

        private void SettingsView_Unloaded(object sender, RoutedEventArgs args)
        {
            var settingsViewModel = plugin.SettingsViewModel;
            if (settingsViewModel != null && settingsViewModelSubscribed)
            {
                settingsViewModel.PropertyChanged -= SettingsViewModel_PropertyChanged;
                settingsViewModelSubscribed = false;
            }
        }

        private void SettingsViewModel_PropertyChanged(object sender, PropertyChangedEventArgs args)
        {
            if (string.Equals(args.PropertyName, "Settings", StringComparison.Ordinal))
            {
                LoadFromSettings();
            }
        }

        #region UI 构造

        private void BuildUi()
        {
            var root = new StackPanel
            {
                Margin = new Thickness(0),
                MaxWidth = 760,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            Content = root;

            root.Children.Add(CreateDescriptionText(
                "游戏启动后，让 Sunshine 把当前串流会话的捕获目标从整块显示器切换为游戏窗口（手机端始终全屏显示游戏窗口）；" +
                "游戏退出后恢复整屏捕获。需要 Sunshine 已安装支持 /api/capture/window-follow 端点的补丁版本。"));

            root.Children.Add(CreateConnectionGroup());
            root.Children.Add(CreateCaptureModeGroup());
            root.Children.Add(CreateTestGroup());
            root.Children.Add(CreateSecurityNoteText());
        }

        private GroupBox CreateConnectionGroup()
        {
            var group = new GroupBox
            {
                Header = "Sunshine 连接",
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 10)
            };

            var panel = new StackPanel();
            group.Content = panel;

            enabledCheckBox = new CheckBox
            {
                Content = "启用插件（游戏启动时自动切换 Sunshine 捕获目标）",
                Margin = new Thickness(0, 0, 0, 10)
            };
            enabledCheckBox.Checked += (sender, args) => ApplyControlValues();
            enabledCheckBox.Unchecked += (sender, args) => ApplyControlValues();
            panel.Children.Add(enabledCheckBox);

            serverUrlTextBox = new TextBox { Text = SunshineSettings.DefaultServerUrl };
            serverUrlTextBox.TextChanged += (sender, args) => ApplyControlValues();
            panel.Children.Add(CreateField("Sunshine 地址（默认 https://127.0.0.1:47990，Sunshine WebUI 端口）", serverUrlTextBox));

            usernameTextBox = new TextBox();
            usernameTextBox.TextChanged += (sender, args) => ApplyControlValues();
            panel.Children.Add(CreateField("Sunshine WebUI 用户名（HTTP Basic 认证）", usernameTextBox));

            passwordBox = new PasswordBox();
            passwordBox.PasswordChanged += (sender, args) => ApplyControlValues();
            panel.Children.Add(CreateField("Sunshine WebUI 密码（HTTP Basic 认证，本机明文保存）", passwordBox));

            var optionsPanel = new StackPanel { Orientation = Orientation.Horizontal };
            debugLoggingCheckBox = new CheckBox
            {
                Content = "输出调试日志（请求/响应详情写入 Playnite 日志）",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 24, 0)
            };
            debugLoggingCheckBox.Checked += (sender, args) => ApplyControlValues();
            debugLoggingCheckBox.Unchecked += (sender, args) => ApplyControlValues();
            optionsPanel.Children.Add(debugLoggingCheckBox);
            panel.Children.Add(optionsPanel);

            startDelayTextBox = new TextBox
            {
                Text = SunshineSettings.DefaultStartDelayMs.ToString(CultureInfo.InvariantCulture)
            };
            startDelayTextBox.TextChanged += (sender, args) => ApplyControlValues();
            panel.Children.Add(CreateField("游戏启动后延迟（毫秒，等游戏窗口出现后再切换，默认 1500）", startDelayTextBox));

            return group;
        }

        private GroupBox CreateCaptureModeGroup()
        {
            var group = new GroupBox
            {
                Header = "捕获模式",
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 10)
            };

            var panel = new StackPanel();
            group.Content = panel;

            captureModeComboBox = new ComboBox { Margin = new Thickness(0, 0, 0, 4) };
            captureModeComboBox.Items.Add(new ComboBoxItem
            {
                Content = CaptureModes.GetDisplayName(CaptureModes.Foreground),
                Tag = CaptureModes.Foreground
            });
            captureModeComboBox.Items.Add(new ComboBoxItem
            {
                Content = CaptureModes.GetDisplayName(CaptureModes.Title),
                Tag = CaptureModes.Title
            });
            captureModeComboBox.Items.Add(new ComboBoxItem
            {
                Content = CaptureModes.GetDisplayName(CaptureModes.Pid),
                Tag = CaptureModes.Pid
            });
            captureModeComboBox.SelectedIndex = 0;
            captureModeComboBox.SelectionChanged += (sender, args) => ApplyControlValues();
            panel.Children.Add(CreateField("模式（foreground 推荐；title/pid 用于多窗口游戏）", captureModeComboBox));

            modeHintTextBlock = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 8)
            };
            panel.Children.Add(modeHintTextBlock);

            windowTitleTextBox = new TextBox();
            windowTitleTextBox.TextChanged += (sender, args) => ApplyControlValues();
            panel.Children.Add(CreateField("窗口标题（仅 title 模式；留空则使用 Playnite 启动的游戏名称）", windowTitleTextBox));

            processIdTextBox = new TextBox();
            processIdTextBox.TextChanged += (sender, args) => ApplyControlValues();
            panel.Children.Add(CreateField("进程 ID（仅 pid 模式；留空或 0 则使用 Playnite 检测到的游戏进程）", processIdTextBox));

            return group;
        }

        private GroupBox CreateTestGroup()
        {
            var group = new GroupBox
            {
                Header = "测试",
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 10)
            };

            var panel = new StackPanel();
            group.Content = panel;

            testButton = new Button
            {
                Content = "测试连接",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(16, 4, 16, 4),
                Margin = new Thickness(0, 0, 0, 8)
            };
            testButton.Click += TestButton_Click;
            panel.Children.Add(testButton);

            testStatusTextBlock = new TextBlock
            {
                Text = "点击“测试连接”检查地址、证书、认证和当前串流会话状态。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray
            };
            panel.Children.Add(testStatusTextBlock);

            return group;
        }

        private static TextBlock CreateDescriptionText(string text)
        {
            return new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
        }

        private static TextBlock CreateSecurityNoteText()
        {
            return new TextBlock
            {
                Text = "安全提示：Sunshine WebUI 默认使用自签名证书，插件仅对上面配置的地址放行证书错误；" +
                       "密码使用 HTTP Basic 认证并明文保存在 Playnite 的插件设置文件中（config.json），" +
                       "请勿在不可信的多用户机器上使用，并尽量只填本机回环地址（127.0.0.1）。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 8)
            };
        }

        private static StackPanel CreateField(string label, Control control)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            panel.Children.Add(new TextBlock
            {
                Text = label,
                Margin = new Thickness(0, 0, 0, 3),
                TextWrapping = TextWrapping.Wrap
            });

            control.HorizontalAlignment = HorizontalAlignment.Stretch;
            panel.Children.Add(control);
            return panel;
        }

        #endregion

        #region 控件 <-> 设置 同步

        private void LoadFromSettings()
        {
            var viewModel = plugin.SettingsViewModel;
            var settings = viewModel == null ? null : viewModel.Settings;
            if (settings == null)
            {
                return;
            }

            loading = true;
            try
            {
                enabledCheckBox.IsChecked = settings.Enabled;
                serverUrlTextBox.Text = settings.ServerUrl ?? string.Empty;
                usernameTextBox.Text = settings.Username ?? string.Empty;
                passwordBox.Password = settings.Password ?? string.Empty;
                debugLoggingCheckBox.IsChecked = settings.DebugLogging;
                startDelayTextBox.Text = settings.StartDelayMs.ToString(CultureInfo.InvariantCulture);
                windowTitleTextBox.Text = settings.WindowTitle ?? string.Empty;
                processIdTextBox.Text = settings.ProcessId > 0 ? settings.ProcessId.ToString(CultureInfo.InvariantCulture) : string.Empty;
                SelectCaptureMode(settings.CaptureMode);
                UpdateModeFieldState();
            }
            finally
            {
                loading = false;
            }
        }

        private void ApplyControlValues()
        {
            if (loading)
            {
                return;
            }

            var viewModel = plugin.SettingsViewModel;
            var settings = viewModel == null ? null : viewModel.Settings;
            if (settings == null)
            {
                return;
            }

            settings.Enabled = enabledCheckBox.IsChecked == true;
            settings.ServerUrl = (serverUrlTextBox.Text ?? string.Empty).Trim();
            settings.Username = usernameTextBox.Text ?? string.Empty;
            settings.Password = passwordBox.Password ?? string.Empty;
            settings.DebugLogging = debugLoggingCheckBox.IsChecked == true;

            int startDelay;
            if (int.TryParse(startDelayTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out startDelay))
            {
                settings.StartDelayMs = Math.Max(0, Math.Min(10000, startDelay));
            }
            else if (string.IsNullOrWhiteSpace(startDelayTextBox.Text))
            {
                settings.StartDelayMs = SunshineSettings.DefaultStartDelayMs;
            }

            settings.CaptureMode = GetSelectedCaptureMode();
            settings.WindowTitle = windowTitleTextBox.Text ?? string.Empty;

            int processId;
            if (int.TryParse(processIdTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out processId))
            {
                settings.ProcessId = Math.Max(0, processId);
            }
            else if (string.IsNullOrWhiteSpace(processIdTextBox.Text))
            {
                settings.ProcessId = 0;
            }

            UpdateModeFieldState();
        }

        private void SelectCaptureMode(string mode)
        {
            var normalized = CaptureModes.Normalize(mode);
            foreach (var item in captureModeComboBox.Items)
            {
                var comboBoxItem = item as ComboBoxItem;
                if (comboBoxItem != null && string.Equals(comboBoxItem.Tag as string, normalized, StringComparison.Ordinal))
                {
                    captureModeComboBox.SelectedItem = comboBoxItem;
                    return;
                }
            }

            captureModeComboBox.SelectedIndex = 0;
        }

        private string GetSelectedCaptureMode()
        {
            var selectedItem = captureModeComboBox.SelectedItem as ComboBoxItem;
            if (selectedItem == null)
            {
                return CaptureModes.Foreground;
            }

            return CaptureModes.Normalize(selectedItem.Tag as string);
        }

        private void UpdateModeFieldState()
        {
            if (modeHintTextBlock == null || windowTitleTextBox == null || processIdTextBox == null)
            {
                return;
            }

            var mode = GetSelectedCaptureMode();
            windowTitleTextBox.IsEnabled = mode == CaptureModes.Title;
            processIdTextBox.IsEnabled = mode == CaptureModes.Pid;

            switch (mode)
            {
                case CaptureModes.Title:
                    modeHintTextBlock.Text = "title 模式：按窗口标题匹配目标窗口，适合同一个进程会弹出多个窗口的游戏。";
                    break;
                case CaptureModes.Pid:
                    modeHintTextBlock.Text = "pid 模式：按进程 ID 匹配目标窗口，适合一个游戏由多个进程组成的场景。";
                    break;
                default:
                    modeHintTextBlock.Text = "foreground 模式：跟随当前前台窗口，兼容性最好，推荐使用。";
                    break;
            }
        }

        #endregion

        #region 测试连接

        private async void TestButton_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = plugin.SettingsViewModel;
            var settings = viewModel == null ? null : viewModel.Settings;
            if (settings == null)
            {
                testStatusTextBlock.Text = "插件设置尚未加载。";
                testStatusTextBlock.Foreground = Brushes.Firebrick;
                return;
            }

            var snapshot = settings.Clone();
            testButton.IsEnabled = false;
            testStatusTextBlock.Foreground = Brushes.DimGray;
            testStatusTextBlock.Text = "正在连接 Sunshine ...";

            try
            {
                var result = await Task.Run(() => plugin.TestConnection(snapshot));
                testStatusTextBlock.Text = result.Message;
                testStatusTextBlock.Foreground = result.Success ? Brushes.DarkGreen : Brushes.Firebrick;
            }
            catch (Exception exception)
            {
                testStatusTextBlock.Text = "测试连接失败：" + exception.Message;
                testStatusTextBlock.Foreground = Brushes.Firebrick;
            }
            finally
            {
                testButton.IsEnabled = true;
            }
        }

        #endregion
    }
}
