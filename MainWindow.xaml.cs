using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

namespace SoftwareAssistant
{
    public partial class MainWindow : Window
    {
        // 中文常用名 -> 英文检索词
        private static readonly Dictionary<string, string[]> Knowledge = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "微信",     new[] { "WeChat" } },
            { "企业微信", new[] { "WeCom", "WeChat Work" } },
            { "qq",       new[] { "QQ", "Tencent QQ" } },
            { "腾讯qq",   new[] { "Tencent QQ", "QQ" } },
            { "qq音乐",   new[] { "QQ Music" } },
            { "钉钉",     new[] { "DingTalk" } },
            { "腾讯会议", new[] { "Tencent Meeting" } },
            { "向日葵",   new[] { "SunLogin", "Oray" } },
            { "网易云音乐", new[] { "NetEase Cloud Music", "CloudMusic" } },
            { "wps",      new[] { "WPS Office", "Kingsoft" } },
            { "百度网盘", new[] { "Baidu Netdisk" } },
            { "迅雷",     new[] { "Thunder", "Xunlei" } },
            { "爱奇艺",   new[] { "iQIYI" } },
            { "优酷",     new[] { "Youku" } },
            { "哔哩哔哩", new[] { "Bilibili" } },
            { "b站",      new[] { "Bilibili" } },
            { "抖音",     new[] { "Douyin", "TikTok" } },
            { "搜狗输入法", new[] { "Sogou Input" } },
            { "火绒",     new[] { "Huorong" } },
            { "360",      new[] { "360 Total Security", "360Safe" } },
            { "谷歌浏览器", new[] { "Google Chrome" } },
            { "chrome",   new[] { "Google Chrome" } },
            { "vscode",   new[] { "Visual Studio Code" } },
            { "steam",    new[] { "Steam" } },
            { "网易云",   new[] { "NetEase Cloud Music" } },
            { "剪映",     new[] { "JianYing", "CapCut" } },
            { "阿里云盘", new[] { "Aliyun Drive", "AliPan" } }
        };

        private LlmConfig _llm;

        public MainWindow()
        {
            InitializeComponent();
            if (!IsAdministrator())
            {
                MessageBox.Show("请右键点击软件，选择【以管理员身份运行】，否则无法安装软件！", "需要管理员权限", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            _llm = LoadConfig();
        }

        // ---------------- 可选的大模型辅助（默认智谱 GLM-4-Flash） ----------------

        private sealed class LlmConfig
        {
            public string ApiKey { get; set; }
            public string BaseUrl { get; set; }
            public string Model { get; set; }
        }

        private static string ConfigPath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoftwareAssistant");
                return Path.Combine(dir, "config.json");
            }
        }

        private static LlmConfig LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return null;
                string json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                var cfg = JsonSerializer.Deserialize<LlmConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (cfg == null || string.IsNullOrWhiteSpace(cfg.ApiKey)) return null;
                if (string.IsNullOrWhiteSpace(cfg.BaseUrl)) cfg.BaseUrl = "https://open.bigmodel.cn/api/paas/v4";
                if (string.IsNullOrWhiteSpace(cfg.Model)) cfg.Model = "glm-4-flash";
                return cfg;
            }
            catch { return null; }
        }

        private static string AskLlm(LlmConfig cfg, string systemPrompt, string userPrompt)
        {
            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(25);

                var payload = new
                {
                    model = cfg.Model,
                    temperature = 0,
                    messages = new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userPrompt }
                    }
                };

                var req = new HttpRequestMessage(HttpMethod.Post, cfg.BaseUrl.TrimEnd('/') + "/chat/completions");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + cfg.ApiKey);
                req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var resp = http.Send(req);
                string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (!resp.IsSuccessStatusCode)
                    throw new Exception("HTTP " + (int)resp.StatusCode + ": " + (text.Length > 160 ? text.Substring(0, 160) : text));

                using (var doc = JsonDocument.Parse(text))
                {
                    return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                }
            }
        }

        // 只在真实候选里挑，避免模型编造包名
        private int AskBestIndex(string term, List<Candidate> cands)
        {
            if (_llm == null || cands.Count < 2) return -1;

            try
            {
                int limit = Math.Min(cands.Count, 20);
                var sb = new StringBuilder();
                sb.AppendLine("用户想安装：" + term);
                sb.AppendLine("候选列表：");
                for (int i = 0; i < limit; i++)
                    sb.AppendLine(string.Format("{0}. {1} ({2})", i + 1, cands[i].Name, cands[i].Id));
                sb.AppendLine("规则：只能从上面选。只输出一个数字——最匹配候选的序号（从 1 开始）；如果都不匹配，输出 0。不要输出任何解释。");

                string ans = AskLlm(_llm, "你是软件包匹配助手，负责从候选清单中挑出用户最可能想安装的那个软件。", sb.ToString());
                var m = Regex.Match(ans ?? "", @"\d+");
                if (!m.Success) return -1;

                int n = int.Parse(m.Value);
                if (n <= 0 || n > limit) return -1;
                return n - 1;
            }
            catch (Exception ex)
            {
                AppendLog("    大模型调用失败，回退本地匹配: " + ex.Message);
                return -1;
            }
        }

        // 搜不到时，让模型给替代关键词
        private List<string> AskQueries(string term)
        {
            var result = new List<string>();
            if (_llm == null) return result;

            try
            {
                string ans = AskLlm(_llm,
                    "用户要安装某个 Windows 软件，请给出 3 个最可能的英文检索关键词。每行一个，不要编号，不要解释。",
                    "用户输入：" + term);

                foreach (var line in (ans ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string t = Regex.Replace(line, @"^\s*(\d+[\.\)、]|[-*])\s*", "").Trim();
                    if (t.Length >= 2 && t.Length <= 40) result.Add(t);
                }
            }
            catch (Exception ex)
            {
                AppendLog("    大模型调用失败: " + ex.Message);
            }

            return result.Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
        }

        // ---------------- 主流程 ----------------

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (!IsAdministrator()) return;

            var list = InputTextBox.Text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0 && !x.StartsWith("#"))
                .ToList();

            if (list.Count == 0) return;

            StartButton.IsEnabled = false;
            LogTextBox.Text = "开始执行...\r\n";
            AppendLog(_llm == null
                ? "提示: 未配置大模型，使用本地模糊匹配。配置文件: " + ConfigPath
                : "提示: 已启用大模型辅助（" + _llm.Model + "）");
            SetProgress(0, "准备中");

            Task.Run(() =>
            {
                int ok = 0, fail = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    string term = list[i];
                    AppendLog("\r\n========================================");
                    AppendLog(string.Format("[{0}/{1}] 正在安装: {2}", i + 1, list.Count, term));
                    SetProgress(i * 100.0 / list.Count, string.Format("({0}/{1}) {2}", i + 1, list.Count, term));

                    if (InstallOne(term)) ok++; else fail++;

                    SetProgress((i + 1) * 100.0 / list.Count, string.Format("已完成 {0}/{1}", i + 1, list.Count));
                }

                AppendLog("\r\n========================================");
                AppendLog(string.Format("全部完成！成功: {0} 个，失败/跳过: {1} 个。", ok, fail));
                SetProgress(100, string.Format("完成 · 成功 {0} / 失败 {1}", ok, fail));
                Dispatcher.Invoke(() => StartButton.IsEnabled = true);
            });
        }

        private bool InstallOne(string term)
        {
            if (IsUrl(term)) return InstallFromUrl(term);

            AppendLog("  [1/3] winget（winget + msstore）");
            if (TryWinget(term)) return true;

            AppendLog("  [2/3] Chocolatey");
            if (TryChoco(term)) return true;

            AppendLog("  [3/3] Scoop");
            if (TryScoop(term)) return true;

            AppendLog("  ❌ 三个包管理器都没能装上这条。");
            AppendLog("     兜底：把安装包直链粘进来（http/https 的 .exe/.msi）");
            return false;
        }

        // ---------------- winget + 模糊匹配 ----------------

        private bool TryWinget(string term)
        {
            if (LooksLikeId(term))
            {
                AppendLog("    → 按包 ID 精确安装");
                SetIndeterminate("安装 " + term);
                if (RunProcess("winget", "install --id \"" + term + "\" --exact --silent --accept-package-agreements --accept-source-agreements", out string idOut))
                {
                    AppendLog("    ✅ 成功");
                    return true;
                }
                AppendLog("    ID 未命中，改为搜索");
                AppendLastLines(idOut, 2);
            }

            var queries = BuildQueries(term);
            var all = SearchAll(queries);

            if (all.Count == 0 && _llm != null)
            {
                var extra = AskQueries(term);
                if (extra.Count > 0)
                {
                    AppendLog("    winget 无结果，大模型建议检索词: " + string.Join(" / ", extra));
                    all = SearchAll(extra);
                }
            }

            if (all.Count == 0)
            {
                AppendLog("    winget 无结果");
                return false;
            }

            foreach (var c in all)
            {
                double s = Math.Max(Score(term, c.Name), Score(term, c.Id) * 0.6);
                if (queries.Count > 1)
                {
                    foreach (var q in queries.Skip(1))
                        s = Math.Max(s, Math.Max(Score(q, c.Name) * 0.95, Score(q, c.Id) * 0.6));
                }
                c.MatchScore = s;
            }

            var ordered = all.OrderByDescending(c => c.MatchScore).Take(6).ToList();
            AppendLog(string.Format("    → 共 {0} 个候选，前 {1}：", all.Count, ordered.Count));
            foreach (var c in ordered)
                AppendLog(string.Format("        {0:0.00}  {1}  ({2}){3}", c.MatchScore, c.Name, c.Id,
                    string.IsNullOrEmpty(c.Source) ? "" : "  [" + c.Source + "]"));

            Candidate pick;
            int aiIdx = AskBestIndex(term, ordered);
            if (aiIdx >= 0)
            {
                pick = ordered[aiIdx];
                AppendLog(string.Format("    → 大模型判定为第 {0} 个: {1} ({2})", aiIdx + 1, pick.Name, pick.Id));
            }
            else
            {
                pick = ordered[0];
                AppendLog(string.Format("    → 本地匹配选择: {0} ({1})  匹配度 {2:0.00}", pick.Name, pick.Id, pick.MatchScore));
            }

            SetIndeterminate("安装 " + pick.Id);
            if (RunProcess("winget", "install --id \"" + pick.Id + "\" --exact --silent --accept-package-agreements --accept-source-agreements", out string installOut))
            {
                AppendLog("    ✅ 成功");
                return true;
            }

            AppendLog("    winget 安装失败：");
            AppendLastLines(installOut, 6);
            return false;
        }

        private List<Candidate> SearchAll(List<string> queries)
        {
            var all = new List<Candidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var q in queries)
            {
                AppendLog("    → 搜索关键词: " + q);
                SetIndeterminate("搜索: " + q);
                RunProcess("winget", "search \"" + q + "\" --accept-source-agreements", out string so);

                foreach (var c in ParseWinget(so))
                {
                    if (seen.Add(c.Id + "|" + c.Name)) all.Add(c);
                }
            }
            return all;
        }

        private sealed class Candidate
        {
            public string Name { get; set; }
            public string Id { get; set; }
            public string Source { get; set; }
            public double MatchScore { get; set; }
        }

        private static List<string> BuildQueries(string term)
        {
            var qs = new List<string> { term };
            string k = Normalize(term);
            if (k.Length == 0) return qs;

            string bestKey = null;
            int bestLen = 0;

            foreach (var kv in Knowledge)
            {
                string kk = Normalize(kv.Key);
                if (kk.Length == 0) continue;

                bool hit = k == kk
                        || (k.Length >= 2 && k.Contains(kk))
                        || (kk.Length >= 2 && kk.Contains(k));

                if (hit && kk.Length > bestLen)
                {
                    bestLen = kk.Length;
                    bestKey = kv.Key;
                }
            }

            if (bestKey != null) qs.AddRange(Knowledge[bestKey]);
            return qs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
                        list.Add(new Candidate
                        {
                            Name = n,
                            Id = id,
                            Source = m.Groups["src"].Success ? m.Groups["src"].Value.Trim() : ""
                        });
                }
            }
            return list;
        }

        private static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s.ToLowerInvariant())
                if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            return sb.ToString();
        }

        private static double Score(string term, string candidate)
        {
            string a = Normalize(term);
            string b = Normalize(candidate);
            if (a.Length == 0 || b.Length == 0) return 0;
            if (a == b) return 1.0;

            int maxLen = Math.Max(a.Length, b.Length);
            double sim = 1.0 - (double)Levenshtein(a, b) / maxLen;

            if (b.Contains(a)) sim = Math.Max(sim, 0.80 + 0.20 * ((double)a.Length / b.Length));
            if (a.Contains(b)) sim = Math.Max(sim, 0.75 + 0.20 * ((double)b.Length / a.Length));

            return Math.Min(1.0, sim);
        }

        private static int Levenshtein(string a, string b)
        {
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;

            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];

            for (int j = 0; j <= b.Length; j++) prev[j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                var tmp = prev; prev = cur; cur = tmp;
            }
            return prev[b.Length];
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
            SetIndeterminate("Chocolatey 搜索 " + key);
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

            SetIndeterminate("Chocolatey 安装 " + pick);
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
            SetIndeterminate("Scoop 安装 " + key);
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
            try
            {
                AppendLog("  → 下载安装包");
                string fileName = Path.GetFileName(new Uri(url).AbsolutePath);
                if (string.IsNullOrWhiteSpace(fileName)) fileName = "setup.exe";
                string target = Path.Combine(Path.GetTempPath(), fileName);

                using (var wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "Mozilla/5.0");
                    var tcs = new TaskCompletionSource<bool>();

                    wc.DownloadProgressChanged += (s, ev) =>
                        SetProgress(ev.ProgressPercentage, string.Format("下载中 {0}%  ({1:0.0} MB / {2:0.0} MB)",
                            ev.ProgressPercentage, ev.BytesReceived / 1048576.0, ev.TotalBytesToReceive / 1048576.0));

                    wc.DownloadFileCompleted += (s, ev) =>
                    {
                        if (ev.Error != null) tcs.TrySetException(ev.Error);
                        else if (ev.Cancelled) tcs.TrySetCanceled();
                        else tcs.TrySetResult(true);
                    };

                    wc.DownloadFileAsync(new Uri(url), target);
                    tcs.Task.Wait();
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

        // ---------------- 进度条 ----------------

        private void SetProgress(double percent, string status)
        {
            Dispatcher.Invoke(() =>
            {
                Progress.IsIndeterminate = false;
                Progress.Value = Math.Max(0, Math.Min(100, percent));
                StatusText.Text = status;
            });
        }

        private void SetIndeterminate(string status)
        {
            Dispatcher.Invoke(() =>
            {
                Progress.IsIndeterminate = true;
                StatusText.Text = status;
            });
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
