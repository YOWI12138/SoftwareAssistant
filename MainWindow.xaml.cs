using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SoftwareAssistant
{
    public class Candidate
    {
        public string Name { get; set; }
        public string Id { get; set; }
        public string Source { get; set; }
        public double MatchScore { get; set; }
    }

    public class InstalledApp
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public string ExePath { get; set; }
        public string Uninstall { get; set; }
        public string Location { get; set; }
        public ImageSource Icon { get; set; }
    }

    public enum PlanKind { Package, Url, Link, None }

    public class PlanItem
    {
        public string Term { get; set; }
        public PlanKind Kind { get; set; }
        public Candidate Pick { get; set; }
        public string Url { get; set; }
        public string SiteName { get; set; }
        public string AlternateUrl { get; set; }
        public string AlternateName { get; set; }
    }

    public partial class MainWindow : Window
    {
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
            { "剪映",     new[] { "JianYing", "CapCut" } },
            { "阿里云盘", new[] { "Aliyun Drive", "AliPan" } }
        };

        private LlmConfig _llm;
        private DispatcherTimer _suggestTimer;
        private int _suggestSeq;
        private List<InstalledApp> _installed = new List<InstalledApp>();
        private readonly Dictionary<string, ImageSource> _iconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        public MainWindow()
        {
            InitializeComponent();

            if (!IsAdministrator())
            {
                MessageBox.Show("请右键点击软件，选择【以管理员身份运行】，否则无法安装软件！", "需要管理员权限", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            _llm = LoadConfig();

            _suggestTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _suggestTimer.Tick += SuggestTimer_Tick;

            var cm = new ContextMenu();
            var miStart = new MenuItem { Header = "启动" };
            miStart.Click += delegate { StartSelected(); };
            var miFolder = new MenuItem { Header = "打开文件所在目录" };
            miFolder.Click += delegate { OpenFolderSelected(); };
            var miUninstall = new MenuItem { Header = "卸载" };
            miUninstall.Click += delegate { UninstallSelected(); };
            cm.Items.Add(miStart);
            cm.Items.Add(miFolder);
            cm.Items.Add(new Separator());
            cm.Items.Add(miUninstall);
            InstalledList.ContextMenu = cm;
            InstalledList.MouseDoubleClick += delegate { StartSelected(); };

            LoadInstalledAsync();
        }

        // ================= 配置 =================

        private sealed class LlmConfig
        {
            public string ApiKey { get; set; }
            public string BaseUrl { get; set; }
            public string Model { get; set; }
        }

        private static string PortableConfigPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json"); }
        }

        private static string RoamingConfigPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                    "SoftwareAssistant", "config.json");
            }
        }

        private static string ActiveConfigPath
        {
            get { return File.Exists(PortableConfigPath) ? PortableConfigPath : RoamingConfigPath; }
        }

        private static LlmConfig LoadConfig()
        {
            try
            {
                string path = ActiveConfigPath;
                if (!File.Exists(path)) return null;

                string json = File.ReadAllText(path, Encoding.UTF8);
                var cfg = JsonSerializer.Deserialize<LlmConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (cfg == null || string.IsNullOrWhiteSpace(cfg.ApiKey)) return null;

                if (string.IsNullOrWhiteSpace(cfg.BaseUrl)) cfg.BaseUrl = "https://open.bigmodel.cn/api/paas/v4";
                if (string.IsNullOrWhiteSpace(cfg.Model)) cfg.Model = "glm-5.3-flash";
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
                    return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            }
        }

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
                sb.AppendLine("规则：只能从上面选。只输出一个数字——最匹配候选的序号（从 1 开始）；都不匹配输出 0。不要输出解释。");

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
            catch (Exception ex) { AppendLog("    大模型调用失败: " + ex.Message); }

            return result.Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
        }

        private static readonly string[] VendorHints =
        {
            "winget 包 ID 形如 发布者.产品名，例如 Alibaba.AliWorkbench、Tencent.WeChat、Google.Chrome",
            "中国厂商常用发布者前缀：Alibaba、Tencent、ByteDance、NetEase、Baidu、Xiaomi、Huawei、Kingsoft、Sogou、Oray、Thunder、Youku、iQIYI、Bilibili",
            "产品名通常用英文或拼音，不含空格，例如 AliWorkbench、WeChat、DingTalk、SunLogin"
        };

        private List<string> AskPackageIds(string term)
        {
            var result = new List<string>();
            if (_llm == null) return result;

            try
            {
                string ans = AskLlm(_llm,
                    "你要把用户口述的软件名转换成 winget 包 ID。只输出候选包 ID，每行一个，最多 5 个，从最可能到最不可能，不要解释、不要编号。" +
                    string.Join("；", VendorHints) + "。",
                    "软件名：" + term);

                foreach (var line in (ans ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string t = Regex.Replace(line, @"^\s*(\d+[\.\)、]|[-*])\s*", "").Trim().TrimEnd('.', '。', ',', '，');
                    if (Regex.IsMatch(t, @"^[A-Za-z0-9][A-Za-z0-9_\-\.]*\.[A-Za-z0-9_\-\.]+$"))
                        result.Add(t);
                }
            }
            catch (Exception ex) { AppendLog("    大模型调用失败: " + ex.Message); }

            return result.Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        }

        private void AskOfficialSite(string term, bool isDriver, out string siteName, out string url)
        {
            siteName = null;
            url = null;
            if (_llm == null) return;

            try
            {
                string hint = isDriver
                    ? "（这是打印机或硬件驱动，请给出厂商官方驱动下载页面；厂商可能是 HP、Canon、Epson、Brother、Ricoh、Kyocera、Lenovo、Samsung、Fuji Xerox、Pantum、奔图）"
                    : "（请给出该软件的官方网站或官方下载页面）";

                string ans = AskLlm(_llm,
                    "你是软件与驱动的官方链接助手。只输出一行，格式为：名称|URL。不要输出解释、不要加代码块。",
                    "目标：" + term + hint);

                string line = (ans ?? "").Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (string.IsNullOrEmpty(line)) return;

                int bar = line.IndexOf('|');
                if (bar <= 0) return;

                string name = line.Substring(0, bar).Trim().Trim('`', '"', ' ');
                string link = line.Substring(bar + 1).Trim().Trim('`', '"', ' ');

                if (link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    link.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    siteName = string.IsNullOrEmpty(name) ? "官方网站" : name;
                    url = link;
                }
            }
            catch (Exception ex) { AppendLog("    官网查询失败: " + ex.Message); }
        }

        // ================= 包 ID 推断与学习 =================

        private Candidate ResolvePackageId(string term)
        {
            var cache = LoadAliasCache();
            if (cache.ContainsKey(term))
            {
                string cachedId = cache[term];
                AppendLog("    → 使用已学习别名: " + term + " = " + cachedId);
                if (VerifyPackageId(cachedId, out string cachedName))
                    return new Candidate { Name = cachedName, Id = cachedId, Source = "已学习" };
                AppendLog("    （缓存已失效，重新推断）");
            }

            if (_llm == null) return null;

            var guesses = AskPackageIds(term);
            foreach (var g in guesses)
            {
                AppendLog("    → 校验候选包 ID: " + g);
                if (VerifyPackageId(g, out string displayName))
                {
                    AppendLog("    ✅ 存在: " + g + "  (" + displayName + ")");
                    cache[term] = g;
                    SaveAliasCache(cache);
                    return new Candidate { Name = displayName, Id = g, Source = "AI 推断" };
                }
            }

            return null;
        }

        private bool VerifyPackageId(string id, out string displayName)
        {
            displayName = id;
            if (!RunProcess("winget", "show --id \"" + id + "\" --exact --accept-source-agreements", out string o))
                return false;

            var m = Regex.Match(o ?? "", @"(?:Found|已找到)\s+(?<name>.+?)\s+\[(?<id>[^\]]+)\]");
            if (m.Success) displayName = m.Groups["name"].Value.Trim();
            return true;
        }

        private static string AliasCachePath
        {
            get
            {
                string dir = Path.GetDirectoryName(ActiveConfigPath);
                if (string.IsNullOrEmpty(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                try { Directory.CreateDirectory(dir); } catch { }
                return Path.Combine(dir, "aliases.json");
            }
        }

        private static Dictionary<string, string> LoadAliasCache()
        {
            try
            {
                string p = AliasCachePath;
                if (!File.Exists(p)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(p, Encoding.UTF8));
                return d == null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(d, StringComparer.OrdinalIgnoreCase);
            }
            catch { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
        }

        private static void SaveAliasCache(Dictionary<string, string> cache)
        {
            try { File.WriteAllText(AliasCachePath, JsonSerializer.Serialize(cache), Encoding.UTF8); }
            catch { }
        }

        // ================= 自动补全 =================

        private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suggestTimer == null) return;
            _suggestTimer.Stop();
            _suggestTimer.Start();
        }

        private async void SuggestTimer_Tick(object sender, EventArgs e)
        {
            _suggestTimer.Stop();

            string line = CurrentLineText().Trim();
            if (line.Length < 2 || IsUrl(line))
            {
                SuggestionPopup.IsOpen = false;
                return;
            }

            int seq = ++_suggestSeq;

            List<Candidate> found;
            try
            {
                found = await Task.Run(() => SearchAll(BuildQueries(line), true)
                                            .OrderByDescending(c => Score(line, c.Name))
                                            .Take(6)
                                            .ToList());
            }
            catch { return; }

            if (seq != _suggestSeq) return;

            if (found.Count == 0) { SuggestionPopup.IsOpen = false; return; }

            SuggestionList.ItemsSource = found;
            SuggestionList.SelectedIndex = -1;
            SuggestionPopup.IsOpen = true;
        }

        private string CurrentLineText()
        {
            string text = InputTextBox.Text ?? "";
            if (text.Length == 0) return "";

            int caret = Math.Max(0, Math.Min(InputTextBox.CaretIndex, text.Length));
            int start = caret - 1;
            start = start < 0 ? 0 : text.LastIndexOf('\n', start);
            start = start < 0 ? 0 : start + 1;
            int end = caret >= text.Length ? text.Length : text.IndexOf('\n', caret);
            if (end < 0) end = text.Length;
            if (end < start) end = start;

            return text.Substring(start, end - start);
        }

        private void ReplaceCurrentLine(string newText)
        {
            string text = InputTextBox.Text ?? "";
            if (text.Length == 0) { InputTextBox.Text = newText; return; }

            int caret = Math.Max(0, Math.Min(InputTextBox.CaretIndex, text.Length));
            int start = caret - 1;
            start = start < 0 ? 0 : text.LastIndexOf('\n', start);
            start = start < 0 ? 0 : start + 1;
            int end = caret >= text.Length ? text.Length : text.IndexOf('\n', caret);
            if (end < 0) end = text.Length;
            if (end < start) end = start;

            InputTextBox.Text = text.Substring(0, start) + newText + text.Substring(end);
            InputTextBox.CaretIndex = start + newText.Length;
        }

        private void SuggestionList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            ApplySuggestion();
        }

        private void SuggestionList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { ApplySuggestion(); e.Handled = true; }
            else if (e.Key == Key.Escape) { SuggestionPopup.IsOpen = false; e.Handled = true; }
        }

        private void ApplySuggestion()
        {
            var c = SuggestionList.SelectedItem as Candidate;
            if (c == null) return;
            SuggestionPopup.IsOpen = false;
            ReplaceCurrentLine(c.Id);
        }

        // ================= 已安装软件 =================

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            LoadInstalledAsync();
        }

        private void LoadInstalledAsync()
        {
            InstalledCount.Text = "加载中…";
            Task.Run(() => LoadInstalledApps()).ContinueWith(t =>
            {
                var list = t.Result ?? new List<InstalledApp>();
                Dispatcher.Invoke(delegate
                {
                    _installed = list;
                    ApplyInstalledFilter();
                });
            });
        }

        private void InstalledFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyInstalledFilter();
        }

        private void ApplyInstalledFilter()
        {
            string key = (InstalledFilter.Text ?? "").Trim();

            var view = string.IsNullOrEmpty(key)
                ? _installed
                : _installed.Where(a => a.Name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            foreach (var a in view) a.Icon = GetIcon(a.ExePath);

            InstalledList.ItemsSource = null;
            InstalledList.ItemsSource = view;
            InstalledCount.Text = string.Format("{0} 个软件", view.Count);
        }

        private ImageSource GetIcon(string exePath)
        {
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return null;
            if (_iconCache.ContainsKey(exePath)) return _iconCache[exePath];

            ImageSource src = null;
            try
            {
                var shfi = new SHFILEINFO();
                IntPtr res = SHGetFileInfo(exePath, 0, ref shfi, (uint)Marshal.SizeOf(shfi),
                                           SHGFI_ICON | SHGFI_SMALLICON);
                if (res != IntPtr.Zero && shfi.hIcon != IntPtr.Zero)
                {
                    var bmp = Imaging.CreateBitmapSourceFromHIcon(shfi.hIcon, Int32Rect.Empty,
                                                                  BitmapSizeOptions.FromEmptyOptions());
                    bmp.Freeze();
                    src = bmp;
                    DestroyIcon(shfi.hIcon);
                }
            }
            catch { }

            _iconCache[exePath] = src;
            return src;
        }

        private void InstalledList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var item = ItemsControl.ContainerFromElement(InstalledList, (DependencyObject)e.OriginalSource) as ListBoxItem;
            if (item != null) item.IsSelected = true;
        }

        private InstalledApp SelectedApp()
        {
            return InstalledList.SelectedItem as InstalledApp;
        }

        private void StartSelected()
        {
            var app = SelectedApp();
            if (app == null) return;

            if (!string.IsNullOrEmpty(app.ExePath) && File.Exists(app.ExePath))
            {
                try { Process.Start(new ProcessStartInfo(app.ExePath) { UseShellExecute = true }); }
                catch (Exception ex) { AppendLog("启动失败: " + ex.Message); }
            }
            else AppendLog("找不到该软件的启动程序: " + app.Name);
        }

        private void OpenFolderSelected()
        {
            var app = SelectedApp();
            if (app == null) return;

            string dir = app.Location;
            if (string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(app.ExePath))
                dir = Path.GetDirectoryName(app.ExePath);

            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                try { Process.Start("explorer.exe", "\"" + dir + "\""); }
                catch (Exception ex) { AppendLog("打开目录失败: " + ex.Message); }
            }
            else AppendLog("找不到该软件的安装目录: " + app.Name);
        }

        private void UninstallSelected()
        {
            var app = SelectedApp();
            if (app == null) return;

            if (string.IsNullOrEmpty(app.Uninstall))
            {
                AppendLog("该软件没有登记卸载命令: " + app.Name);
                return;
            }

            try { Process.Start(new ProcessStartInfo("cmd.exe", "/c " + app.Uninstall) { UseShellExecute = true }); }
            catch (Exception ex) { AppendLog("卸载启动失败: " + ex.Message); }
        }

        private static List<InstalledApp> LoadInstalledApps()
        {
            var list = new List<InstalledApp>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var roots = new List<RegistryKey>();
            try { roots.Add(Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")); } catch { }
            try { roots.Add(Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall")); } catch { }
            try { roots.Add(Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")); } catch { }

            foreach (var root in roots)
            {
                if (root == null) continue;

                foreach (var name in root.GetSubKeyNames())
                {
                    RegistryKey sub = null;
                    try
                    {
                        sub = root.OpenSubKey(name);
                        if (sub == null) continue;

                        string displayName = sub.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(displayName)) continue;

                        object sysComp = sub.GetValue("SystemComponent");
                        if (sysComp != null && Convert.ToInt32(sysComp) == 1) continue;
                        if (displayName.StartsWith("Update for", StringComparison.OrdinalIgnoreCase)) continue;
                        if (displayName.StartsWith("KB", StringComparison.OrdinalIgnoreCase)) continue;

                        string version = sub.GetValue("DisplayVersion") as string ?? "";
                        string displayIcon = sub.GetValue("DisplayIcon") as string ?? "";
                        string uninstall = sub.GetValue("QuietUninstallString") as string;
                        if (string.IsNullOrWhiteSpace(uninstall))
                            uninstall = sub.GetValue("UninstallString") as string ?? "";
                        string location = sub.GetValue("InstallLocation") as string ?? "";

                        string key = displayName + "|" + version;
                        if (!seen.Add(key)) continue;

                        list.Add(new InstalledApp
                        {
                            Name = displayName.Trim(),
                            Version = version,
                            ExePath = ExtractExePath(displayIcon),
                            Uninstall = uninstall,
                            Location = location
                        });
                    }
                    catch { }
                    finally { if (sub != null) sub.Dispose(); }
                }
            }

            return list.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string ExtractExePath(string displayIcon)
        {
            if (string.IsNullOrWhiteSpace(displayIcon)) return null;

            string p = displayIcon.Trim().Trim('"');
            int comma = p.LastIndexOf(',');
            if (comma > 2) p = p.Substring(0, comma).Trim().Trim('"');

            if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(p)) return p;

            int idx = p.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                string cut = p.Substring(0, idx + 4).Trim().Trim('"');
                if (File.Exists(cut)) return cut;
            }

            return null;
        }

        // ================= 检查匹配 → 确认 → 执行 =================

        private void PreviewButton_Click(object sender, RoutedEventArgs e)
        {
            if (!IsAdministrator()) return;

            var terms = ParseInput();
            if (terms.Count == 0) return;

            SuggestionPopup.IsOpen = false;
            PreviewButton.IsEnabled = false;
            StartButton.IsEnabled = false;
            LogTextBox.Text = "正在解析匹配…\r\n";

            Task.Run(() =>
            {
                var plan = new List<PlanItem>();
                for (int i = 0; i < terms.Count; i++)
                {
                    string t = terms[i];
                    SetProgress(i * 100.0 / terms.Count, "解析: " + t);
                    AppendLog("");
                    AppendLog(string.Format("解析 ({0}/{1}): {2}", i + 1, terms.Count, t));
                    plan.Add(ResolvePlanItem(t));
                }

                Dispatcher.Invoke(delegate
                {
                    PreviewButton.IsEnabled = true;
                    StartButton.IsEnabled = true;
                    SetProgress(0, "解析完成，等待确认");
                    ShowPlanDialog(plan);
                });
            });
        }

        private PlanItem ResolvePlanItem(string term)
        {
            if (IsUrl(term))
                return new PlanItem { Term = term, Kind = PlanKind.Url };

            var pick = ResolveTerm(term);
            if (pick != null)
                return new PlanItem { Term = term, Kind = PlanKind.Package, Pick = pick };

            bool isDriver = LooksLikeDriver(term);

            AppendLog(isDriver ? "    → 按驱动处理，查询厂商官方驱动页" : "    → 包管理器都没有，查询官方网站");

            string siteName, url;
            AskOfficialSite(term, isDriver, out siteName, out url);

            var item = new PlanItem { Term = term, Kind = PlanKind.Link, SiteName = siteName, Url = url };

            if (isDriver)
            {
                item.AlternateName = "微软更新目录（驱动库）";
                item.AlternateUrl = "https://www.catalog.update.microsoft.com/Search.aspx?q=" + Uri.EscapeDataString(term);
            }

            if (string.IsNullOrEmpty(url) && string.IsNullOrEmpty(item.AlternateUrl))
            {
                item.Kind = PlanKind.None;
                AppendLog("    ❌ 未能找到任何可用来源");
            }
            else
            {
                if (!string.IsNullOrEmpty(url)) AppendLog("    → 官网: " + url);
                if (!string.IsNullOrEmpty(item.AlternateUrl)) AppendLog("    → 备用: " + item.AlternateName);
            }

            return item;
        }

        private static bool LooksLikeDriver(string term)
        {
            string k = term.ToLowerInvariant();
            return k.Contains("驱动") || k.Contains("driver") || k.Contains("打印机") || k.Contains("printer");
        }

        private void ShowPlanDialog(List<PlanItem> plan)
        {
            var win = new Window
            {
                Title = "确认安装清单",
                Width = 820,
                Height = 560,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = MakeBrush("#1B1C20")
            };

            var root = new DockPanel { Margin = new Thickness(18) };

            var header = new TextBlock
            {
                Text = "请核对每个输入对应的目标。确认无误后再点「确认并执行」。",
                Foreground = MakeBrush("#9AA0A8"),
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 12)
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            DockPanel.SetDock(buttons, Dock.Bottom);

            bool confirmed = false;

            var ok = MakeDialogButton("确认并执行", "#4C8DFF", "#FFFFFF", 140);
            var cancel = MakeDialogButton("取消", "#2E3138", "#D5D8DD", 90);
            ok.Margin = new Thickness(0, 0, 10, 0);
            ok.Click += delegate { confirmed = true; win.Close(); };
            cancel.Click += delegate { win.Close(); };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);

            var panel = new StackPanel();

            foreach (var p in plan)
            {
                var row = new Grid { Margin = new Thickness(0, 7, 0, 7) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var left = new TextBlock
                {
                    Text = p.Term,
                    Foreground = MakeBrush("#C8CDD4"),
                    FontSize = 13,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Top
                };
                Grid.SetColumn(left, 0);
                row.Children.Add(left);

                var right = new StackPanel();
                Grid.SetColumn(right, 1);

                if (p.Kind == PlanKind.Package)
                {
                    right.Children.Add(new TextBlock
                    {
                        Text = p.Pick.Name + "    " + p.Pick.Id,
                        Foreground = MakeBrush("#E4E6EA"),
                        FontSize = 13,
                        TextWrapping = TextWrapping.Wrap
                    });
                    right.Children.Add(new TextBlock
                    {
                        Text = "来源: " + (string.IsNullOrEmpty(p.Pick.Source) ? "winget" : p.Pick.Source),
                        Foreground = MakeBrush("#7C828C"),
                        FontSize = 11,
                        Margin = new Thickness(0, 2, 0, 0)
                    });
                }
                else if (p.Kind == PlanKind.Url)
                {
                    right.Children.Add(new TextBlock { Text = "直链下载", Foreground = MakeBrush("#E4E6EA"), FontSize = 13 });
                }
                else if (p.Kind == PlanKind.None)
                {
                    right.Children.Add(new TextBlock { Text = "✗ 未找到可用来源", Foreground = MakeBrush("#FF6B6B"), FontSize = 13 });
                }
                else
                {
                    if (!string.IsNullOrEmpty(p.Url))
                        right.Children.Add(MakeLink(p.SiteName + "  " + p.Url, p.Url));

                    if (!string.IsNullOrEmpty(p.AlternateUrl))
                    {
                        var alt = MakeLink(p.AlternateName + "  " + p.AlternateUrl, p.AlternateUrl);
                        alt.Margin = new Thickness(0, 4, 0, 0);
                        right.Children.Add(alt);
                    }
                }

                row.Children.Add(right);
                panel.Children.Add(row);
                panel.Children.Add(new Border { Height = 1, Background = MakeBrush("#2F3238") });
            }

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = panel
            };
            root.Children.Add(scroll);

            win.Content = root;
            win.ShowDialog();

            if (confirmed) RunPlan(plan);
        }

        private static FrameworkElement MakeLink(string text, string url)
        {
            var tb = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
            var link = new Hyperlink(new Run(text)) { Foreground = MakeBrush("#4C8DFF") };
            Uri uri;
            if (Uri.TryCreate(url, UriKind.Absolute, out uri)) link.NavigateUri = uri;
            link.RequestNavigate += delegate (object s, RequestNavigateEventArgs e)
            {
                try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
                catch { }
                e.Handled = true;
            };
            tb.Inlines.Add(link);
            return tb;
        }

        private static Button MakeDialogButton(string text, string bg, string fg, double width)
        {
            var b = new Button
            {
                Content = text,
                Width = width,
                Height = 34,
                Background = MakeBrush(bg),
                Foreground = MakeBrush(fg),
                BorderThickness = new Thickness(0)
            };
            return b;
        }

        private static readonly Dictionary<string, Brush> _brushCache = new Dictionary<string, Brush>();

        private static Brush MakeBrush(string hex)
        {
            if (_brushCache.ContainsKey(hex)) return _brushCache[hex];
            var b = (Brush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            _brushCache[hex] = b;
            return b;
        }

        private void RunPlan(List<PlanItem> plan)
        {
            StartButton.IsEnabled = false;
            PreviewButton.IsEnabled = false;

            AppendLog("\r\n开始执行 " + plan.Count + " 项…");

            Task.Run(() =>
            {
                int ok = 0, fail = 0;
                for (int i = 0; i < plan.Count; i++)
                {
                    var p = plan[i];
                    SetProgress(i * 100.0 / plan.Count, string.Format("({0}/{1}) {2}", i + 1, plan.Count, p.Term));
                    AppendLog("\r\n========================================");
                    AppendLog(string.Format("[{0}/{1}] {2}", i + 1, plan.Count, p.Term));

                    bool done = false;
                    if (p.Kind == PlanKind.Url) done = InstallFromUrl(p.Term);
                    else if (p.Kind == PlanKind.Package) done = InstallCandidate(p.Pick);
                    else if (p.Kind == PlanKind.Link)
                    {
                        string target = !string.IsNullOrEmpty(p.Url) ? p.Url : p.AlternateUrl;
                        try
                        {
                            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                            AppendLog("    → 已在浏览器中打开官方页面，请手动下载安装");
                            done = true;
                        }
                        catch (Exception ex) { AppendLog("    ❌ 打开链接失败: " + ex.Message); }
                    }

                    if (done) ok++; else fail++;
                    SetProgress((i + 1) * 100.0 / plan.Count, string.Format("已完成 {0}/{1}", i + 1, plan.Count));
                }

                AppendLog("\r\n========================================");
                AppendLog(string.Format("全部完成！成功: {0} 个，失败/跳过: {1} 个。", ok, fail));
                SetProgress(100, string.Format("完成 · 成功 {0} / 失败 {1}", ok, fail));
                Dispatcher.Invoke(delegate { StartButton.IsEnabled = true; PreviewButton.IsEnabled = true; });
            });
        }

        private List<string> ParseInput()
        {
            return InputTextBox.Text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0 && !x.StartsWith("#"))
                .ToList();
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (!IsAdministrator()) return;

            var list = ParseInput();
            if (list.Count == 0) return;

            SuggestionPopup.IsOpen = false;
            StartButton.IsEnabled = false;
            PreviewButton.IsEnabled = false;
            LogTextBox.Text = "开始执行...\r\n";
            AppendLog(_llm == null
                ? "提示: 未配置大模型，使用本地模糊匹配。配置路径: " + ActiveConfigPath
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
                Dispatcher.Invoke(delegate { StartButton.IsEnabled = true; PreviewButton.IsEnabled = true; });
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

            bool isDriver = LooksLikeDriver(term);
            string siteName, url;
            AskOfficialSite(term, isDriver, out siteName, out url);
            if (!string.IsNullOrEmpty(url))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    AppendLog("  → 已打开官方页面: " + url);
                    return true;
                }
                catch (Exception ex) { AppendLog("  打开链接失败: " + ex.Message); }
            }

            AppendLog("  ❌ 所有来源都未能安装这条。");
            return false;
        }

        // ================= 解析（不安装） =================

        private Candidate ResolveTerm(string term)
        {
            if (LooksLikeId(term) && VerifyPackageId(term, out string idName))
            {
                AppendLog("    ✅ 精确命中: " + idName + " (" + term + ")");
                return new Candidate { Name = idName, Id = term, Source = "ID" };
            }

            var queries = BuildQueries(term);
            var all = SearchAll(queries, false);

            if (all.Count == 0 && _llm != null)
            {
                var extra = AskQueries(term);
                if (extra.Count > 0)
                {
                    AppendLog("    winget 无结果，大模型建议检索词: " + string.Join(" / ", extra));
                    all = SearchAll(extra, false);
                }
            }

            if (all.Count == 0)
            {
                AppendLog("    winget 搜索无结果，尝试推断包 ID");
                var inferred = ResolvePackageId(term);
                if (inferred != null) return inferred;
            }

            if (all.Count == 0)
            {
                AppendLog("    winget 无结果");
                return null;
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

            int aiIdx = AskBestIndex(term, ordered);
            if (aiIdx >= 0)
            {
                var aiPick = ordered[aiIdx];
                AppendLog(string.Format("    → 大模型判定为第 {0} 个: {1} ({2})", aiIdx + 1, aiPick.Name, aiPick.Id));
                return aiPick;
            }

            AppendLog(string.Format("    → 本地匹配: {0} ({1})  匹配度 {2:0.00}", ordered[0].Name, ordered[0].Id, ordered[0].MatchScore));
            return ordered[0];
        }

        private bool InstallCandidate(Candidate pick)
        {
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

        private bool TryWinget(string term)
        {
            var pick = ResolveTerm(term);
            if (pick == null) return false;
            return InstallCandidate(pick);
        }

        private List<Candidate> SearchAll(List<string> queries, bool quiet)
        {
            var all = new List<Candidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var q in queries)
            {
                if (!quiet)
                {
                    AppendLog("    → 搜索关键词: " + q);
                    SetIndeterminate("搜索: " + q);
                }

                RunProcess("winget", "search \"" + q + "\" --accept-source-agreements", out string so);

                foreach (var c in ParseWinget(so))
                    if (seen.Add(c.Id + "|" + c.Name)) all.Add(c);
            }
            return all;
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

                if (hit && kk.Length > bestLen) { bestLen = kk.Length; bestKey = kv.Key; }
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

        // ================= Chocolatey / Scoop =================

        private bool TryChoco(string term)
        {
            if (!HasCommand("choco"))
            {
                AppendLog("    未检测到 Chocolatey，跳过（https://chocolatey.org/install）");
                return false;
            }

            string key = Regex.Replace(term, @"\s+", "");
            if (!Regex.IsMatch(key, @"^[A-Za-z0-9_\-\.\+]+$"))
            {
                AppendLog("    Chocolatey 只接受英文包名，跳过");
                return false;
            }

            SetIndeterminate("Chocolatey 搜索 " + key);
            if (!RunProcess("choco", "search " + key + " --limit-output --exact", out string s))
                RunProcess("choco", "search " + key + " --limit-output", out s);

            var names = ParseChoco(s);
            if (names.Count == 0) { AppendLog("    Chocolatey 无结果"); return false; }

            string pick = names.FirstOrDefault(n => string.Equals(n, key, StringComparison.OrdinalIgnoreCase)) ?? names[0];
            SetIndeterminate("Chocolatey 安装 " + pick);
            if (RunProcess("choco", "install " + pick + " -y --no-progress --limit-output", out string o))
            {
                AppendLog("    ✅ 成功: " + pick);
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

        private bool TryScoop(string term)
        {
            if (!HasCommand("scoop"))
            {
                AppendLog("    未检测到 Scoop，跳过（https://scoop.sh）");
                return false;
            }

            string key = Regex.Replace(term, @"\s+", "");
            if (!Regex.IsMatch(key, @"^[A-Za-z0-9_\-\.]+$"))
            {
                AppendLog("    Scoop 只接受英文包名，跳过");
                return false;
            }

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

        // ================= 直链下载 =================

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

                    wc.DownloadProgressChanged += delegate (object s, DownloadProgressChangedEventArgs ev)
                    {
                        SetProgress(ev.ProgressPercentage, string.Format("下载中 {0}%  ({1:0.0} MB / {2:0.0} MB)",
                            ev.ProgressPercentage, ev.BytesReceived / 1048576.0, ev.TotalBytesToReceive / 1048576.0));
                    };

                    wc.DownloadFileCompleted += delegate (object s, System.ComponentModel.AsyncCompletedEventArgs ev)
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

                AppendLog("  ✅ 安装程序已启动");
                return true;
            }
            catch (Exception ex)
            {
                AppendLog("  ❌ 下载/启动失败: " + ex.Message);
                return false;
            }
        }

        // ================= 进度 =================

        private void SetProgress(double percent, string status)
        {
            Dispatcher.Invoke(delegate
            {
                Progress.IsIndeterminate = false;
                Progress.Value = Math.Max(0, Math.Min(100, percent));
                StatusText.Text = status;
            });
        }

        private void SetIndeterminate(string status)
        {
            Dispatcher.Invoke(delegate
            {
                Progress.IsIndeterminate = true;
                StatusText.Text = status;
            });
        }

        // ================= 通用 =================

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_SMALLICON = 0x000000001;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool DestroyIcon(IntPtr hIcon);

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
                    var t1 = Task.Run(delegate { stdout = p.StandardOutput.ReadToEnd(); });
                    var t2 = Task.Run(delegate { stderr = p.StandardError.ReadToEnd(); });
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
            Dispatcher.Invoke(delegate
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
