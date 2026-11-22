using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Zecole.Launcher.Quest
{
    /// <summary>
    /// Meta VR Glasses FoV Tester (구 Glasses FoV Lab) 설치·활성화.
    /// 릴리스 ZIP 의 scripts/install-one-click.ps1 · enable-quest-control.ps1 순서를 그대로 옮김.
    ///  - 설치: adb install -r → 앱 실행
    ///  - 다른 게임에 적용: 앱 개인 폴더의 연결 키 → Meta 공식 CLI 로 지원 확인(시야 값은 원래대로 되돌림)
    ///    → 보조 프로그램(classes.dex)·설정 올리고 백그라운드 실행. 재부팅하면 다시 켜야 함.
    /// 작업 잠금·기기 선택·무선 연결은 Quest 한글패치 엔진과 함께 씀.
    /// </summary>
    internal sealed partial class QuestService
    {
        private const string FovPkg = "com.glasseslab.fov";
        private const string FovActivity = "com.glasseslab.fov/com.unity3d.player.UnityPlayerGameActivity";
        private const string FovHelper = "app_process /system/bin com.glasseslab.bridge.FovServer /data/local/tmp/glasses-fov.config";
        private static readonly string[] FovProps = { "debug.oculus.eyeFovUp", "debug.oculus.eyeFovDown", "debug.oculus.eyeFovInward", "debug.oculus.eyeFovOutward", "debug.oculus.headsetOverride" };
        private static readonly string[] FovAngles = { "25", "41", "35", "35" };   // 앱(SDK 207 마스크)에 맞춘 비대칭 시야. 원본 스크립트와 같은 값
        private const string MetaCliId = "26548836238067195";
        private const string FovRepo = "glasses-fov-lab";   // 이 엔진은 이 저장소의 릴리스만 받음 (화면이 보낸 저장소 이름을 믿지 않음)
        private static string FovRoot => Path.Combine(Program.DataDir, "fovlab");
        private static string MetaCliDir => Path.Combine(Program.DataDir, "tools", "metavr");

        private sealed class FovReq
        {
            public string Id, Repo, Tag, Asset, Url, Digest, Serial, DeviceId;
            public static FovReq From(Dictionary<string, object> a)
            {
                string S(string k) => a.TryGetValue(k, out var v) ? v as string : null;
                var r = new FovReq { Id = S("id"), Repo = S("repo"), Tag = S("tag"), Asset = S("asset"), Url = S("url"), Digest = S("digest"), Serial = S("serial"), DeviceId = S("deviceId") };
                if (r.Id == null || r.Repo == null) throw new ArgumentException("잘못된 요청");
                if (!string.Equals(r.Repo, FovRepo, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("지원하지 않는 저장소입니다.");
                foreach (var v in new[] { r.Repo, r.Tag ?? "none" })
                    if (!Regex.IsMatch(v, @"^[A-Za-z0-9_][A-Za-z0-9_.-]*$") || v.Contains("..")) throw new ArgumentException("잘못된 요청");
                return r;
            }
            public string Latest => Tag == null ? null : Regex.Replace(Tag, @"^v\.?", "");
        }

        // ───────────── 상태 ─────────────
        public Task<object> FovStateAsync(Dictionary<string, object> a)
        {
            var r = FovReq.From(a);
            if (r.Serial != null) _selected[r.Id] = r.Serial;
            return Task.Run(() =>
            {
                if (_busy) return (object)new Dictionary<string, object> { ["busy"] = true };
                try { return (object)FovScan(r); }
                catch (Exception ex) { Log.Error("fov-scan", ex); return Err(ex.Message); }
            });
        }

        private Dictionary<string, object> FovScan(FovReq r)
        {
            var exe = AdbTools.Find();
            if (exe == null)
            {
                var s = St("연결 도구 준비 필요", "처음 한 번 약관 동의 후 ADB를 자동으로 준비합니다.", "준비 필요", "neutral", "별도로 ADB를 설치할 필요가 없습니다.");
                s["needsTerms"] = true; s["canInstall"] = true; s["installLabel"] = "연결 도구 준비"; s["action"] = "install"; s["termsUrl"] = AdbTools.Terms;
                return s;
            }
            var (entries, physical) = ListDevices(exe);
            if (physical.Count == 0 && QuestWireless.AutoReconnect(exe)) (entries, physical) = ListDevices(exe);
            var devices = physical.Values.ToArray();
            var idOf = physical.ToDictionary(kv => kv.Value, kv => kv.Key);
            var saved = QuestWireless.Saved();
            if (devices.Length == 0)
            {
                bool unauth = entries.Any(x => x.Length > 1 && x[1] == "unauthorized");
                var s = unauth ? St("USB 디버깅을 허용해 주세요", "헤드셋 안의 USB 디버깅 허용 창을 확인해 주세요.", "인증 필요", "neutral")
                               : St("Quest를 연결해 주세요", saved.Count > 0 ? "USB로 연결하거나 무선 연결을 다시 시도해 주세요." : "USB 또는 무선 디버깅으로 연결해 주세요.", "연결 필요", "neutral");
                s["message"] = saved.Count > 0 && !unauth && QuestWireless.Paused ? "무선 연결을 끊었습니다. 다시 연결하려면 ‘무선 다시 연결’을 누르거나 USB로 연결해 주세요."
                    : saved.Count > 0 && !unauth ? $"무선 연결({saved[0].Address.Split(':')[0]})을 찾지 못했습니다. Quest가 켜져 있고 같은 Wi-Fi인지 확인해 주세요."
                    : "Quest를 연결하면 자동으로 확인합니다.";
                s["wifiSaved"] = saved.Count > 0 ? saved[0].Address : null;
                return s;
            }
            _selected.TryGetValue(r.Id, out var serial);
            if (devices.Length > 1 && !devices.Contains(serial))
            {
                var s = St("Quest를 선택해 주세요", "여러 기기가 연결되어 있습니다.", "선택 필요", "neutral", "설치할 기기를 선택해 주세요.");
                s["devices"] = devices; return s;
            }
            if (devices.Length == 1) serial = devices[0];
            var adb = new Adb(exe, serial) { DeviceId = idOf.TryGetValue(serial, out var did) && did != serial ? did : null };
            var model = adb.Shell(30, false, "getprop", "ro.product.model").output.Trim();
            void Conn(Dictionary<string, object> s)
            {
                s["devices"] = devices; s["serial"] = serial; s["model"] = model; s["deviceId"] = adb.DeviceId;
                s["wireless"] = adb.Wireless; s["wifiSaved"] = saved.Count > 0 ? saved[0].Address : null;
                s["deviceLabels"] = devices.ToDictionary(d => d, d => { var x = idOf.TryGetValue(d, out var v) ? v : d; return (object)((d.Contains(':') ? "무선 · " : "") + x.Substring(Math.Max(0, x.Length - 6))); });
            }
            if (adb.Wireless && adb.DeviceId == null)
            {
                var s = St("기기 확인 필요", "무선으로 연결된 Quest의 기기 번호를 읽지 못했습니다.", "확인 필요", "warning", "USB로 연결하거나 무선 연결을 다시 시도해 주세요.");
                Conn(s); s["serial"] = null; return s;
            }
            if (!Regex.IsMatch(model, "Quest", RegexOptions.IgnoreCase))
            {
                var s = St("Quest가 아닌 기기", model, "확인 필요", "warning", "선택한 기기가 Quest가 아닙니다.");
                Conn(s); s["serial"] = null; return s;
            }

            var ver = FovAppVersion(adb);
            bool helper = FovHelperRunning(adb);
            bool otherGames = Regex.IsMatch(model, @"Quest (3|Pro)", RegexOptions.IgnoreCase);   // 다른 게임 적용은 Quest 3·3S·Pro 대상 (Quest 2는 앱 안 미리보기만)
            bool latest = ver != null && r.Latest != null && SameVersion(ver, r.Latest);
            // 앱만 업데이트하면 Quest 에서는 예전 보조 프로그램이 계속 돌고 있음 → 최신 릴리스의 보조 프로그램과 다르면 다시 켜야 함
            bool stale = false;
            if (helper && r.Tag != null)
            {
                try
                {
                    var src = Path.Combine(FovRoot, "packages", r.Tag, "source.json");
                    var want = File.Exists(src) ? (Bridge.Json.DeserializeObject(File.ReadAllText(src)) as Dictionary<string, object>)?["dex_sha256"] as string : null;
                    if (want != null) stale = adb.Hash("/data/local/tmp/glasses-fov.dex", true) != want;
                }
                catch { }
            }
            Dictionary<string, object> st;
            if (ver == null)
            {
                st = St("미설치", "Quest에 앱이 없습니다.", "미설치", "neutral");
                st["installLabel"] = "앱 설치"; st["action"] = "install";
            }
            else if (!latest && r.Latest != null)
            {
                st = St("v" + ver, $"v{r.Latest}로 업데이트할 수 있습니다.", "업데이트 필요", "warning");
                st["installLabel"] = "앱 업데이트"; st["action"] = "install";
            }
            else if (otherGames && !helper)
            {
                st = St("v" + ver, "다른 게임 적용: 꺼짐 · 재부팅하면 다시 켜야 합니다.", "최신 버전", "good");
                st["installLabel"] = "다른 게임에 적용 켜기"; st["action"] = "enable";
            }
            else if (otherGames && stale)
            {
                st = St("v" + ver, "다른 게임 적용: 예전 버전으로 켜져 있습니다. 다시 켜야 업데이트가 반영됩니다.", "다시 켜기 필요", "warning");
                st["installLabel"] = "업데이트 반영: 다른 게임 적용 다시 켜기"; st["action"] = "enable";
            }
            else
            {
                st = St("v" + ver, otherGames ? "다른 게임 적용: 켜짐 · 앱에서 글래스 시야 스위치를 켜세요." : "이 기기는 앱 안 미리보기만 지원합니다.", "최신 버전", "good");
                st["installLabel"] = "앱 실행"; st["action"] = "launch";
            }
            st["canInstall"] = true;
            st["appVersion"] = ver; st["helperRunning"] = helper; st["helperStale"] = stale; st["otherGames"] = otherGames;
            st["message"] = adb.Wireless ? "Quest 무선 연결 완료" : "Quest 연결 완료 · 작업 중에는 케이블을 분리하지 마세요.";
            Conn(st);
            if (ver != null) FovReceipt(ver); else try { File.Delete(Path.Combine(FovRoot, "installation.json")); } catch { }
            return st;
        }

        private static bool SameVersion(string a, string b)
        {
            int[] P(string v) => Regex.Matches(v ?? "", @"\d+").Cast<Match>().Select(m => int.Parse(m.Value)).Take(4).ToArray();
            var x = P(a); var y = P(b);
            return x.Length > 0 && x.SequenceEqual(y);
        }

        private static string FovAppVersion(Adb adb)
        {
            var o = adb.Shell(30, false, "dumpsys", "package", FovPkg).output;
            var m = Regex.Match(o, @"versionName=([0-9][0-9A-Za-z._-]{0,30})");
            if (m.Success) return m.Groups[1].Value;
            return Regex.IsMatch(o, @"Package \[" + Regex.Escape(FovPkg) + @"\]") ? "?" : null;
        }

        private static bool FovHelperRunning(Adb adb) => FovHelperPids(adb).Count > 0;

        private static List<string> FovHelperPids(Adb adb)
        {
            var o = adb.Shell(30, false, "ps", "-A", "-o", "PID,ARGS").output;
            return o.Split('\n').Select(l => Regex.Match(l, @"^\s*(\d+)\s+" + Regex.Escape(FovHelper) + @"\s*$")).Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();
        }

        /// <summary>목록 화면의 '설치됨' 표시용 기록 (마지막으로 확인한 기기 기준)</summary>
        private static void FovReceipt(string ver)
        {
            try
            {
                Directory.CreateDirectory(FovRoot);
                File.WriteAllText(Path.Combine(FovRoot, "installation.json"), Bridge.Json.Serialize(new Dictionary<string, object>
                    { ["state"] = "installed", ["patch_version"] = ver, ["checked_at"] = DateTime.Now.ToString("o") }), new UTF8Encoding(false));
            }
            catch { }
        }

        // ───────────── 작업 ─────────────
        public async Task<object> FovRunAsync(Dictionary<string, object> a)
        {
            var r = FovReq.From(a);
            var action = a.TryGetValue("action", out var ac) ? ac as string : null;
            if (action != "install" && action != "enable" && action != "launch") throw new ArgumentException("action");
            bool terms = a.TryGetValue("termsAccepted", out var t) && t is bool b && b;
            lock (_selected) { if (_busy) throw new PatchError("다른 작업이 진행 중입니다."); _busy = true; }
            void say(string m, double? p) => Say(r.Id, m, p);
            try
            {
                return await Task.Run(async () =>
                {
                    say(action == "install" ? "설치를 준비합니다…" : action == "enable" ? "다른 게임 적용을 준비합니다…" : "앱을 실행합니다…", null);
                    if (AdbTools.Find() == null)
                    {
                        if (!terms) throw new PatchError("ADB 다운로드 약관 동의가 필요합니다.");
                        await AdbTools.DownloadAsync(say);
                    }
                    var state = FovScan(r);
                    if (!(state.TryGetValue("serial", out var sv) && sv is string serial)) return Done(state, null);
                    if ((r.Serial != null && serial != r.Serial) || (r.DeviceId != null && (state["deviceId"] as string) != r.DeviceId))
                        return Done(state, "연결된 Quest가 화면에서 확인한 기기와 다릅니다. 다시 확인한 뒤 진행해 주세요.");
                    var adb = new Adb(AdbTools.Find(), serial) { DeviceId = state["deviceId"] as string };
                    adb.Say = m => say(m, null);
                    string msg;
                    if (action == "launch")
                    {
                        FovLaunch(adb);
                        msg = "Quest에서 앱을 실행했습니다.";
                    }
                    else
                    {
                        var dir = await FovPrepare(r, say);
                        if (action == "install")
                        {
                            say("Quest에 앱을 설치하는 중…", null);
                            var res = adb.Run(new[] { "install", "-r", Path.Combine(dir, "GlassesFoVLab.apk") }, adb.BulkTimeout(600), false);
                            if (res.code != 0 || !res.output.Contains("Success")) throw new PatchError("앱 설치에 실패했습니다: " + LastLine(res.output));
                            FovLaunch(adb);
                            msg = FovHelperRunning(adb) ? "앱 설치 완료 · ‘다른 게임 적용 다시 켜기’를 눌러야 새 버전이 다른 게임에도 반영됩니다." : "앱 설치 완료 · Quest에서 앱이 실행됩니다.";
                        }
                        else
                        {
                            if (!(state.TryGetValue("otherGames", out var og) && og is bool ok && ok)) throw new PatchError("다른 게임 적용은 Quest 3·3S·Pro에서만 쓸 수 있습니다.");
                            if (state["appVersion"] == null) throw new PatchError("먼저 앱을 설치해 주세요.");
                            await FovEnable(adb, dir, say);
                            msg = "다른 게임 적용을 켰습니다. 앱의 ‘글래스 시야’ 스위치를 켜고 화면이 잠깐 꺼졌다 켜질 때까지 기다려 주세요.";
                        }
                    }
                    var after = FovScan(r);
                    return Done(after, msg, true);
                });
            }
            catch (Exception ex)
            {
                Log.Error("fov-run", ex);
                var s = Err(ex is PatchError || ex is InvalidOperationException ? ex.Message : "작업 중 오류가 발생했습니다: " + ex.Message);
                s["failed"] = true;
                return s;
            }
            finally { _busy = false; }
        }

        private static string LastLine(string s) => (s ?? "").Split('\n').Select(x => x.Trim()).LastOrDefault(x => x.Length > 0) ?? "";

        private static void FovLaunch(Adb adb)
        {
            var res = adb.Run(new[] { "shell", "am", "start", "-n", FovActivity }, 30, false);
            if (res.code != 0 || Regex.IsMatch(res.output, @"Error", RegexOptions.IgnoreCase))
                throw new PatchError("앱을 자동으로 실행하지 못했습니다. Quest의 ‘알 수 없는 출처’에서 앱을 열어 주세요.");
        }

        /// <summary>릴리스 ZIP 을 받아 해시 확인 후 APK·보조 프로그램만 꺼냄 (같은 태그는 다시 받지 않음)</summary>
        private static async Task<string> FovPrepare(FovReq r, Action<string, double?> say)
        {
            var digest = r.Digest?.Replace("sha256:", "").ToLowerInvariant();
            if (digest == null || !Regex.IsMatch(digest, "^[a-f0-9]{64}$")) throw new PatchError("GitHub 해시 정보가 없어 진행하지 않았습니다. 잠시 후 다시 시도해 주세요.");
            if (r.Tag == null || r.Asset == null || r.Url == null) throw new PatchError("릴리스 정보를 확인하지 못했습니다.");
            if (!r.Url.StartsWith("https://github.com/zecole-vr/" + FovRepo + "/releases/download/", StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(r.Asset, @"^[A-Za-z0-9_.-]+\.zip$"))
                throw new PatchError("zecole-vr 릴리스 파일만 사용할 수 있습니다.");
            var dir = Path.Combine(FovRoot, "packages", r.Tag);
            var mark = Path.Combine(dir, "source.json");
            try
            {
                var m = File.Exists(mark) ? Bridge.Json.DeserializeObject(File.ReadAllText(mark)) as Dictionary<string, object> : null;
                if (m != null && m["sha256"] as string == digest
                    && File.Exists(Path.Combine(dir, "GlassesFoVLab.apk")) && Hash.File(Path.Combine(dir, "GlassesFoVLab.apk")) == m["apk_sha256"] as string
                    && File.Exists(Path.Combine(dir, "classes.dex")) && Hash.File(Path.Combine(dir, "classes.dex")) == m["dex_sha256"] as string) return dir;
            }
            catch { }
            var dl = Path.Combine(FovRoot, "_download"); Directory.CreateDirectory(dl);
            var file = Path.Combine(dl, r.Asset);
            if (!(File.Exists(file) && Hash.File(file) == digest))
            {
                say("GitHub에서 앱을 받는 중…", 0);
                await Net.DownloadFileAsync(r.Url, file, (d, t) => say($"앱 받는 중 · {d / 1048576}MB{(t > 0 ? " / " + t / 1048576 + "MB" : "")}", t > 0 ? d * 100.0 / t : (double?)null));
                if (Hash.File(file) != digest) { File.Delete(file); throw new PatchError("받은 파일의 해시가 GitHub 기록과 다릅니다."); }
            }
            say("받은 파일을 확인하는 중…", null);
            var stage = dir + ".staging";
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            Directory.CreateDirectory(stage);
            using (var z = ZipFile.OpenRead(file))
            {
                ZipArchiveEntry One(string suffix)
                {
                    var hits = z.Entries.Where(e => e.FullName.Replace('\\', '/').EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (hits.Count != 1) throw new PatchError("릴리스 파일 구성이 예상과 다릅니다: " + suffix);
                    return hits[0];
                }
                One("/Builds/GlassesFoVLab.apk").ExtractToFile(Path.Combine(stage, "GlassesFoVLab.apk"));
                One("/bridge/bin/classes.dex").ExtractToFile(Path.Combine(stage, "classes.dex"));
            }
            File.WriteAllText(Path.Combine(stage, "source.json"), Bridge.Json.Serialize(new Dictionary<string, object>
            {
                ["tag"] = r.Tag, ["asset"] = r.Asset, ["sha256"] = digest,
                ["apk_sha256"] = Hash.File(Path.Combine(stage, "GlassesFoVLab.apk")), ["dex_sha256"] = Hash.File(Path.Combine(stage, "classes.dex")),
            }), new UTF8Encoding(false));
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(stage, dir);
            try { File.Delete(file); } catch { }
            foreach (var other in Directory.GetDirectories(Path.Combine(FovRoot, "packages")))
                if (!string.Equals(Path.GetFileName(other), r.Tag, StringComparison.OrdinalIgnoreCase)) try { Directory.Delete(other, true); } catch { }
            return dir;
        }

        /// <summary>enable-quest-control.ps1 이식</summary>
        private static async Task FovEnable(Adb adb, string dir, Action<string, double?> say)
        {
            var serial = adb.Serial;
            // 지난번 확인 도중 끊겼다면 그때 기록한 원래 시야 값부터 되돌림
            var pending = Path.Combine(FovRoot, "fov-baseline-" + (adb.Key.Length > 0 ? adb.Key : "device") + ".json");
            if (File.Exists(pending))
            {
                say("지난번에 바뀐 시야 값을 원래대로 되돌리는 중…", 5);
                var old = ((Bridge.Json.DeserializeObject(File.ReadAllText(pending)) as Dictionary<string, object>)?["values"] as object[])?.OfType<string>().ToArray();
                if (old == null || old.Length != FovProps.Length || old.Any(v => !Regex.IsMatch(v, @"^[0-9.]*$"))) throw new PatchError("이전 시야 값 기록이 손상됐습니다. 헤드셋을 재부팅한 뒤 다시 시도해 주세요.");
                RestoreProps(adb, old);
                File.Delete(pending);
            }

            say("앱 연결 키를 확인하는 중…", 10);
            if (adb.Shell(30, false, "run-as", FovPkg, "mkdir", "-p", "files").code != 0)
                throw new PatchError("앱 폴더에 접근하지 못했습니다. 이 런처로 앱을 다시 설치해 주세요.");
            // 읽기 실패와 '키 없음'을 구분 (읽기 실패로 기존 키를 덮어쓰지 않게)
            var read = adb.Shell(30, false, "run-as", FovPkg, "sh", "-c", "if [ -f files/fov-control.key ]; then cat files/fov-control.key; else echo ZECOLE_NO_KEY; fi");
            if (read.code != 0) throw new PatchError("앱 연결 키를 읽지 못했습니다. 연결을 확인한 뒤 다시 시도해 주세요.");
            var key = read.output.Trim();
            if (key == "ZECOLE_NO_KEY" || !Regex.IsMatch(key, "^[a-f0-9]{64}$"))
            {
                var bytes = new byte[32];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
                key = string.Concat(bytes.Select(x => x.ToString("x2")));
                // 원본 스크립트처럼 키는 표준 입력으로 넘김 (명령줄에 남기지 않음)
                adb.Run(new[] { "shell", "run-as " + FovPkg + " sh -c 'cat > files/fov-control.key'" }, key + "\n", 30, true);
                if (adb.Shell(30, false, "run-as", FovPkg, "cat", "files/fov-control.key").output.Trim() != key) throw new PatchError("앱 연결 키를 저장하지 못했습니다.");
            }

            // Meta 공식 CLI 로 이 기기가 시야 시뮬레이션을 지원하는지 확인 (확인 뒤 시야 값은 원래대로)
            var cli = await EnsureMetaCli(say);
            say("Meta 공식 도구로 기기를 확인하는 중…", 30);
            var list = RunCli(cli, "device list --json", 120);
            if (list.code != 0) throw new PatchError("Meta 공식 도구가 기기 목록을 읽지 못했습니다.");
            if (!CliIds(list.output).Contains(serial)) throw new PatchError("Meta 공식 도구가 이 Quest를 찾지 못했습니다. 연결을 확인한 뒤 다시 시도해 주세요.");
            var baseline = FovProps.Select(p => adb.Shell(30, true, "getprop", p).output.Trim()).ToArray();
            if (baseline.Any(v => !Regex.IsMatch(v, @"^[0-9.]*$"))) throw new PatchError("기기의 시야 설정 값이 예상과 달라 아무것도 바꾸지 않았습니다.");
            Directory.CreateDirectory(FovRoot);
            File.WriteAllText(pending, Bridge.Json.Serialize(new Dictionary<string, object> { ["serial"] = serial, ["values"] = baseline, ["at"] = DateTime.Now.ToString("o") }), new UTF8Encoding(false));
            try
            {
                var en = RunCli(cli, "device fov-sim enable --device " + serial, 120);
                if (en.code != 0) throw new PatchError("이 기기는 공식 시야 시뮬레이션을 지원하지 않습니다.");
                foreach (var p in FovProps.Take(4))
                {
                    var v = adb.Shell(30, true, "getprop", p).output.Trim();
                    if (!Regex.IsMatch(v, @"^[0-9]+(\.[0-9]+)?$") || double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) <= 0 || double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) > 90)
                        throw new PatchError("공식 시야 값이 올바르지 않습니다.");
                }
            }
            finally
            {
                RestoreProps(adb, baseline);   // 실패하면 기록 파일을 남겨 다음 실행에서 다시 되돌림
                File.Delete(pending);
            }

            // 새 파일을 먼저 올려 둔 뒤 교체 → 업로드가 실패해도 기존 보조 프로그램은 그대로
            say("보조 프로그램을 Quest에 올리는 중…", 60);
            var cfg = Path.Combine(FovRoot, "bridge-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".config");
            try
            {
                File.WriteAllText(cfg, key + "\n" + string.Join("\n", FovAngles) + "\n", Encoding.ASCII);
                adb.Run(new[] { "push", Path.Combine(dir, "classes.dex"), "/data/local/tmp/glasses-fov.dex.new" }, adb.BulkTimeout(120));
                adb.Run(new[] { "push", cfg, "/data/local/tmp/glasses-fov.config.new" }, adb.BulkTimeout(60));
            }
            finally { try { File.Delete(cfg); } catch { } }
            if (adb.Shell(30, false, "chmod", "600", "/data/local/tmp/glasses-fov.config.new").code != 0) throw new PatchError("연결 키 파일 권한을 제한하지 못했습니다.");
            var dexHash = Hash.File(Path.Combine(dir, "classes.dex"));
            if (adb.Hash("/data/local/tmp/glasses-fov.dex.new") != dexHash) throw new PatchError("Quest로 올린 보조 프로그램이 원본과 다릅니다.");

            var before = FovHelperPids(adb);
            foreach (var pid in before)   // 이 보조 프로그램만 정확히 골라 교체
                if (adb.Shell(30, false, "kill", pid).code != 0) throw new PatchError("이전 보조 프로그램을 멈추지 못했습니다.");
            for (int i = 0; i < 10 && FovHelperPids(adb).Intersect(before).Any(); i++) Thread.Sleep(300);
            if (FovHelperPids(adb).Intersect(before).Any()) throw new PatchError("이전 보조 프로그램이 멈추지 않았습니다. 헤드셋을 재부팅한 뒤 다시 시도해 주세요.");
            if (adb.Shell(30, false, "mv", "-f", "/data/local/tmp/glasses-fov.dex.new", "/data/local/tmp/glasses-fov.dex").code != 0
                || adb.Shell(30, false, "mv", "-f", "/data/local/tmp/glasses-fov.config.new", "/data/local/tmp/glasses-fov.config").code != 0)
                throw new PatchError("보조 프로그램 파일을 교체하지 못했습니다.");
            say("보조 프로그램을 시작하는 중…", 85);
            adb.Run(new[] { "shell", "CLASSPATH=/data/local/tmp/glasses-fov.dex nohup " + FovHelper + " >/data/local/tmp/glasses-fov.log 2>&1 </dev/null &" }, 30);
            // 새로 뜬 프로세스가 있어야 성공 (이전 프로세스를 새 것으로 오인하지 않게)
            for (int i = 0; i < 8; i++) { Thread.Sleep(500); if (FovHelperPids(adb).Except(before).Any()) return; }
            var log = adb.Shell(15, false, "tail", "-n", "5", "/data/local/tmp/glasses-fov.log").output;
            throw new PatchError("보조 프로그램이 시작되지 않았습니다. " + LastLine(log));
        }

        /// <summary>시야 값 5개를 모두 되돌림. 하나가 실패해도 나머지는 끝까지 시도한 뒤 오류</summary>
        private static void RestoreProps(Adb adb, string[] values)
        {
            var failed = new List<string>();
            for (int i = 0; i < FovProps.Length; i++)
            {
                bool ok;
                try { ok = adb.Run(new[] { "shell", "setprop " + FovProps[i] + " '" + values[i] + "'" }, 30, false).code == 0; } catch { ok = false; }
                if (!ok) failed.Add(FovProps[i]);
            }
            if (failed.Count > 0) throw new PatchError("시야 값 일부를 원래대로 되돌리지 못했습니다. 다시 시도하거나 헤드셋을 재부팅해 주세요.");
        }

        private static HashSet<string> CliIds(string json)
        {
            var ids = new HashSet<string>();
            object parsed;
            try { parsed = Bridge.Json.DeserializeObject(json.Substring(Math.Max(0, json.IndexOf('[')))); } catch { throw new PatchError("Meta 공식 도구의 기기 목록 형식을 읽지 못했습니다."); }
            foreach (var d in (parsed as object[] ?? new object[0]).OfType<Dictionary<string, object>>())
            {
                string G(string k) => d.TryGetValue(k, out var v) ? v as string : null;
                if (G("state") != "device") continue;
                if (!(Regex.IsMatch(G("model") ?? "", "Quest", RegexOptions.IgnoreCase) || new[] { "eureka", "panther", "cambria", "hollywood" }.Contains(G("device")))) continue;
                var id = G("id") ?? G("serial") ?? G("device_id");
                if (id != null) ids.Add(id);
            }
            return ids;
        }

        private static (string output, int code) RunCli(string cli, string args, int timeoutSec)
        {
            var psi = new ProcessStartInfo(cli, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(cli),
            };
            using (var p = Process.Start(psi))
            {
                var outTask = p.StandardOutput.ReadToEndAsync(); var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutSec * 1000)) { try { p.Kill(); } catch { } throw new PatchError("Meta 공식 도구 응답 시간이 초과되었습니다."); }
                p.WaitForExit();
                var err = errTask.Result;
                if (err.Length > 0) Log.Info("[metavr] " + args + " · " + err.Trim());
                return (outTask.Result, p.ExitCode);
            }
        }

        /// <summary>Meta 공식 CLI(metavr.exe): oculus.com 이 알려 주는 SHA-256 으로 검증한 것만 씀</summary>
        private static async Task<string> EnsureMetaCli(Action<string, double?> say)
        {
            var exe = Path.Combine(MetaCliDir, "metavr.exe");
            var receipt = Path.Combine(MetaCliDir, "receipt.json");
            try
            {
                if (File.Exists(exe) && File.Exists(receipt)
                    && (Bridge.Json.DeserializeObject(File.ReadAllText(receipt)) as Dictionary<string, object>)?["exe_sha256"] as string == Hash.File(exe)) return exe;
            }
            catch { }
            say("Meta 공식 도구(Meta VR CLI)를 받는 중… 처음 한 번만 받습니다.", null);
            var info = Bridge.Json.DeserializeObject(await Net.Downloader.GetStringAsync("https://www.oculus.com/binary-info/" + MetaCliId + "/latest/")) as Dictionary<string, object>;
            var sha = (info?["sha256"] as string)?.ToLowerInvariant();
            if (sha == null || !Regex.IsMatch(sha, "^[a-f0-9]{64}$")) throw new PatchError("Meta 공식 다운로드 검증 정보를 확인하지 못했습니다. 다시 시도해 주세요.");
            var parent = Path.GetDirectoryName(MetaCliDir); Directory.CreateDirectory(parent);
            var stage = Path.Combine(parent, "metavr-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(stage);
            try
            {
                var zip = Path.Combine(stage, "download.zip");
                await Net.DownloadFileAsync("https://www.oculus.com/download_app/?id=" + MetaCliId, zip, (d, t) => say($"Meta 공식 도구 받는 중 · {d / 1048576}MB", t > 0 ? d * 100.0 / t : (double?)null));
                if (Hash.File(zip) != sha) throw new PatchError("Meta 공식 도구의 해시가 맞지 않아 실행하지 않습니다.");
                var outDir = Path.Combine(stage, "x"); Directory.CreateDirectory(outDir);
                var root = Path.GetFullPath(outDir).TrimEnd('\\') + "\\";
                using (var z = ZipFile.OpenRead(zip))
                    foreach (var e in z.Entries)
                    {
                        var target = Path.GetFullPath(Path.Combine(outDir, e.FullName.Replace('/', '\\')));
                        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new PatchError("Meta 공식 도구 압축 파일에 잘못된 경로가 있습니다.");
                        if (e.FullName.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(target)); e.ExtractToFile(target, true);
                    }
                if (!File.Exists(Path.Combine(outDir, "metavr.exe"))) throw new PatchError("Meta 공식 압축 파일에서 metavr.exe를 찾지 못했습니다.");
                File.WriteAllText(Path.Combine(outDir, "receipt.json"), Bridge.Json.Serialize(new Dictionary<string, object>
                    { ["archive_sha256"] = sha, ["exe_sha256"] = Hash.File(Path.Combine(outDir, "metavr.exe")), ["downloaded_at"] = DateTime.Now.ToString("o") }), new UTF8Encoding(false));
                if (Directory.Exists(MetaCliDir)) Directory.Delete(MetaCliDir, true);
                Directory.Move(outDir, MetaCliDir);
                return exe;
            }
            finally { try { Directory.Delete(stage, true); } catch { } }
        }
    }
}
