using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;

namespace SoftwareAssistant
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            if (!IsAdministrator())
            {
                MessageBox.Show("请右键点击软件，选择【以管理员身份运行】，否则无法安装软件！", "需要管理员权限", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (!IsAdministrator()) return;

            string[] apps = InputTextBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (apps.Length == 0) return;

            StartButton.IsEnabled = false;
            LogTextBox.Text = "开始执行...\r\n";

            System.Threading.Tasks.Task.Run(() =>
            {
                int ok = 0, fail = 0;
                foreach (var app in apps)
                {
                    string cleanApp = app.Trim();
                    if (string.IsNullOrEmpty(cleanApp) || cleanApp.StartsWith("#")) continue;

                    AppendLog($"\r\n========================================");
                    AppendLog($"正在安装: {cleanApp} ...");

                    bool result = RunWinget(cleanApp);
                    if (result)
                    {
                        AppendLog($"✅ 成功安装: {cleanApp}");
                        ok++;
                    }
                    else
                    {
                        AppendLog($"❌ 安装失败或跳过: {cleanApp}");
                        fail++;
                    }
                }

                AppendLog($"\r\n========================================");
                AppendLog($"全部完成！成功: {ok} 个，失败/跳过: {fail} 个。");
                
                Dispatcher.Invoke(() => StartButton.IsEnabled = true);
            });
        }

        private void AppendLog(string text)
        {
            Dispatcher.Invoke(() => 
            {
                LogTextBox.AppendText(text + "\r\n");
                LogTextBox.ScrollToEnd();
            });
        }

        private bool RunWinget(string appId)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("winget", $"install --id \"{appId}\" --exact --silent --accept-package-agreements --accept-source-agreements")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    string error = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    return p.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private bool IsAdministrator()
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            LogTextBox.Text = "";
        }
    }
}
