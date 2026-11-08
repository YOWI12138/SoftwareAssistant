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

    public class CatalogEntry
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string NameNorm { get; set; }
        public string IdNorm { get; set; }
        public string Init { get; set; }
    }

    public class InstalledApp
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public string ExePath { get; set; }
        public string Uninstall { get; set; }
        public string Location { get; set; }
        public ImageSource Icon { get; set; }
        public DateTime? InstallTime { get; set; }
        public Visibility NewMark { get; set; }
    }

    public class WebHit
    {
        public string Title { get; set; }
        public string Url { get; set; }
        public string Host { get; set; }
        public bool Trusted { get; set; }
    }

    public class ItemResult
    {
        public string Term { get; set; }
        public string Status { get; set; }
        public string PackageId { get; set; }
        public string PackageName { get; set; }
        public string InstallDir { get; set; }
        public List<WebHit> Hits { get; set; }
        public bool Ok { get; set; }
    }

    public enum PlanKind { Package, Url, Link, None }

    public class PlanItem
    {
        public string Term { get; set; }
        public PlanKind Kind { get; set; }
        public Candidate Pick { get; set; }
        public string Url { get; set; }
        public string SiteName { get; set; }
        public string SupportUrl { get; set; }
        public string AlternateUrl { get; set; }
        public string AlternateName { get; set; }
    }

    public partial class MainWindow : Window
    {
        private static readonly Dictionary<string, string[]> Knowledge = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "微信",     new[] { "WeChat" } },
            { "企业微信", new[] { "WeCom" } },
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
            { "b站",      new[] { "Bilibili" } },
            { "抖音",     new[] { "Douyin", "TikTok" } },
            { "剪映",     new[] { "JianYing", "CapCut" } }
        };

        private static readonly string[] BlockedHosts =
        {
            "zol.com.cn", "onlinedown.net", "huajun.com", "pconline.com.cn", "pc6.com",
            "duote.com", "xiazaizhijia.com", "2345.com", "crsky.com", "downza.cn",
            "52pk.com", "skycn.com", "greenxf.com", "ddooo.com", "softpedia.com",
            "baidu.com", "bing.com", "microsoft.com", "so.com", "sogou.com"
        };

        private static readonly Dictionary<char, char> PyInit = BuildPyMap();

        private static Dictionary<char, char> BuildPyMap()
        {
            var d = new Dictionary<char, char>();
            const string pairs =
                "微w信x腾t讯x企q业y钉d飞f书s会h议y网w易y音y乐l视s频p播b放f器q浏l览l谷g歌g火f狐h搜s狗g输s入r法f百b度d盘p里l巴b淘t宝b天t猫m京j东d拼p多d苏s宁n美m团t大d众z点d评p小x红h书s知z乎h抖d快k手s博b安a全q卫w士s管g家j驱q打d印y机j笔b记j本b电d脑n桌z面m文w档d图t下x载z压y缩s解j工g具j开k发f编b辑j代d码m邮y箱x聊l远y程c控k制z录l屏p截j转z换h格g式s游y戏x平p台t加j速s模m拟n杀s毒d优y化h清q理l测c翻f译y词c典d手s写x语y识s别b云y盘p同t步b备b份f恢h复f";

            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                char c = pairs[i];
                char l = pairs[i + 1];
                if (!d.ContainsKey(c)) d[c] = l;
            }
            return d;
        }

        private LlmConfig _llm;
        private DispatcherTimer _suggestTimer;
        private DispatcherTimer _searchTimer;
        private DispatcherTimer _progressTimer;
        private List<InstalledApp> _installed = new List<InstalledApp>();
        private readonly Dictionary<string, ImageSource> _iconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private List<CatalogEntry> _catalog = new List<CatalogEntry>();
        private readonly List<ItemResult> _results = new List<ItemResult>();
        private volatile bool _stopRequested;
        private volatile Process _currentProc;
        private volatile WebClient _currentWebClient;

        private DateTime _itemStart = DateTime.Now;
        private int _itemIndex;
        private int _itemCount = 1;
        private double _avgItemSeconds = 45;

        private static readonly HttpClient _http = CreateHttp();

        private static HttpClient CreateHttp()
        {
            var c = new HttpClient();
            c.Timeout = TimeSpan.FromSeconds(60);
            try { c.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"); } catch { }
            return c;
        }

        public MainWindow()
        {
            InitializeComponent();

            if (!IsAdministrator())
            {
                MessageBox.Show("请右键点击软件，选择【以管理员身份运行】，否则无法安装软件！", "需要管理员权限", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            _llm = LoadConfig();
            LoadCatalog();

            _suggestTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            _suggestTimer.Tick += SuggestTimer_Tick;

            _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
            _searchTimer.Tick += SearchTimer_Tick;

            _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _progressTimer.Tick += ProgressTimer_Tick;

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

        // ================= 本地名录 =================

        private static IEnumerable<string> CatalogPaths()
        {
            yield return Path.Combine(AppContext.BaseDirectory, "winget_catalog.json");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                      "SoftwareAssistant", "winget_catalog.json");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "winget_catalog.json");
        }

        private void LoadCatalog()
        {
            string found = null;
            foreach (var p in CatalogPaths())
                if (File.Exists(p)) { found = p; break; }

            if (found == null)
            {
                CatalogHint.Text = "搜索软件（未找到本地名录，仅联网搜索）";
                AppendLog("提示: 未找到 winget_catalog.json，本地搜索不可用");
                return;
            }

            try
            {
                var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(found, Encoding.UTF8), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                var list = new List<CatalogEntry>();
                if (raw != null)
                {
                    foreach (var kv in raw)
                    {
                        string name = kv.Value ?? "";
                        list.Add(new CatalogEntry
                        {
                            Id = kv.Key,
                            Name = name,
                            NameNorm = Normalize(name),
                            IdNorm = Normalize(kv.Key),
                            Init = Initials(name + " " + kv.Key)
                        });
                    }
                }

                _catalog = list;
                CatalogHint.Text = string.Format("搜索软件（本地名录 {0} 条）", list.Count);
                AppendLog(string.Format("本地名录已加载: {0} 条", list.Count));
            }
            catch (Exception ex) { AppendLog("本地名录加载失败: " + ex.Message); }
        }

        private static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s.ToLowerInvariant())
                if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            return sb.ToString();
        }

        private static string Initials(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";

            var sb = new StringBuilder();
            bool inAsciiWord = false;

            foreach (char ch in s)
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9'))
                {
                    if (!inAsciiWord) sb.Append(char.ToLowerInvariant(ch));
                    inAsciiWord = true;
                }
                else if (ch > 127)
                {
                    char l;
                    if (PyInit.TryGetValue(ch, out l)) sb.Append(l);
                    inAsciiWord = false;
                }
                else inAsciiWord = false;
            }
            return sb.ToString();
        }

        private static int RankEntry(CatalogEntry e, string q)
        {
            if (e.NameNorm == q || e.Init == q || e.IdNorm == q) return 100;
            if (e.NameNorm.StartsWith(q)) return 92;
            if (e.Init.StartsWith(q)) return 86;
            if (e.NameNorm.Contains(q)) return 74;
            if (e.IdNorm.Contains(q)) return 40;
            return 10;
        }

        private List<CatalogEntry> SearchCatalog(string query, int max)
        {
            var result = new List<CatalogEntry>();
            if (_catalog.Count == 0) return result;

            string q = Normalize(query);
            if (q.Length == 0) return result;

            foreach (var e in _catalog)
            {
                if (e.NameNorm.Length == 0) continue;
                if (e.NameNorm.IndexOf(q, StringComparison.Ordinal) >= 0
                    || e.Init.IndexOf(q, StringComparison.Ordinal) >= 0
                    || e.IdNorm.IndexOf(q, StringComparison.Ordinal) >= 0)
                    result.Add(e);
            }

            return result.OrderByDescending(e => RankEntry(e, q)).ThenBy(e => e.Name.Length).Take(max).ToList();
        }

        // ================= 搜索框 =================

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_searchTimer == null) return;
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        private void SearchTimer_Tick(object sender, EventArgs e)
        {
            _searchTimer.Stop();
            string q = (SearchBox.Text ?? "").Trim();
            if (q.Length == 0) { SearchPopup.IsOpen = false; return; }

            var hits = SearchCatalog(q, 12);
            if (hits.Count == 0) { SearchPopup.IsOpen = false; return; }

            SearchList.ItemsSource = hits;
            SearchList.SelectedIndex = -1;
            SearchPopup.IsOpen = true;
        }

        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down && SearchPopup.IsOpen)
            {
                SearchList.Focus();
                if (SearchList.Items.Count > 0) SearchList.SelectedIndex = 0;
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && SearchPopup.IsOpen) { AddSelectedFromSearch(); e.Handled = true; }
            else if (e.Key == Key.Escape) SearchPopup.IsOpen = false;
        }

        private void SearchList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) { AddSelectedFromSearch(); }

        private void SearchList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { AddSelectedFromSearch(); e.Handled = true; }
            else if (e.Key == Key.Escape) { SearchPopup.IsOpen = false; SearchBox.Focus(); e.Handled = true; }
        }

        private void AddSelectedFromSearch()
        {
            var entry = SearchList.SelectedItem as CatalogEntry;
            if (entry == null) return;

            SearchPopup.IsOpen = false;
            SearchBox.Text = "";

            string cur = InputTextBox.Text ?? "";
            if (cur.Length > 0 && !cur.EndsWith("\n")) cur += "\r\n";
            InputTextBox.Text = cur + entry.Id + "\r\n";
            InputTextBox.CaretIndex = InputTextBox.Text.Length;
            InputTextBox.ScrollToEnd();

            AppendLog("已加入列表: " + entry.Name + " (" + entry.Id + ")");
        }

        // ================= 列表补全 =================

        private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suggestTimer == null) return;
            _suggestTimer.Stop();
            _suggestTimer.Start();
        }

        private void SuggestTimer_Tick(object sender, EventArgs e)
        {
            _suggestTimer.Stop();
            string line = CurrentLineText().Trim();
            if (line.Length < 1 || IsUrl(line)) { SuggestionPopup.IsOpen = false; return; }

            var hits = SearchCatalog(line, 8);
            if (hits.Count == 0) { SuggestionPopup.IsOpen = false; return; }

            SuggestionList.ItemsSource = hits;
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

        private void SuggestionList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) { ApplySuggestion(); }

        private void SuggestionList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { ApplySuggestion(); e.Handled = true; }
            else if (e.Key == Key.Escape) { SuggestionPopup.IsOpen = false; e.Handled = true; }
        }

        private void ApplySuggestion()
        {
            var c = SuggestionList.SelectedItem as CatalogEntry;
            if (c == null) return;
            SuggestionPopup.IsOpen = false;
            ReplaceCurrentLine(c.Id);
        }

        // ================= 配置与大模型 =================

        private sealed class LlmConfig
        {
            public string ApiKey { get; set; }
            public string BaseUrl { get; set; }
            public string Model { get; set; }
        }

        private static string PortableConfigPath { get { return Path.Combine(AppContext.BaseDirectory, "config.json"); } }

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

                var cfg = JsonSerializer.Deserialize<LlmConfig>(File.ReadAllText(path, Encoding.UTF8),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (cfg == null || string.IsNullOrWhiteSpace(cfg.ApiKey)) return null;

                if (string.IsNullOrWhiteSpace(cfg.BaseUrl)) cfg.BaseUrl = "https://open.bigmodel.cn/api/paas/v4";
                if (string.IsNullOrWhiteSpace(cfg.Model)) cfg.Model = "glm-4.7-flashx";
                return cfg;
            }
            catch { return null; }
        }

        private string AskLlm(LlmConfig cfg, string systemPrompt, string userPrompt)
        {
            if (_stopRequested) return "";

            var payload = new
            {
                model = cfg.Model,
                temperature = 0,
                max_tokens = 1024,
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                }
            };

            string body = JsonSerializer.Serialize(payload);
            Exception last = null;

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Post, cfg.BaseUrl.TrimEnd('/') + "/chat/completions"))
                    {
                        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + cfg.ApiKey);
                        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                        var resp = _http.Send(req);
                        string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                        if (!resp.IsSuccessStatusCode)
                            throw new Exception("HTTP " + (int)resp.StatusCode + ": " + (text.Length > 160 ? text.Substring(0, 160) : text));

                        using (var doc = JsonDocument.Parse(text))
                        {
                            var msg = doc.RootElement.GetProperty("choices")[0].GetProperty("message");

                            string content = null;
                            JsonElement ce;
                            if (msg.TryGetProperty("content", out ce) && ce.ValueKind == JsonValueKind.String)
                                content = ce.GetString();

                            JsonElement re;
                            if (string.IsNullOrWhiteSpace(content) && msg.TryGetProperty("reasoning_content", out re) && re.ValueKind == JsonValueKind.String)
                                content = re.GetString();

                            return content ?? "";
                        }
                    }
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt == 1) System.Threading.Thread.Sleep(1200);
                }
            }

            throw last ?? new Exception("未知错误");
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
                sb.AppendLine("只能从上面选。只输出一个数字；都不匹配输出 0。不要解释。");

                string ans = AskLlm(_llm, "你是软件包匹配助手。", sb.ToString());
                var m = Regex.Match(ans ?? "", @"\d+");
                if (!m.Success) return -1;

                int n = int.Parse(m.Value);
                if (n <= 0 || n > limit) return -1;
                return n - 1;
            }
            catch (Exception ex)
            {
                AppendLog("    大模型调用失败: " + ex.Message);
                return -1;
            }
        }

        private List<string> AskOfficialNames(string term)
        {
            var list = new List<string>();
            if (_llm == null) return list;

            try
            {
                string ans = AskLlm(_llm,
                    "把用户口述的软件名转换成官方名称。每行一个候选名称，最多 5 个，不要编号，不要解释。",
                    "软件：" + term);

                foreach (var line in (ans ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string t = Regex.Replace(line, @"^\s*(\d+[\.\)、]|[-*])\s*", "").Trim();
                    if (t.Length >= 2 && t.Length <= 60) list.Add(t);
                }
            }
            catch (Exception ex) { AppendLog("    大模型调用失败: " + ex.Message); }

            return list.Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        }

        private void AskOfficialSite(string term, bool isDriver, out string siteName, out string url, out string supportUrl)
        {
            siteName = null;
            url = null;
            supportUrl = null;
            if (_llm == null) return;

            try
            {
                string sys = isDriver
                    ? "你是硬件驱动查找助手。给出厂商官方的『驱动下载页面』和『产品支持页面』。只输出一行：名称|驱动下载页|支持页。不要解释。"
                    : "你是软件链接助手。给出该软件的官方下载页面和支持页面。只输出一行：名称|官方下载页|支持页。不要解释。";

                string user = "目标：" + term + (isDriver
                    ? "（这是打印机或硬件驱动。必须给到具体型号的驱动页面，不要厂商首页。厂商可能是 HP、Canon、Epson、Brother、Ricoh、Kyocera、Lenovo、Samsung、Fuji Xerox、Pantum/奔图）"
                    : "");

                string ans = AskLlm(_llm, sys, user);
                string line = (ans ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                         .Select(x => x.Trim()).FirstOrDefault(x => x.Contains("|"));
                if (string.IsNullOrEmpty(line)) return;

                var parts = line.Split('|');
                siteName = parts[0].Trim().Trim('`', '"', ' ');
                if (parts.Length >= 2) url = CleanUrl(parts[1]);
                if (parts.Length >= 3) supportUrl = CleanUrl(parts[2]);
            }
            catch (Exception ex) { AppendLog("    官网查询失败: " + ex.Message); }
        }

        private static string CleanUrl(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            string u = s.Trim().Trim('`', '"', '\'', ' ', '。', '，');
            if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
            return u;
        }

        private static bool IsHomepage(string url)
        {
            try
            {
                var u = new Uri(url);
                return u.AbsolutePath == "/" || string.IsNullOrEmpty(u.AbsolutePath);
            }
            catch { return true; }
        }

        // ================= 网页检索（并行） =================

        private List<WebHit> SearchWeb(string term)
        {
            if (_stopRequested) return new List<WebHit>();

            string q = Uri.EscapeDataString(term + " 官网 下载");

            string[] engines =
            {
                "https://www.bing.com/search?q=" + q + "&setlang=zh-CN",
                "https://cn.bing.com/search?q=" + q,
                "https://www.sogou.com/web?query=" + q,
                "https://html.duckduckgo.com/html/?q=" + q,
                "https://www.so.com/s?q=" + q
            };

            foreach (var engine in engines)
            {
                if (_stopRequested) break;

                string html = HttpGetText(engine);
                if (string.IsNullOrEmpty(html)) continue;

                var hits = ParseHits(html);
                if (hits.Count == 0) continue;

                AppendLog("    → 网页检索成功（来源 " + HostOf(engine) + "，共 " + hits.Count + " 条）");
                ConfirmBestLink(term, hits);
                return hits;
            }

            AppendLog("    → 各搜索源均未取到结果，已提供搜索链接兜底");
            return new List<WebHit>();
        }

        private static string HostOf(string url)
        {
            try { return new Uri(url).Host; } catch { return url; }
        }

        private static List<WebHit> ParseHits(string html)
        {
            var hits = new List<WebHit>();
            var seenHost = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string[] patterns =
            {
                @"<h2>\s*<a[^>]*href=""(?<u>https?://[^""]+)""[^>]*>(?<t>.*?)</a>",
                @"<a[^>]*class=""[^""]*result__a[^""]*""[^>]*href=""(?<u>https?://[^""]+)""[^>]*>(?<t>.*?)</a>",
                @"<h3[^>]*>\s*<a[^>]*href=""(?<u>https?://[^""]+)""[^>]*>(?<t>.*?)</a>"
            };

            foreach (var pat in patterns)
            {
                var rx = new Regex(pat, RegexOptions.Singleline | RegexOptions.IgnoreCase);

                foreach (Match m in rx.Matches(html))
                {
                    string u = m.Groups["u"].Value;
                    if (string.IsNullOrEmpty(u) || u.Length > 300) continue;

                    string host;
                    try { host = new Uri(u).Host; } catch { continue; }

                    if (BlockedHosts.Any(b => host.EndsWith(b, StringComparison.OrdinalIgnoreCase))) continue;
                    if (!seenHost.Add(host)) continue;

                    string title = Regex.Replace(m.Groups["t"].Value, "<.*?>", "").Trim();

                    hits.Add(new WebHit
                    {
                        Title = DecodeHtml(title),
                        Url = u,
                        Host = host
                    });

                    if (hits.Count >= 8) break;
                }

                if (hits.Count > 0) break;
            }

            return hits;
        }

        private static string DecodeHtml(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
                    .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&nbsp;", " ");
        }

        private void ConfirmBestLink(string term, List<WebHit> hits)
        {
            if (_llm == null) { hits[0].Trusted = true; return; }

            try
            {
                int limit = Math.Min(hits.Count, 6);
                var sb = new StringBuilder();
                sb.AppendLine("要安装的软件：" + term);
                sb.AppendLine("网页候选项：");
                for (int i = 0; i < limit; i++)
                    sb.AppendLine(string.Format("{0}. {1}  {2}", i + 1, hits[i].Title, hits[i].Url));
                sb.AppendLine("从中选出最可能是『官方下载页』的一项。只输出一个数字；都不像就输出 0。不要解释。");

                string ans = AskLlm(_llm, "你负责判断哪个链接是软件官方网站或官方下载页。", sb.ToString());
                var m = Regex.Match(ans ?? "", @"\d+");
                if (!m.Success) { hits[0].Trusted = true; return; }

                int n = int.Parse(m.Value);
                if (n >= 1 && n <= limit) hits[n - 1].Trusted = true;
            }
            catch
            {
                if (hits.Count > 0) hits[0].Trusted = true;
            }
        }

        private static string HttpGetText(string url)
        {
            try
            {
                var resp = _http.GetAsync(url).GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode) return null;
                return resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            catch { return null; }
        }

        // ================= 包 ID 推断 =================

        private Candidate ResolvePackageId(string term)
        {
            var cache = LoadAliasCache();
            if (cache.ContainsKey(term))
            {
                string cachedId = cache[term];
                if (VerifyPackageId(cachedId, out string cachedName))
                    return new Candidate { Name = cachedName, Id = cachedId, Source = "已学习" };
            }

            if (_llm == null) return null;

            foreach (var g in AskPackageIds(term))
            {
                AppendLog("    → 校验包 ID: " + g);
                if (!VerifyPackageId(g, out string displayName)) continue;

                double sim = Math.Max(Score(term, displayName), Score(term, g));
                if (sim < 0.30)
                {
                    AppendLog("    ⚠ 名称差异过大，已丢弃: " + g + " (" + displayName + ")");
                    continue;
                }

                cache[term] = g;
                SaveAliasCache(cache);
                return new Candidate { Name = displayName, Id = g, Source = "AI 推断" };
            }
            return null;
        }

        private List<string> AskPackageIds(string term)
        {
            var result = new List<string>();
            if (_llm == null) return result;

            try
            {
                string ans = AskLlm(_llm,
                    "把软件名转换成 winget 包 ID。只输出候选包 ID，每行一个，最多 5 个，不要解释。" +
                    "包 ID 形如 发布者.产品名，例如 Alibaba.AliWorkbench、Tencent.WeChat、Google.Chrome。",
                    "软件名：" + term);

                foreach (var line in (ans ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string t = Regex.Replace(line, @"^\s*(\d+[\.\)、]|[-*])\s*", "").Trim().TrimEnd('.', '。', ',', '，');
                    if (Regex.IsMatch(t, @"^[A-Za-z0-9][A-Za-z0-9_\-\.]*\.[A-Za-z0-9_\-\.]+$")) result.Add(t);
                }
            }
            catch (Exception ex) { AppendLog("    大模型调用失败: " + ex.Message); }

            return result.Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
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
                return d == null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                                 : new Dictionary<string, string>(d, StringComparer.OrdinalIgnoreCase);
            }
            catch { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
        }

        private static void SaveAliasCache(Dictionary<string, string> cache)
        {
            try { File.WriteAllText(AliasCachePath, JsonSerializer.Serialize(cache), Encoding.UTF8); } catch { }
        }

        // ================= 已安装软件 =================

        private void RefreshButton_Click(object sender, RoutedEventArgs e) { LoadInstalledAsync(); }

        private void LoadInstalledAsync()
        {
            InstalledCount.Text = "加载中…";
            Task.Run(() => LoadInstalledApps()).ContinueWith(t =>
            {
                var list = t.Result ?? new List<InstalledApp>();
                Dispatcher.Invoke(delegate { _installed = list; ApplyInstalledFilter(); });
            });
        }

        private void InstalledFilter_TextChanged(object sender, TextChangedEventArgs e) { ApplyInstalledFilter(); }

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
                IntPtr res = SHGetFileInfo(exePath, 0, ref shfi, (uint)Marshal.SizeOf(shfi), SHGFI_ICON | SHGFI_SMALLICON);
                if (res != IntPtr.Zero && shfi.hIcon != IntPtr.Zero)
                {
                    var bmp = Imaging.CreateBitmapSourceFromHIcon(shfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
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

        private InstalledApp SelectedApp() { return InstalledList.SelectedItem as InstalledApp; }

        private void StartSelected()
        {
            var app = SelectedApp();
            if (app == null) return;

            if (!string.IsNullOrEmpty(app.ExePath) && File.Exists(app.ExePath))
            {
                try { Process.Start(new ProcessStartInfo(app.ExePath) { UseShellExecute = true }); }
                catch (Exception ex) { AppendLog("启动失败: " + ex.Message); }
            }
            else AppendLog("找不到启动程序: " + app.Name);
        }

        private void OpenFolderSelected()
        {
            var app = SelectedApp();
            if (app == null) return;

            string dir = app.Location;
            if (string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(app.ExePath)) dir = Path.GetDirectoryName(app.ExePath);

            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                try { Process.Start("explorer.exe", "\"" + dir + "\""); }
                catch (Exception ex) { AppendLog("打开目录失败: " + ex.Message); }
            }
            else AppendLog("找不到安装目录: " + app.Name);
        }

        private void UninstallSelected()
        {
            var app = SelectedApp();
            if (app == null) return;
            if (string.IsNullOrEmpty(app.Uninstall)) { AppendLog("没有卸载命令: " + app.Name); return; }

            try { Process.Start(new ProcessStartInfo("cmd.exe", "/c " + app.Uninstall) { UseShellExecute = true }); }
            catch (Exception ex) { AppendLog("卸载启动失败: " + ex.Message); }
        }

        [DllImport("advapi32.dll", EntryPoint = "RegQueryInfoKeyW", CharSet = CharSet.Unicode)]
        private static extern int RegQueryInfoKey(
            IntPtr hKey, IntPtr lpClass, IntPtr lpcchClass, IntPtr lpReserved,
            out uint lpcSubKeys, out uint lpcbMaxSubKeyLen, out uint lpcbMaxClassLen,
            out uint lpcValues, out uint lpcbMaxValueNameLen, out uint lpcbMaxValueLen,
            out uint lpcbSecurityDescriptor, out long lpftLastWriteTime);

        private static DateTime? GetKeyWriteTime(RegistryKey key)
        {
            try
            {
                long ft;
                uint a, b, c, d, e, f, g;
                int rc = RegQueryInfoKey(key.Handle.DangerousGetHandle(), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                                         out a, out b, out c, out d, out e, out f, out g, out ft);
                if (rc != 0 || ft <= 0) return null;
                return DateTime.FromFileTimeUtc(ft).ToLocalTime();
            }
            catch { return null; }
        }

        private static bool IsVCRedist(string name)
        {
            return name.IndexOf("Visual C++", StringComparison.OrdinalIgnoreCase) >= 0;
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
                        if (IsVCRedist(displayName)) continue;

                        string version = sub.GetValue("DisplayVersion") as string ?? "";
                        string displayIcon = sub.GetValue("DisplayIcon") as string ?? "";
                        string uninstall = sub.GetValue("QuietUninstallString") as string;
                        if (string.IsNullOrWhiteSpace(uninstall)) uninstall = sub.GetValue("UninstallString") as string ?? "";
                        string location = sub.GetValue("InstallLocation") as string ?? "";

                        string key = displayName + "|" + version;
                        if (!seen.Add(key)) continue;

                        DateTime? installed = GetKeyWriteTime(sub);
                        string installDate = sub.GetValue("InstallDate") as string;
                        if (!installed.HasValue && !string.IsNullOrWhiteSpace(installDate) && installDate.Length == 8)
                        {
                            DateTime parsed;
                            if (DateTime.TryParseExact(installDate, "yyyyMMdd", null,
                                    System.Globalization.DateTimeStyles.None, out parsed))
                                installed = parsed;
                        }

                        list.Add(new InstalledApp
                        {
                            Name = displayName.Trim(),
                            Version = version,
                            ExePath = ExtractExePath(displayIcon),
                            Uninstall = uninstall,
                            Location = location,
                            InstallTime = installed
                        });
                    }
                    catch { }
                    finally { if (sub != null) sub.Dispose(); }
                }
            }

            DateTime now = DateTime.Now;
            foreach (var a in list)
                a.NewMark = (a.InstallTime.HasValue && (now - a.InstallTime.Value).TotalHours < 1.0)
                    ? Visibility.Visible : Visibility.Collapsed;

            return list.OrderByDescending(a => a.InstallTime ?? DateTime.MinValue)
                       .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
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

        private static string FindInstallDir(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword)) return null;

            try
            {
                var apps = LoadInstalledApps();
                string k = keyword.Trim();

                var hit = apps.FirstOrDefault(a => a.Name.Equals(k, StringComparison.OrdinalIgnoreCase))
                       ?? apps.FirstOrDefault(a => a.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

                if (hit == null) return null;

                if (!string.IsNullOrEmpty(hit.Location) && Directory.Exists(hit.Location)) return hit.Location;

                if (!string.IsNullOrEmpty(hit.ExePath))
                {
                    string d = Path.GetDirectoryName(hit.ExePath);
                    if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) return d;
                }
            }
            catch { }

            return null;
        }

        // ================= 进度与 ETA =================

        private void ProgressTimer_Tick(object sender, EventArgs e)
        {
            if (_itemCount <= 0) return;

            double elapsed = (DateTime.Now - _itemStart).TotalSeconds;
            double avg = Math.Max(12.0, _avgItemSeconds);
            double frac = Math.Min(0.92, elapsed / avg);

            double overall = ((_itemIndex + frac) / _itemCount) * 100.0;
            Progress.IsIndeterminate = false;
            Progress.Value = Math.Max(0, Math.Min(100, overall));

            double remain = Math.Max(0, avg - elapsed) + Math.Max(0, _itemCount - _itemIndex - 1) * avg;
            EtaText.Text = "预计剩余 " + FormatSpan(remain);
        }

        private static string FormatSpan(double seconds)
        {
            if (seconds < 1) return "0 秒";
            int s = (int)Math.Round(seconds);
            if (s < 60) return s + " 秒";
            int m = s / 60; int r = s % 60;
            return r == 0 ? m + " 分" : m + " 分 " + r + " 秒";
        }

        private void BeginProgress(int total)
        {
            _itemCount = Math.Max(1, total);
            _itemIndex = 0;
            _itemStart = DateTime.Now;
            _progressTimer.Start();
        }

        private void EndProgress()
        {
            _progressTimer.Stop();
            EtaText.Text = "";
        }

        // ================= 检查匹配 =================

        private ItemResult StartWebSearch(string term, string status)
        {
            var item = new ItemResult { Term = term, Status = status };
            lock (_results) _results.Add(item);
            Dispatcher.Invoke(delegate { RebuildWebPanel(); });

            var local = item;
            Task.Run(() => SearchWeb(term)).ContinueWith(t =>
            {
                local.Hits = SafeWait(t);
                Dispatcher.Invoke(delegate
                {
                    if (local.Status == "检索中") local.Status = "已检索";
                    RebuildWebPanel();
                });
            });

            return item;
        }

        private void PreviewButton_Click(object sender, RoutedEventArgs e)
        {
            if (!IsAdministrator()) return;

            var terms = ParseInput();
            if (terms.Count == 0) return;

            SuggestionPopup.IsOpen = false;
            SearchPopup.IsOpen = false;
            _stopRequested = false;
            PreviewButton.IsEnabled = false;
            StartButton.IsEnabled = false;
            ClearResults();

            AppendLog(_llm == null ? "提示: 未配置大模型" : "提示: 已启用大模型（" + _llm.Model + "）");

            Task.Run(() =>
            {
                var plan = new List<PlanItem>();
                for (int i = 0; i < terms.Count; i++)
                {
                    if (_stopRequested) break;
                    string t = terms[i];
                    SetProgress(i * 100.0 / terms.Count, "解析: " + t);
                    AppendLog("");
                    AppendLog(string.Format("解析 ({0}/{1}): {2}", i + 1, terms.Count, t));

                    var p = ResolvePlanItem(t);
                    plan.Add(p);

                    ItemResult item;
                    if (p.Kind == PlanKind.Package && p.Pick != null)
                    {
                        item = new ItemResult { Term = t, Status = "已解析", PackageId = p.Pick.Id, PackageName = p.Pick.Name };
                        lock (_results) _results.Add(item);
                        AppendLog("    ✔ 已确定安装包，跳过网页检索");
                        Dispatcher.Invoke(delegate { RebuildWebPanel(); });
                    }
                    else
                    {
                        AppendLog("    → 未确定安装包，开始网页检索");
                        item = StartWebSearch(t, "检索中");
                        if (p.Kind == PlanKind.None) item.Status = "无来源";
                    }

                    Dispatcher.Invoke(delegate { RebuildWebPanel(); });
                }

                Dispatcher.Invoke(delegate
                {
                    PreviewButton.IsEnabled = true;
                    StartButton.IsEnabled = true;
                    SetProgress(0, "解析完成");
                    ShowPlanDialog(plan);
                });
            });
        }

        private PlanItem ResolvePlanItem(string term)
        {
            if (IsUrl(term)) return new PlanItem { Term = term, Kind = PlanKind.Url };

            var pick = ResolveTerm(term);
            if (pick != null) return new PlanItem { Term = term, Kind = PlanKind.Package, Pick = pick };

            bool isDriver = LooksLikeDriver(term);
            AppendLog(isDriver ? "    → 按驱动处理" : "    → 查询官方网站");

            string siteName, url, supportUrl;
            AskOfficialSite(term, isDriver, out siteName, out url, out supportUrl);

            var item = new PlanItem { Term = term, Kind = PlanKind.Link, SiteName = siteName, Url = url, SupportUrl = supportUrl };

            if (isDriver)
            {
                item.AlternateName = "微软更新目录";
                item.AlternateUrl = "https://www.catalog.update.microsoft.com/Search.aspx?q=" + Uri.EscapeDataString(term);
            }

            if (string.IsNullOrEmpty(url) && string.IsNullOrEmpty(supportUrl) && string.IsNullOrEmpty(item.AlternateUrl))
            {
                item.Kind = PlanKind.None;
                AppendLog("    ❌ 未找到来源");
            }
            else
            {
                if (!string.IsNullOrEmpty(url)) AppendLog("    → 下载页: " + url);
                if (!string.IsNullOrEmpty(supportUrl)) AppendLog("    → 支持页: " + supportUrl);
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
                Width = 860,
                Height = 580,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = MakeBrush("#1B1C20")
            };

            var root = new DockPanel { Margin = new Thickness(18) };

            var header = new TextBlock
            {
                Text = "请核对每个输入对应的目标，确认后点「确认并执行」。",
                Foreground = MakeBrush("#9AA0A8"),
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 12)
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
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
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var left = new TextBlock { Text = p.Term, Foreground = MakeBrush("#C8CDD4"), FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Top };
                Grid.SetColumn(left, 0);
                row.Children.Add(left);

                var right = new StackPanel();
                Grid.SetColumn(right, 1);

                if (p.Kind == PlanKind.Package)
                {
                    right.Children.Add(new TextBlock { Text = p.Pick.Name + "    " + p.Pick.Id, Foreground = MakeBrush("#E4E6EA"), FontSize = 13, TextWrapping = TextWrapping.Wrap });
                    right.Children.Add(new TextBlock { Text = "来源: " + (string.IsNullOrEmpty(p.Pick.Source) ? "winget" : p.Pick.Source), Foreground = MakeBrush("#7C828C"), FontSize = 11, Margin = new Thickness(0, 2, 0, 0) });
                }
                else if (p.Kind == PlanKind.Url)
                {
                    right.Children.Add(new TextBlock { Text = "直链下载", Foreground = MakeBrush("#E4E6EA"), FontSize = 13 });
                }
                else if (p.Kind == PlanKind.None)
                {
                    right.Children.Add(new TextBlock { Text = "✗ 未找到可用来源", Foreground = MakeBrush("#FF6B6B"), FontSize = 13 });

                    var bing = MakeLink("🔍 必应搜索「" + p.Term + "」",
                        "https://www.bing.com/search?q=" + Uri.EscapeDataString(p.Term));
                    bing.Margin = new Thickness(0, 4, 0, 0);
                    right.Children.Add(bing);

                    var baidu = MakeLink("🔍 百度搜索「" + p.Term + "」",
                        "https://www.baidu.com/s?wd=" + Uri.EscapeDataString(p.Term));
                    baidu.Margin = new Thickness(0, 4, 0, 0);
                    right.Children.Add(baidu);
                }
                else
                {
                    if (!string.IsNullOrEmpty(p.Url))
                    {
                        right.Children.Add(MakeLink("下载页  " + p.Url, p.Url));
                        if (IsHomepage(p.Url))
                            right.Children.Add(new TextBlock { Text = "⚠ 这是厂商首页", Foreground = MakeBrush("#E0A030"), FontSize = 11, Margin = new Thickness(0, 2, 0, 0) });
                    }
                    if (!string.IsNullOrEmpty(p.SupportUrl))
                    {
                        var sp = MakeLink("支持页  " + p.SupportUrl, p.SupportUrl);
                        sp.Margin = new Thickness(0, 4, 0, 0);
                        right.Children.Add(sp);
                    }
                    if (!string.IsNullOrEmpty(p.AlternateUrl))
                    {
                        var alt = MakeLink(p.AlternateName + "  " + p.AlternateUrl, p.AlternateUrl);
                        alt.Margin = new Thickness(0, 4, 0, 0);
                        right.Children.Add(alt);
                    }

                    var bing = MakeLink("🔍 必应搜索「" + p.Term + "」",
                        "https://www.bing.com/search?q=" + Uri.EscapeDataString(p.Term));
                    bing.Margin = new Thickness(0, 6, 0, 0);
                    right.Children.Add(bing);
                }

                row.Children.Add(right);
                panel.Children.Add(row);
                panel.Children.Add(new Border { Height = 1, Background = MakeBrush("#2F3238") });
            }

            root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel });
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
                try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
                e.Handled = true;
            };
            tb.Inlines.Add(link);
            return tb;
        }

        private static Button MakeDialogButton(string text, string bg, string fg, double width)
        {
            return new Button { Content = text, Width = width, Height = 34, Background = MakeBrush(bg), Foreground = MakeBrush(fg), BorderThickness = new Thickness(0) };
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

        // ================= 执行 =================

        private void RunPlan(List<PlanItem> plan)
        {
            _stopRequested = false;
            StartButton.IsEnabled = false;
            PreviewButton.IsEnabled = false;
            ClearResults();

            Task.Run(() =>
            {
                int ok = 0, fail = 0;
                BeginProgress(plan.Count);

                for (int i = 0; i < plan.Count; i++)
                {
                    if (_stopRequested) { AppendLog("⏹ 已停止"); break; }

                    var p = plan[i];
                    _itemIndex = i;
                    _itemStart = DateTime.Now;

                    ItemResult item;
                    if (p.Kind == PlanKind.Package && p.Pick != null)
                    {
                        item = new ItemResult { Term = p.Term, Status = "执行中", PackageId = p.Pick.Id, PackageName = p.Pick.Name };
                        lock (_results) _results.Add(item);
                        Dispatcher.Invoke(delegate { RebuildWebPanel(); });
                    }
                    else
                    {
                        item = StartWebSearch(p.Term, "执行中");
                    }

                    bool done = false;
                    if (p.Kind == PlanKind.Url) done = InstallFromUrl(p.Term);
                    else if (p.Kind == PlanKind.Package) done = InstallPackage(p.Pick, item);
                    else if (p.Kind == PlanKind.Link)
                    {
                        string target = !string.IsNullOrEmpty(p.SupportUrl) ? p.SupportUrl : (!string.IsNullOrEmpty(p.Url) ? p.Url : p.AlternateUrl);
                        try
                        {
                            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                            AppendLog("    → 已打开: " + target);
                            done = true;
                        }
                        catch (Exception ex) { AppendLog("    ❌ 打开失败: " + ex.Message); }
                    }

                    item.Status = done ? "成功" : "失败";
                    item.Ok = done;

                    UpdateAvg((DateTime.Now - _itemStart).TotalSeconds);

                    if (done) ok++; else fail++;
                    Dispatcher.Invoke(delegate { RebuildWebPanel(); });
                }

                EndProgress();
                AppendLog("\r\n========================================");
                AppendLog(string.Format("全部完成！成功 {0}，失败 {1}", ok, fail));

                Dispatcher.Invoke(delegate
                {
                    StartButton.IsEnabled = true;
                    PreviewButton.IsEnabled = true;
                    LoadInstalledAsync();
                });
            });
        }

        private List<WebHit> SafeWait(Task<List<WebHit>> t)
        {
            try { return t.Result; }
            catch { return new List<WebHit>(); }
        }

        private void UpdateAvg(double seconds)
        {
            if (seconds <= 0 || seconds > 900) return;
            _avgItemSeconds = _avgItemSeconds * 0.7 + seconds * 0.3;
        }

        private List<string> ParseInput()
        {
            return InputTextBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith("#")).ToList();
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (!IsAdministrator()) return;

            var list = ParseInput();
            if (list.Count == 0) return;

            SuggestionPopup.IsOpen = false;
            SearchPopup.IsOpen = false;
            _stopRequested = false;
            StartButton.IsEnabled = false;
            PreviewButton.IsEnabled = false;
            ClearResults();

            AppendLog(_llm == null ? "提示: 未配置大模型" : "提示: 已启用大模型（" + _llm.Model + "）");

            Task.Run(() =>
            {
                int ok = 0, fail = 0;
                BeginProgress(list.Count);

                for (int i = 0; i < list.Count; i++)
                {
                    if (_stopRequested) { AppendLog("⏹ 已停止"); break; }

                    string term = list[i];
                    _itemIndex = i;
                    _itemStart = DateTime.Now;

                    var item = StartWebSearch(term, "安装中");

                    AppendLog("\r\n========================================");
                    AppendLog(string.Format("[{0}/{1}] {2}", i + 1, list.Count, term));

                    bool done = InstallOne(term, item);

                    item.Status = done ? "成功" : "失败";
                    item.Ok = done;

                    UpdateAvg((DateTime.Now - _itemStart).TotalSeconds);

                    if (done) ok++; else fail++;
                    Dispatcher.Invoke(delegate { RebuildWebPanel(); });
                }

                EndProgress();
                AppendLog("\r\n========================================");
                AppendLog(string.Format("全部完成！成功 {0}，失败 {1}", ok, fail));

                Dispatcher.Invoke(delegate
                {
                    StartButton.IsEnabled = true;
                    PreviewButton.IsEnabled = true;
                    LoadInstalledAsync();
                });
            });
        }

        private bool InstallOne(string term, ItemResult item)
        {
            if (IsUrl(term)) return InstallFromUrl(term);

            var pick = ResolveTerm(term);
            if (pick != null && InstallPackage(pick, item)) return true;

            AppendLog("  [2/3] Chocolatey");
            if (TryChoco(term)) return true;

            AppendLog("  [3/3] Scoop");
            if (TryScoop(term)) return true;

            bool isDriver = LooksLikeDriver(term);
            string siteName, url, supportUrl;
            AskOfficialSite(term, isDriver, out siteName, out url, out supportUrl);

            string target = !string.IsNullOrEmpty(supportUrl) ? supportUrl : url;
            if (!string.IsNullOrEmpty(target))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                    AppendLink("→ 打开官方页面: " + target, target);
                    return true;
                }
                catch (Exception ex) { AppendLog("  打开链接失败: " + ex.Message); }
            }

            AppendLog("  ❌ 所有来源都未能安装");
            return false;
        }

        private bool InstallPackage(Candidate pick, ItemResult item)
        {
            SetIndeterminate("安装 " + pick.Id);

            if (item != null)
            {
                item.PackageId = pick.Id;
                item.PackageName = pick.Name;
            }

            if (RunProcess("winget", "install --id \"" + pick.Id + "\" --exact --silent --accept-package-agreements --accept-source-agreements", out string installOut))
            {
                AppendLog("    ✅ 安装成功: " + pick.Id);

                string dir = FindInstallDir(pick.Name);
                if (string.IsNullOrEmpty(dir)) dir = FindInstallDir(pick.Id.Split('.').Last());

                if (!string.IsNullOrEmpty(dir))
                {
                    if (item != null) item.InstallDir = dir;
                    AppendLog("    📁 安装位置: " + dir);
                    AppendLink("     打开目录: " + dir, dir);
                }
                else
                {
                    AppendLog("    📁 未能自动定位安装目录，可在右侧已安装列表中右键查看");
                }

                return true;
            }

            AppendLog("    winget 安装失败：");
            AppendLastLines(installOut, 6);
            return false;
        }

        // ================= 解析 =================

        private Candidate ResolveTerm(string term)
        {
            AppendLog("    [1] 本地名录检索");
            var local = SearchCatalog(term, 5);
            if (local.Count > 0)
            {
                var best = local[0];
                if (RankEntry(best, Normalize(term)) >= 86)
                {
                    AppendLog("    ✅ 名录命中: " + best.Name + " (" + best.Id + ") —— 中断联网检索");
                    return new Candidate { Name = best.Name, Id = best.Id, Source = "本地名录" };
                }
                AppendLog("    ≈ 名录仅有相近项: " + best.Name + "，不算命中");
            }
            else
            {
                AppendLog("    ✗ 本地名录无匹配");
            }

            if (LooksLikeId(term) && VerifyPackageId(term, out string idName))
                return new Candidate { Name = idName, Id = term, Source = "ID" };

            var queries = BuildQueries(term);
            var all = SearchAll(queries);

            if (all.Count == 0)
            {
                foreach (var n in AskOfficialNames(term))
                {
                    var hit = SearchCatalog(n, 1);
                    if (hit.Count > 0 && RankEntry(hit[0], Normalize(n)) >= 86)
                    {
                        AppendLog("    ✅ AI 名称命中: " + n);
                        return new Candidate { Name = hit[0].Name, Id = hit[0].Id, Source = "AI 名称" };
                    }
                }

                var inferred = ResolvePackageId(term);
                if (inferred != null) return inferred;
            }

            if (all.Count == 0) return null;

            foreach (var c in all)
            {
                double s = Math.Max(Score(term, c.Name), Score(term, c.Id) * 0.6);
                foreach (var q in queries.Skip(1))
                    s = Math.Max(s, Math.Max(Score(q, c.Name) * 0.95, Score(q, c.Id) * 0.6));
                c.MatchScore = s;
            }

            var ordered = all.OrderByDescending(c => c.MatchScore).Take(6).ToList();

            int aiIdx = AskBestIndex(term, ordered);
            return aiIdx >= 0 ? ordered[aiIdx] : ordered[0];
        }

        private List<Candidate> SearchAll(List<string> queries)
        {
            var all = new List<Candidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var q in queries)
            {
                if (_stopRequested) break;
                AppendLog("    → 搜索: " + q);
                SetIndeterminate("搜索: " + q);
                RunProcess("winget", "search \"" + q + "\" --accept-source-agreements", out string so);

                foreach (var c in ParseWinget(so))
                    if (seen.Add(c.Id)) all.Add(c);
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

                bool hit = k == kk || (k.Length >= 2 && k.Contains(kk)) || (kk.Length >= 2 && kk.Contains(k));
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
                if (t.StartsWith("-") || t.Length < 3) continue;
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
            if (string.IsNullOrEmpty(s) || s.Contains(" ")) return false;
            if (Regex.IsMatch(s, @"^[0-9A-Z]{12}$")) return true;
            return s.Contains(".") && Regex.IsMatch(s, @"^[A-Za-z0-9][A-Za-z0-9_\-\.]*$");
        }

        // ================= Choco / Scoop / 下载 =================

        private bool TryChoco(string term)
        {
            if (!HasCommand("choco")) { AppendLog("    未检测到 Chocolatey"); return false; }

            string key = Regex.Replace(term, @"\s+", "");
            if (!Regex.IsMatch(key, @"^[A-Za-z0-9_\-\.\+]+$")) { AppendLog("    Chocolatey 只接受英文包名"); return false; }

            SetIndeterminate("Chocolatey 搜索 " + key);
            if (!RunProcess("choco", "search " + key + " --limit-output --exact", out string s))
                RunProcess("choco", "search " + key + " --limit-output", out s);

            var names = ParseChoco(s);
            if (names.Count == 0) return false;

            string pick = names.FirstOrDefault(n => string.Equals(n, key, StringComparison.OrdinalIgnoreCase)) ?? names[0];
            SetIndeterminate("Chocolatey 安装 " + pick);
            if (RunProcess("choco", "install " + pick + " -y --no-progress --limit-output", out string o))
            {
                AppendLog("    ✅ 成功: " + pick);
                return true;
            }
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
                if (name.Length > 0 && Regex.IsMatch(name, @"^[A-Za-z0-9_\-\.\+]+$")) list.Add(name);
            }
            return list;
        }

        private bool TryScoop(string term)
        {
            if (!HasCommand("scoop")) { AppendLog("    未检测到 Scoop"); return false; }

            string key = Regex.Replace(term, @"\s+", "");
            if (!Regex.IsMatch(key, @"^[A-Za-z0-9_\-\.]+$")) { AppendLog("    Scoop 只接受英文包名"); return false; }

            SetIndeterminate("Scoop 安装 " + key);
            if (RunProcess("scoop", "install " + key, out string o))
            {
                AppendLog("    ✅ 成功");
                return true;
            }
            AppendLastLines(o, 4);
            return false;
        }

        private static bool IsUrl(string s)
        {
            return s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        private bool InstallFromUrl(string url)
        {
            try
            {
                string fileName = Path.GetFileName(new Uri(url).AbsolutePath);
                if (string.IsNullOrWhiteSpace(fileName)) fileName = "setup.exe";
                string target = Path.Combine(Path.GetTempPath(), fileName);

                using (var wc = new WebClient())
                {
                    _currentWebClient = wc;
                    wc.Headers.Add("User-Agent", "Mozilla/5.0");
                    var tcs = new TaskCompletionSource<bool>();

                    wc.DownloadProgressChanged += delegate (object s, DownloadProgressChangedEventArgs ev)
                    {
                        SetProgress(ev.ProgressPercentage, string.Format("下载 {0}% ({1:0.0}/{2:0.0} MB)",
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

                if (target.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                    Process.Start(new ProcessStartInfo("msiexec", "/i \"" + target + "\""));
                else
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

                AppendLog("  ✅ 安装程序已启动");
                return true;
            }
            catch (Exception ex)
            {
                AppendLog("  ❌ 下载失败: " + ex.Message);
                return false;
            }
        }

        // ================= 右侧检索面板 =================

        private void ClearResults()
        {
            lock (_results) _results.Clear();
            RebuildWebPanel();
        }

        private void RebuildWebPanel()
        {
            WebPanel.Children.Clear();

            List<ItemResult> snapshot;
            lock (_results) snapshot = _results.ToList();

            if (snapshot.Count == 0)
            {
                WebPanel.Children.Add(new TextBlock
                {
                    Text = "检查匹配与安装时都会并行检索官网，结果显示在这里。",
                    Foreground = MakeBrush("#6B7280"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            foreach (var r in snapshot)
            {
                var card = new Border
                {
                    Background = MakeBrush("#1F2126"),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8),
                    Margin = new Thickness(0, 0, 0, 8)
                };

                var sp = new StackPanel();

                string mark = r.Ok ? "✓" : (r.Status == "执行中" || r.Status == "安装中" ? "…" : "✗");
                var head = new TextBlock
                {
                    Text = mark + " " + r.Term,
                    Foreground = MakeBrush(r.Ok ? "#3DDC84" : (r.Status == "失败" ? "#FF6B6B" : "#E4E6EA")),
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                };
                sp.Children.Add(head);

                if (!string.IsNullOrEmpty(r.PackageId))
                {
                    sp.Children.Add(new TextBlock
                    {
                        Text = r.PackageId,
                        Foreground = MakeBrush("#7C828C"),
                        FontSize = 10,
                        TextWrapping = TextWrapping.Wrap
                    });
                }

                if (!string.IsNullOrEmpty(r.InstallDir))
                {
                    var lk = MakeLink("📁 " + r.InstallDir, r.InstallDir);
                    lk.Margin = new Thickness(0, 3, 0, 0);
                    sp.Children.Add(lk);
                }

                if (r.Hits != null && r.Hits.Count > 0)
                {
                    foreach (var h in r.Hits.Take(3))
                    {
                        var lk = MakeLink((h.Trusted ? "★ " : "· ") + h.Host, h.Url);
                        lk.Margin = new Thickness(0, 3, 0, 0);
                        sp.Children.Add(lk);
                    }
                }

                if (r.Status != "检索中")
                {
                    var bing = MakeLink("🔍 必应搜索「" + r.Term + "」",
                        "https://www.bing.com/search?q=" + Uri.EscapeDataString(r.Term));
                    bing.Margin = new Thickness(0, 3, 0, 0);
                    sp.Children.Add(bing);

                    if (r.Hits == null || r.Hits.Count == 0)
                    {
                        var baidu = MakeLink("🔍 百度搜索「" + r.Term + "」",
                            "https://www.baidu.com/s?wd=" + Uri.EscapeDataString(r.Term));
                        baidu.Margin = new Thickness(0, 3, 0, 0);
                        sp.Children.Add(baidu);
                    }
                }

                card.Child = sp;
                WebPanel.Children.Add(card);
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
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

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
                    _currentProc = p;
                    string stdout = "";
                    string stderr = "";
                    var t1 = Task.Run(delegate { stdout = p.StandardOutput.ReadToEnd(); });
                    var t2 = Task.Run(delegate { stderr = p.StandardError.ReadToEnd(); });
                    p.WaitForExit();
                    Task.WaitAll(t1, t2);
                    output = (stdout + "\r\n" + stderr).Trim();
                    _currentProc = null;
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                _currentProc = null;
                output = ex.Message;
                return false;
            }
        }

        private void AppendLastLines(string text, int count)
        {
            if (string.IsNullOrEmpty(text)) return;

            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(CleanLine).Where(l => l.Length > 0).ToList();

            int start = Math.Max(0, lines.Count - count);
            for (int i = start; i < lines.Count; i++) AppendLog("       " + lines[i]);
        }

        private void AppendLog(string text)
        {
            Dispatcher.Invoke(delegate
            {
                var p = new Paragraph(new Run(text)) { Margin = new Thickness(0) };
                LogBox.Document.Blocks.Add(p);
                LogBox.ScrollToEnd();
            });
        }

        private void AppendLink(string label, string url)
        {
            Dispatcher.Invoke(delegate
            {
                var p = new Paragraph { Margin = new Thickness(0) };
                var link = new Hyperlink(new Run(label)) { Foreground = MakeBrush("#4C8DFF") };
                if (!IsUrl(url)) url = "file:///" + url.Replace("\\", "/");
                Uri uri;
                if (Uri.TryCreate(url, UriKind.Absolute, out uri)) link.NavigateUri = uri;
                link.RequestNavigate += delegate (object s, RequestNavigateEventArgs e)
                {
                    try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
                    e.Handled = true;
                };
                p.Inlines.Add(link);
                LogBox.Document.Blocks.Add(p);
                LogBox.ScrollToEnd();
            });
        }

        private static bool IsAdministrator()
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (_stopRequested) return;
            _stopRequested = true;
            AppendLog("⏹ 正在强制中止…");

            try
            {
                var wc = _currentWebClient;
                if (wc != null) wc.CancelAsync();
            }
            catch { }

            try
            {
                var pr = _currentProc;
                if (pr != null && !pr.HasExited)
                {
                    AppendLog("   正在结束进程: " + pr.ProcessName + " (PID " + pr.Id + ")");
                    Process.Start(new ProcessStartInfo("taskkill", "/PID " + pr.Id + " /T /F")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                }
            }
            catch (Exception ex) { AppendLog("   结束进程失败: " + ex.Message); }

            EndProgress();
            StartButton.IsEnabled = true;
            PreviewButton.IsEnabled = true;
            Progress.IsIndeterminate = false;
            Progress.Value = 0;
            StatusText.Text = "已中止";
            AppendLog("⏹ 已中止：后续项目不再执行");
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            LogBox.Document.Blocks.Clear();
            LogBox.Document.Blocks.Add(new Paragraph(new Run("日志已清空")) { Margin = new Thickness(0) });
            ClearResults();
        }
    }
}
