using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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

            Task.Run(() =>
            {
                int ok = 0, fail = 0;
                foreach (var raw in apps)
                {
                    string term = raw.Trim();
                    if (string.IsNullOrEmpty(term) || term.StartsWith("#")) continue;

                    AppendLog("\r\n========================================");
                    AppendLog("正在安装: " + term);

                    if (InstallOne(term)) ok++; else fail++;
                }

                AppendLog("\r\n========================================");
                AppendLog(string.Format("全部完成！成功: {0} 个，失败/跳过: {1} 个。", ok, fail));
                Dispatcher.Invoke(() => StartButton.IsEnabled = true);
            });
        }

        private bool InstallOne(string term)
        {
            // 1) 看起来像包 ID（含点、无空格）就先按 ID 精确安装
            if (LooksLikeId(term))
            {
                AppendLog("  → 按包 ID 精确安装");
                if (RunWinget("install --id \"" + term + "\" --exact --silent", out string idOut))
                {
                    AppendLog("  ✅ 成功");
                    return true;
                }
                AppendLog("  ID 未命中，改为按名称搜索");
                AppendLastLines(idOut, 3);
            }

            // 2) 按关键词搜索
            AppendLog("  → 搜索匹配的软件");
            RunWingetRaw("search --source winget \"" + term + "\"", out string searchOut);
            var candidates = ParseSearch(searchOut);

            if (candidates.Count == 0)
            {
                AppendLog("  ❌ 没有找到匹配的包");
                AppendLog("     winget 原始输出：");
                AppendLastLines(searchOut, 6);
                return false;
            }

            AppendLog(string.Format("  → 找到 {0} 个候选", candidates.Count));
            foreach (var c in candidates.Take(5))
                AppendLog(string.Format("      · {0}  ({1})", c.Name, c.Id));

            var pick = candidates.FirstOrDefault(c => string.Equals(c.Name.Trim(), term, StringComparison.OrdinalIgnoreCase));
            if (pick == null) pick = candidates[0];

            AppendLog(string.Format("  → 选择: {0} ({1})", pick.Name, pick.Id));
            if (RunWinget("install --id \"" + pick.Id + "\" --exact --silent", out string installOut))
            {
                AppendLog("  ✅ 成功");
                return true;
            }

            AppendLog("  ❌ 安装失败，winget 输出：");
            AppendLastLines(installOut, 8);
            return false;
        }

        private static bool LooksLikeId(string s)
        {
            if (s.Contains(" ")) return false;
            return Regex.IsMatch(s, @"^[A-Za-z0-9][A-Za-z0-9_\-\.]*\.[A-Za-z0-9_\-\.]+$");
        }

        private sealed class Candidate
        {
            public string Name { get; set; }
            public string Id { get; set; }
        }

        private static List<Candidate> ParseSearch(string output)
        {
            var list = new List<Candidate>();
            if (string.IsNullOrEmpty(output)) return list;

            var rowRx = new Regex(@"^(?<name>.+?)\s+(?<id>[A-Za-z0-9][A-Za-z0-9_\-\.]*\.[A-Za-z0-9_\-\.]+)\s+(?<ver>\S+)\s+(?<src>winget|msstore)\s*$");

            foreach (var rawLine in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = CleanLine(rawLine);
                if (line.Length == 0) continue;

                string t = line.Trim();
                if (t.StartsWith("-")) continue;
                if (t.StartsWith("Name") || t.StartsWith("名称")) continue;

                var m = rowRx.Match(t);
                if (m.Success)
                {
                    list.Add(new Candidate
                    {
                        Name = m.Groups["name"].Value.Trim(),
                        Id = m.Groups["id"].Value.Trim()
                    });
                }
            }
            return list;
        }

        private static string CleanLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string r = Regex.Replace(s, @"\x1B\[[0-9;?]*[A-Za-z]", "");
            r = r.Replace("\b", "").Replace("\r", "").Replace("\0", "");
            return r.TrimEnd();
        }

        private bool RunWinget(string args, out string output)
        {
            return RunProcess("winget", args + " --accept-package-agreements --accept-source-agreements", out output);
        }

        private bool RunWingetRaw(string args, out string output)
        {
            return RunProcess("winget", args + " --accept-source-agreements", out output);
        }

        private bool RunProcess(string fileName, string args, out string output)
        {
            output = "";
            try
            {
                var psi = new ProcessStartInfo(fileName, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using (var p = Process.Start(psi))
                {
                    string stdout = "";
                    string stderr = "";
                    var t1 = Task.Run(() => { stdout = p.StandardOutput.ReadToEnd(); });
                    var t2 = Task.Run(() => { stderr = p.StandardError.ReadToEnd(); });
                    p.WaitForExit();
                    Task.WaitAll(t1, t2);
                    output = (stdout + "\r\n" + stderr).Trim();
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return false;
            }
        }

        private void AppendLastLines(string text, int count)
        {
            if (string.IsNullOrEmpty(text)) return;

            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(CleanLine)
                            .Where(l => l.Length > 0)
                            .ToList();

            int start = Math.Max(0, lines.Count - count);
            for (int i = start; i < lines.Count; i++)
                AppendLog("     " + lines[i]);
        }

        private void AppendLog(string text)
        {
            Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText(text + "\r\n");
                LogTextBox.ScrollToEnd();
            });
        }

        private static bool IsAdministrator()
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
