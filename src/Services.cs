using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Zecole.Launcher
{
    internal static class Shell
    {
        public static void OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http")) return;
            try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("open-url", ex); }
        }

        public static void OpenFolder(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (File.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else if (Directory.Exists(path)) Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception ex) { Log.Error("open-folder", ex); }
        }
    }

    /// <summary>
    /// HTTP. 런처가 접속하는 주소는 아래 허용 목록으로 제한.
    /// GitHub API 는 비로그인 시간당 60회 제한이 있어 ETag 조건부 요청(304 는 횟수에 안 들어감)과 디스크 캐시를 씀.
    /// </summary>
    internal static class Net
    {
        public const string UserAgent = "ZECOLE-Launcher/1.0";
        private static readonly string[] AllowedHosts =
        {
            "api.github.com", "raw.githubusercontent.com", "zecole.store", "www.zecole.store", "www.youtube.com",
        };

        private static readonly HttpClient Client = Make(TimeSpan.FromSeconds(20));
        public static readonly HttpClient Downloader = Make(Timeout.InfiniteTimeSpan);
        private static readonly string CacheDir = Path.Combine(Program.DataDir, "cache");

        private static HttpClient Make(TimeSpan timeout)
        {
            var h = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate, AllowAutoRedirect = true };
            var c = new HttpClient(h) { Timeout = timeout };
            c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return c;
        }

        public static void EnsureAllowed(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https" || !AllowedHosts.Contains(u.Host.ToLowerInvariant()))
                throw new InvalidOperationException("허용되지 않은 주소: " + url);
        }

        private sealed class Entry { public string etag; public string body; public int status; public DateTime at; }

        public static async Task<Dictionary<string, object>> GetTextAsync(string url, int maxAgeSec)
        {
            EnsureAllowed(url);
            var file = Path.Combine(CacheDir, Hash(url) + ".json");
            Entry cached = null;
            try { if (File.Exists(file)) cached = Bridge.Json.Deserialize<Entry>(File.ReadAllText(file, Encoding.UTF8)); } catch { }

            if (cached != null && maxAgeSec > 0 && (DateTime.UtcNow - cached.at).TotalSeconds < maxAgeSec)
                return Result(cached, true, false);

            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (cached?.etag != null) req.Headers.TryAddWithoutValidation("If-None-Match", cached.etag);
                if (url.Contains("api.github.com")) req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                using (var res = await Client.SendAsync(req))
                {
                    if (res.StatusCode == HttpStatusCode.NotModified && cached != null)
                    {
                        cached.at = DateTime.UtcNow; Save(file, cached);
                        return Result(cached, true, false);
                    }
                    var code = (int)res.StatusCode;
                    // 403/429 = GitHub 요청 한도 초과 → 마지막 값 사용
                    if ((code == 403 || code == 429 || code >= 500) && cached != null) return Result(cached, true, true);
                    var body = await res.Content.ReadAsStringAsync();
                    var e = new Entry { etag = res.Headers.ETag?.ToString(), body = code == 200 ? body : null, status = code, at = DateTime.UtcNow };
                    if (code == 200 || code == 404) Save(file, e);
                    return Result(e, false, false);
                }
            }
            catch (Exception ex) when (cached != null)
            {
                Log.Info("offline " + url + " " + ex.Message);
                return Result(cached, true, true);
            }
        }

        private static Dictionary<string, object> Result(Entry e, bool fromCache, bool stale) => new Dictionary<string, object>
        {
            ["status"] = e.status, ["body"] = e.body, ["cached"] = fromCache, ["stale"] = stale, ["at"] = e.at.ToString("o"),
        };

        private static void Save(string file, Entry e)
        {
            try { Directory.CreateDirectory(CacheDir); File.WriteAllText(file, Bridge.Json.Serialize(e), Encoding.UTF8); } catch { }
        }

        public static string Hash(string s)
        {
            using (var sha = SHA1.Create()) return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(s)).Select(b => b.ToString("x2")));
        }

        public static async Task DownloadFileAsync(string url, string path, Action<long, long> progress, CancellationToken ct = default)
        {
            using (var res = await Downloader.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                res.EnsureSuccessStatusCode();
                var total = res.Content.Headers.ContentLength ?? -1;
                var tmp = path + ".part";
                using (var src = await res.Content.ReadAsStreamAsync())
                using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
                {
                    var buf = new byte[1 << 16];
                    long done = 0; int n; var last = 0L;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length, ct)) > 0)
                    {
                        await dst.WriteAsync(buf, 0, n, ct);
                        done += n;
                        if (done - last > 256 * 1024 || done == total) { last = done; progress?.Invoke(done, total); }
                    }
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
        }

        public static string Sha256File(string path)
        {
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(path))
                return string.Concat(sha.ComputeHash(f).Select(b => b.ToString("x2")));
        }
    }

    /// <summary>유튜브 채널 RSS → 최신 영상 목록 (API 키 불필요)</summary>
    internal static class YouTube
    {
        public static async Task<object> LatestAsync(string channelId)
        {
            if (string.IsNullOrEmpty(channelId) || !Regex.IsMatch(channelId, "^UC[A-Za-z0-9_-]{22}$")) return new object[0];
            var r = await Net.GetTextAsync("https://www.youtube.com/feeds/videos.xml?channel_id=" + channelId, 1800);
            var body = r["body"] as string;
            if (string.IsNullOrEmpty(body)) return new object[0];
            XNamespace atom = "http://www.w3.org/2005/Atom", yt = "http://www.youtube.com/xml/schemas/2015", media = "http://search.yahoo.com/mrss/";
            var doc = XDocument.Parse(body);
            return doc.Root.Elements(atom + "entry").Take(15).Select(e => new Dictionary<string, object>
            {
                ["id"] = (string)e.Element(yt + "videoId"),
                ["title"] = (string)e.Element(atom + "title"),
                ["published"] = (string)e.Element(atom + "published"),
                ["views"] = (string)e.Element(media + "group")?.Element(media + "community")?.Element(media + "statistics")?.Attribute("views"),
            }).ToArray();
        }
    }

    /// <summary>이 PC 에 설치된 Steam 게임 (한글패치 추천에 사용)</summary>
    internal static class Steam
    {
        public static Dictionary<string, object> InstalledApps()
        {
            var result = new Dictionary<string, object>();
            try
            {
                var root = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                        ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
                if (string.IsNullOrEmpty(root)) return result;
                root = root.Replace('/', '\\');
                var libs = new List<string> { root };
                var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));

                foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var apps = Path.Combine(lib, "steamapps");
                    if (!Directory.Exists(apps)) continue;
                    foreach (var acf in Directory.GetFiles(apps, "appmanifest_*.acf"))
                    {
                        var t = File.ReadAllText(acf);
                        var id = Regex.Match(t, "\"appid\"\\s+\"(\\d+)\"").Groups[1].Value;
                        var dir = Regex.Match(t, "\"installdir\"\\s+\"([^\"]+)\"").Groups[1].Value;
                        var name = Regex.Match(t, "\"name\"\\s+\"([^\"]+)\"").Groups[1].Value;
                        if (id.Length > 0) result[id] = new Dictionary<string, object> { ["name"] = name, ["path"] = Path.Combine(apps, "common", dir) };
                    }
                }
            }
            catch (Exception ex) { Log.Error("steam", ex); }
            return result;
        }
    }

    /// <summary>
    /// 각 게임 설치기가 남긴 기록으로 설치 버전 확인.
    /// 1) 표준: %LOCALAPPDATA%\Zecole\{stateDir}\zecole-status.json  ← 앞으로 만드는 설치기는 이 파일만 쓰면 됨
    /// 2) 게임별 규칙(detect): 기존 설치기의 기록 파일 경로와 JSON 키
    /// 3) 런처 실행 기록: 런처로 받아 실행한 설치기 버전
    /// </summary>
    internal static class Status
    {
        public static Dictionary<string, object> Scan(object[] items)
        {
            var records = Installer.LoadRecords();
            var result = new Dictionary<string, object>();
            foreach (Dictionary<string, object> it in items ?? new object[0])
            {
                var id = it["id"] as string;
                var repo = it.TryGetValue("repo", out var r) ? r as string : null;
                var stateDir = it.TryGetValue("stateDir", out var sd) ? sd as string : null;
                var o = new Dictionary<string, object> { ["usedBefore"] = false };
                try
                {
                    if (!string.IsNullOrEmpty(stateDir) && IsSafeRelative(stateDir))
                    {
                        var dir = Path.Combine(Program.ZecoleRoot, stateDir);
                        o["usedBefore"] = Directory.Exists(dir);
                        var std = Path.Combine(dir, "zecole-status.json");
                        if (File.Exists(std)) Fill(o, std, "patch_version|version", "state", "installed|ok", "standard");
                    }
                    if (!o.ContainsKey("installedVersion") && it.TryGetValue("detect", out var d) && d is object[] rules)
                    {
                        foreach (Dictionary<string, object> rule in rules)
                        {
                            var pattern = rule["path"] as string;
                            if (!IsSafeRelative(pattern)) continue;
                            var hit = Glob(Program.ZecoleRoot, pattern).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                            if (hit == null) continue;
                            o["usedBefore"] = true;
                            Fill(o, hit, rule["version"] as string, rule.TryGetValue("state", out var s) ? s as string : null,
                                rule.TryGetValue("ok", out var ok) ? ok as string : null, "detect");
                            if (o.ContainsKey("installedVersion")) break;
                        }
                    }
                }
                catch (Exception ex) { Log.Error("status " + id, ex); }
                if (repo != null && records.TryGetValue(repo, out var rec)) o["lastRun"] = rec;
                result[id] = o;
            }
            return result;
        }

        private static bool IsSafeRelative(string p) => !string.IsNullOrEmpty(p) && !Path.IsPathRooted(p) && !p.Contains("..");

        private static void Fill(Dictionary<string, object> o, string file, string versionKeys, string stateKey, string okValues, string source)
        {
            var json = Bridge.Json.DeserializeObject(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
            if (json == null) return;
            if (!string.IsNullOrEmpty(stateKey))
            {
                var st = Get(json, stateKey)?.ToString();
                o["state"] = st;
                if (st != null && okValues != null && !okValues.Split('|').Contains(st, StringComparer.OrdinalIgnoreCase)) return; // 제거·복구된 상태
            }
            foreach (var k in (versionKeys ?? "").Split('|'))
            {
                var v = Get(json, k)?.ToString();
                if (string.IsNullOrEmpty(v)) continue;
                o["installedVersion"] = Regex.Replace(v, @"^v\.?", "", RegexOptions.IgnoreCase);
                o["gameVersion"] = Get(json, "game_version")?.ToString();
                o["source"] = source;
                o["updatedAt"] = File.GetLastWriteTimeUtc(file).ToString("o");
                return;
            }
        }

        private static object Get(Dictionary<string, object> json, string dotted)
        {
            object cur = json;
            foreach (var part in dotted.Split('.'))
            {
                if (!(cur is Dictionary<string, object> d) || !d.TryGetValue(part, out cur)) return null;
            }
            return cur;
        }

        /// <summary>"CONVRGENCE/installations/*/*/installation.json" 형태의 간단한 와일드카드</summary>
        private static IEnumerable<string> Glob(string root, string pattern)
        {
            var segs = pattern.Replace('\\', '/').Split('/');
            IEnumerable<string> cur = new[] { root };
            for (int i = 0; i < segs.Length; i++)
            {
                var seg = segs[i];
                var last = i == segs.Length - 1;
                cur = cur.SelectMany(dir =>
                {
                    if (!Directory.Exists(dir)) return Enumerable.Empty<string>();
                    try { return last ? Directory.GetFiles(dir, seg) : Directory.GetDirectories(dir, seg); }
                    catch { return Enumerable.Empty<string>(); }
                }).ToList();
            }
            return cur;
        }
    }
}
