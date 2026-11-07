using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
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
            // 0) 直链下载
            if (IsUrl(term)) return InstallFromUrl(term);

            // 1) winget（社区源 + Microsoft Store 源）
            AppendLog("  [1/3] winget（winget + msstore）");
            if (TryWinget(term)) return true;

            // 2) Chocolatey
            AppendLog("  [2/3] Chocolatey");
            if (TryChoco(term)) return true;

            // 3) Scoop
            AppendLog("  [3/3] Scoop");
            if (TryScoop(term)) return true;

            AppendLog("  ❌ 三个包管理器都没能装上这条。");
            AppendLog("     兜底办法：把安装包直链粘进来（支持 http/https 的 .exe/.msi）");
            return false;
        }

        // ---------------- winget ----------------

        private bool TryWinget(string term)
        {
            if (LooksLikeId(term))
            {
                AppendLog("    → 按包 ID 精确安装");
                if (RunProcess("winget", "install --id \"" + term + "\" --exact --silent --accept-package-agreements --accept-source-agreements", out string idOut))
                {
                    AppendLog("    ✅ 成功");
                    return true;
                }
                AppendLog("    ID 未命中，改为搜索");
                AppendLastLines(idOut, 2);
            }

            RunProcess("winget", "search \"" + term + "\" --accept-source-agreements", out string searchOut);
            var candidates = ParseWinget(searchOut);

            if (candidates.Count == 0)
            {
                AppendLog("    winget 无结果");
                return false;
            }

            AppendLog(string.Format("    → 找到 {0} 个候选", candidates.Count));
            foreach (var c in candidates.Take(6))
                AppendLog(string.Format("       · {0}  ({1}){2}", c.Name, c.Id, string.IsNullOrEmpty(c.Source) ? "" : "  [" + c.Source + "]"));

            var pick = candidates.FirstOrDefault(c => string.Equals(c.Name.Trim(), term, StringComparison.OrdinalIgnoreCase));
            if (pick == null) pick = candidates[0];

            AppendLog(string.Format("    → 选择: {0} ({1})", pick.Name, pick.Id));
            if (RunProcess("winget", "install --id \"" + pick.Id + "\" --exact --silent --accept-package-agreements --accept-source-agreements", out string installOut))
            {
                AppendLog("    ✅ 成功");
                return true;
            }

            AppendLog("    winget 安装失败：");
            AppendLastLines(installOut, 6);
            return false;
        }

        private sealed class Candidate
        {
            public string Name { get; set; }
            public string Id { get; set; }
            public string Source { get; set; }
        }

        private static List<Candidate> ParseWinget(string output)
        {
            var list = new List<Candidate>();
            if (string.IsNullOrEmpty(output)) return list;

            var rowRx = new Regex(@"^(?<name>.+?)\s+(?<id>[A-Za-z0-9][A-Za-z0-9_\-\.]*\.[A-Za-z0-9_\-\.]+|[0-9A-Z]{12})(\s+(?<ver>\S+))?(\s+(?<src>winget|msstore))?\s*$");

            foreach (var rawLine in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = CleanLine(rawLine);
                if (line.Length == 0) continue;

                string t = line.Trim();
                if (t.StartsWith("-")) continue;
                if (t.Length < 3) continue;
                if (t.StartsWith("Name") || t.StartsWith("名称")) continue;

                var parts = Regex.Split(t, @"\s{2,}");
                if (parts.Length >= 2)
                {
                    string n = parts[0].Trim();
                    string id = parts[1].Trim();
                    if (n.Length > 0 && IsIdLike(id))
                    {
                        list.Add(new Candidate { Name = n, Id = id, Source = parts.Length >= 4 ? parts[3].Trim() : "" });
                        continue;
                    }
                }

                var m = rowRx.Match(t);
                if (m.Success)
                {
                    string id = m.Groups["id"].Value.Trim();
                    string n = m.Groups["name"].Value.Trim();
                    if (n.Length > 0 && IsIdLike(id))
                        list.Add(new Candidate { Name = n, Id = id, Source = m.Groups["src"].Success ? m.Groups["src"].Value.Trim() : "" });
                }
            }
            return list;
        }

        private static bool LooksLikeId(string s)
        {
            if (s.Contains(" ")) return false;
            return Regex.IsMatch(s, @"^[A-Za-z0-9][A-Za-z0-9_\-\.]*\.[A-Za-z0-9_\-\.]+$");
        }

        private static bool IsIdLike(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (s.Contains(" ")) return false;
            if (Regex.IsMatch(s, @"^[0-9A-Z]{12}$")) return true;
            return s.Contains(".") && Regex.IsMatch(s, @"^[A-Za-z0-9][A-Za-z0-9_\-\.]*$");
        }

        // ---------------- Chocolatey ----------------

        private bool TryChoco(string term)
        {
            if (!HasCommand("choco"))
            {
                AppendLog("    未检测到 Chocolatey，跳过（安装见 https://chocolatey.org/install）");
                return false;
            }

            string key = Regex.Replace(term, @"\s+", "");
            if (!Regex.IsMatch(key, @"^[A-Za-z0-9_\-\.\+]+$"))
            {
                AppendLog("    Chocolatey 只接受英文包名，跳过");
                return false;
            }

            AppendLog("    搜索 Chocolatey 包库");
            if (!RunProcess("choco", "search " + key + " --limit-output --exact", out string s))
                RunProcess("choco", "search " + key + " --limit-output", out s);

            var names = ParseChoco(s);
            if (names.Count == 0)
            {
                AppendLog("    Chocolatey 无结果");
                return false;
            }

            string pick = names.FirstOrDefault(n => string.Equals(n, key, StringComparison.OrdinalIgnoreCase)) ?? names[0];
            AppendLog("    → 选择: " + pick);

            if (RunProcess("choco", "install " + pick + " -y --no-progress --limit-output", out string o))
            {
                AppendLog("    ✅ 成功");
                return true;
            }

            AppendLog("    Chocolatey 安装失败：");
            AppendLastLines(o, 5);
            return false;
        }

        private static List<string> ParseChoco(string output)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(output)) return list;

            foreach (var raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = CleanLine(raw).Trim();
                int idx = t.IndexOf('|');
                if (idx <= 0) continue;
                string name = t.Substring(0, idx).Trim();
                if (name.Length > 0 && Regex.IsMatch(name, @"^[A-Za-z0-9_\-\.\+]+$"))
                    list.Add(name);
            }
            return list;
        }

        // ---------------- Scoop ----------------

        private bool TryScoop(string term)
        {
            if (!HasCommand("scoop"))
            {
                AppendLog("    未检测到 Scoop，跳过（安装见 https://scoop.sh）");
                return false;
            }

            string key = Regex.Replace(term, @"\s+", "");
            if (!Regex.IsMatch(key, @"^[A-Za-z0-9_\-\.]+$"))
            {
                AppendLog("    Scoop 只接受英文包名，跳过");
                return false;
            }

            AppendLog("    尝试 scoop install " + key);
            if (RunProcess("scoop", "install " + key, out string o))
            {
                AppendLog("    ✅ 成功");
                return true;
            }

            AppendLog("    Scoop 没装上：");
            AppendLastLines(o, 4);
            return false;
        }

        // ---------------- 直链下载 ----------------

        private static bool IsUrl(string s)
        {
            return s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        private bool InstallFromUrl(string url)
        {
            string target = "";
            try
            {
                AppendLog("  → 下载安装包");
                string fileName = Path.GetFileName(new Uri(url).AbsolutePath);
                if (string.IsNullOrWhiteSpace(fileName)) fileName = "setup.exe";
                target = Path.Combine(Path.GetTempPath(), fileName);

                using (var wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "Mozilla/5.0");
                    wc.DownloadFile(url, target);
                }
                AppendLog("  → 已下载: " + target);

                if (target.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                    Process.Start(new ProcessStartInfo("msiexec", "/i \"" + target + "\""));
                else
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

                AppendLog("  ✅ 安装程序已启动，请按提示完成");
                return true;
            }
            catch (Exception ex)
            {
                AppendLog("  ❌ 下载/启动失败: " + ex.Message);
                return false;
            }
        }

        // ---------------- 通用 ----------------

        private static bool HasCommand(string name)
        {
            try
            {
                var psi = new ProcessStartInfo("where", name)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        private static string CleanLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string r = Regex.Replace(s, @"\x1B\[[0-9;?]*[A-Za-z]", "");
            r = r.Replace("\b", "").Replace("\r", "").Replace("\0", "");
            return r.TrimEnd();
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
                AppendLog("       " + lines[i]);
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
