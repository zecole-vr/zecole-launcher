using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Zecole.Launcher.Quest
{
    /// <summary>
    /// 런처 화면 ↔ Quest 엔진. 기존 설치기 launcher.py 의 상태 확인(_scan)·작업(_worker) 흐름을 이식.
    /// </summary>
    internal sealed partial class QuestService
    {
        private readonly Action<string, object> _emit;
        private volatile bool _busy;
        private readonly Dictionary<string, string> _selected = new Dictionary<string, string>(); // 게임별 선택 기기

        public bool Busy => _busy;
        public QuestService(Action<string, object> emit) { _emit = emit; }

        private sealed class Req
        {
            public string Id, Repo, PkgKey, Tag, Asset, Url, Digest, Storage, Serial, DeviceId;
            public static Req From(Dictionary<string, object> a)
            {
                string S(string k) => a.TryGetValue(k, out var v) ? v as string : null;
                var r = new Req { Id = S("id"), Repo = S("repo"), PkgKey = S("pkgKey") ?? S("repo"), Tag = S("tag"), Asset = S("asset"), Url = S("url"), Digest = S("digest"), Storage = S("storage"), Serial = S("serial"), DeviceId = S("deviceId") };
                if (r.Id == null || r.Repo == null || r.Storage == null || !Regex.IsMatch(r.Storage, @"^[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*$")
                    || r.Storage.Split('/').Any(x => x == "." || x == "..")) throw new ArgumentException("잘못된 요청");
                // 보관 폴더 경로에 쓰이는 값은 단순한 이름만 허용
                foreach (var v in new[] { r.Repo, r.PkgKey, r.Tag ?? "none" })
                    if (!Regex.IsMatch(v, @"^[A-Za-z0-9_][A-Za-z0-9_.-]*$") || v.Contains("..")) throw new ArgumentException("잘못된 요청");
                return r;
            }
            public string StoragePath => Path.Combine(Program.ZecoleRoot, Storage.Replace('/', Path.DirectorySeparatorChar));
            public string PackageDir => Path.Combine(QuestPackage.Root, PkgKey, Tag ?? "-");
        }

        private void Say(string id, string message, double? pct) =>
            _emit("quest-progress", new Dictionary<string, object> { ["id"] = id, ["message"] = message, ["percent"] = pct });

        // ───────────── 상태 확인 ─────────────
        public Task<object> StateAsync(Dictionary<string, object> a)
        {
            var r = Req.From(a);
            if (r.Serial != null) _selected[r.Id] = r.Serial;
            return Task.Run(() =>
            {
                if (_busy) return (object)new Dictionary<string, object> { ["busy"] = true };
                try { return (object)Scan(r, null); }
                catch (Exception ex) { Log.Error("quest-scan", ex); return Err(ex.Message); }
            });
        }

        private static Dictionary<string, object> St(string current, string note, string tag, string tone, string message = null) => new Dictionary<string, object>
        {
            ["current"] = current, ["note"] = note, ["tag"] = tag, ["tone"] = tone, ["message"] = message,
            ["canInstall"] = false, ["canRestore"] = false, ["devices"] = new string[0],
        };
        private static Dictionary<string, object> Err(string msg) => St("상태 확인 필요", "", "확인 필요", "warning", msg);

        private QuestPackage CachedPackage(Req r)
        {
            try { return File.Exists(Path.Combine(r.PackageDir, "manifest.json")) ? QuestPackage.Load(r.PackageDir) : null; }
            catch { return null; }
        }

        private Dictionary<string, object> Scan(Req r, QuestPackage pkg)
        {
            var exe = AdbTools.Find();
            if (exe == null)
            {
                var s = St("연결 도구 준비 필요", "처음 한 번 약관 동의 후 ADB를 자동으로 준비합니다.", "준비 필요", "neutral", "별도로 ADB를 설치할 필요가 없습니다.");
                s["needsTerms"] = true; s["canInstall"] = true; s["installLabel"] = "연결 도구 준비"; s["termsUrl"] = AdbTools.Terms;
                return s;
            }
            var adb = new Adb(exe);
            var (entries, physical) = ListDevices(exe);
            // 연결된 기기가 없으면 기억해 둔 무선 주소로 자동 재연결
            if (physical.Count == 0 && QuestWireless.AutoReconnect(exe)) (entries, physical) = ListDevices(exe);
            var devices = physical.Values.ToArray();
            var idOf = physical.ToDictionary(kv => kv.Value, kv => kv.Key);
            var saved = QuestWireless.Saved();
            if (devices.Length == 0)
            {
                bool unauth = entries.Any(x => x.Length > 1 && x[1] == "unauthorized");
                var s = unauth ? St("USB 디버깅을 허용해 주세요", "헤드셋 안의 USB 디버깅 허용 창을 확인해 주세요.", "인증 필요", "neutral")
                               : St("Quest를 연결해 주세요", saved.Count > 0 ? "USB로 연결하거나 무선 연결을 다시 시도해 주세요." : "USB로 연결하거나, 무선 디버깅 중이면 ‘무선 Quest 찾기’를 눌러 주세요.", "연결 필요", "neutral");
                s["message"] = saved.Count > 0 && !unauth && QuestWireless.Paused
                    ? "무선 연결을 끊었습니다. 다시 연결하려면 ‘무선 다시 연결’을 누르거나 USB로 연결해 주세요."
                    : saved.Count > 0 && !unauth
                    ? $"무선 연결({saved[0].Address.Split(':')[0]})을 찾지 못했습니다. Quest가 켜져 있고 같은 Wi-Fi인지 확인해 주세요. Quest를 재부팅했다면 USB로 한 번 연결해 무선을 다시 켜야 합니다."
                    : "기기를 확인할 때까지 게임 파일을 변경하지 않습니다.";
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
            adb.Serial = serial; adb.DeviceId = idOf.TryGetValue(serial, out var did) && did != serial ? did : null;
            var model = adb.Shell(30, false, "getprop", "ro.product.model").output.Trim();
            if (adb.Wireless && adb.DeviceId == null)
            {
                var s = St("기기 확인 필요", "무선으로 연결된 Quest의 기기 번호를 읽지 못했습니다.", "확인 필요", "warning", "USB로 연결하거나 무선 연결을 다시 시도해 주세요. 확인 전에는 게임 파일을 변경하지 않습니다.");
                s["devices"] = devices; s["wireless"] = true; return s;
            }
            if (adb.Wireless && !saved.Any(h => h.Address == serial)) { try { QuestWireless.Remember(adb.DeviceId, serial, model); saved = QuestWireless.Saved(); } catch { } }
            string connMsg = adb.Wireless ? "Quest 무선 연결 완료 · 설치 중에는 Quest를 켜 둔 채 공유기 가까이에 두세요." : "Quest 연결 완료 · 설치 중에는 케이블을 분리하지 마세요.";
            string Short(string d) { var x = idOf.TryGetValue(d, out var v) ? v : d; return (d.Contains(':') ? "무선 · " : "") + x.Substring(Math.Max(0, x.Length - 6)); }
            void Conn(Dictionary<string, object> s)
            {
                s["devices"] = devices; s["serial"] = serial; s["model"] = model; s["deviceId"] = adb.DeviceId;
                s["wireless"] = adb.Wireless; s["wifiSaved"] = saved.Count > 0 ? saved[0].Address : null;
                s["deviceLabels"] = devices.ToDictionary(d => d, d => (object)Short(d));
            }

            pkg = pkg ?? CachedPackage(r);
            if (pkg == null)
            {
                // 패치 데이터를 아직 받지 않았으면 기기 연결과 이 PC 의 설치 기록만 보여줌
                var receipt = LatestReceipt(r, adb.Key);
                var s = receipt != null && receipt["state"] as string == "installed"
                    ? St("v" + (receipt.TryGetValue("patch_version", out var pv) && pv != null ? pv : receipt.TryGetValue("release", out var rv) ? rv : "?"), "이 PC의 설치 기록 · 설치하면 기기 파일을 다시 확인합니다", "설치 기록 있음", "good")
                    : St("Quest 연결됨", "설치를 누르면 한글패치 데이터를 받은 뒤 기기를 확인합니다.", "준비됨", "neutral");
                s["canInstall"] = true; s["installLabel"] = "한글패치 설치"; s["canRestore"] = receipt != null && receipt["state"] as string == "installed";
                Conn(s); s["packageReady"] = false;
                s["message"] = connMsg;
                return s;
            }

            var engine = new QuestEngine(pkg, adb, r.StoragePath, (m, p) => Say(r.Id, m, p));
            Dictionary<string, object> R(Dictionary<string, object> s) { Conn(s); s["packageReady"] = true; return s; }
            try { engine.VersionCheck(); }
            catch (PatchError ex) { return R(St("게임 버전 확인 필요", "", "확인 필요", "warning", ex.Message)); }

            Say(r.Id, "기기의 게임 파일을 확인합니다…", null);
            var m = pkg.M;
            var current = adb.Hash(pkg.Main, true); var original = adb.Hash(pkg.Backup, true);
            var journal = Path.Combine(engine.Work, "transaction.json");
            if (File.Exists(journal))
            {
                var j = Bridge.Json.DeserializeObject(File.ReadAllText(journal, Encoding.UTF8)) as Dictionary<string, object>;
                var phase = j?["phase"] as string;
                if (!new[] { "complete", "rolled_back", "abandoned" }.Contains(phase))
                {
                    var running = engine.GameRunning();
                    var s = St("중단된 작업 확인", "설치 기록과 실제 파일을 검증하여 복구합니다.", "복구 필요", "warning",
                        running ? "게임을 종료해 주세요." : "복구 버튼을 누르면 확인 가능한 상태에서만 복구합니다.");
                    s["canInstall"] = !running; s["installLabel"] = "중단된 작업 복구"; s["recovery"] = true;
                    return R(s);
                }
            }
            if (!pkg.KnownHashes.Contains(current))
                return R(St("설치 상태 확인 필요", "지원하지 않는 파일이므로 자동으로 변경하지 않습니다.", "확인 필요", "warning", "다른 수정본 또는 게임 업데이트 여부를 확인해 주세요."));
            string srcHash = pkg.S("source_sha256");
            if (original != null && original != srcHash)
                return R(St("원본 백업 확인 필요", "기기의 원본 백업이 예상과 다릅니다.", "확인 필요", "warning", "자동으로 변경하지 않습니다."));
            // 원본은 PC 에 보관. 없으면 기기에 남은 원본(원본 상태 게임 파일·이전 백업·시험본 백업)에서 가져올 수 있는지 확인
            var pcOriginal = engine.PcOriginal;
            bool pcOk = File.Exists(pcOriginal) && new FileInfo(pcOriginal).Length == pkg.SourceBytes;
            bool originalReachable = current == srcHash || pcOk || original == srcHash || (current != srcHash && engine.LegacyOriginal(current) != null)
                || engine.LegacyPcOriginals().Count > 0;   // 이전 설치기의 PC 원본 (해시는 사용 직전에 확인)

            Dictionary<string, object> st;
            if (current == srcHash)
                st = St("미설치", $"게임 빌드 {m["version_code"]} · 원본 파일 확인 완료", "미설치", "neutral");
            else if (current == pkg.S("target_sha256"))
                st = St("v" + m["release"], "기기 파일 확인 완료 · 최신 한글패치", "최신 버전", "good");
            else
            {
                var label = (string)pkg.Dict("compatible_previous")[current];
                st = St(char.IsDigit(label[0]) ? "v" + label : label, "이 설치본으로 업데이트할 수 있습니다.", "업데이트 필요", "warning");
            }
            st["installLabel"] = current == srcHash ? "한글패치 설치" : current == pkg.S("target_sha256") ? "설치 확인 / 재설치" : "한글패치 업데이트";
            var run = engine.GameRunning();
            // 원본을 구할 수 없으면: 같은 버전 확인만 가능, 업데이트·복구는 불가 (게임 재설치로 원본 복구)
            st["canInstall"] = !run && (originalReachable || current == pkg.S("target_sha256"));
            st["canRestore"] = !run && current != srcHash && originalReachable;
            st["installed"] = current == pkg.S("target_sha256");
            st["pcOriginal"] = pcOk;
            st["message"] = run ? "게임을 완전히 종료한 뒤 다시 확인해 주세요."
                : !originalReachable ? "이 PC에 원본 백업이 없어요. 원래 게임으로 되돌리려면 Meta 앱에서 게임을 삭제한 뒤 다시 설치해 주세요."
                : connMsg;
            try
            {
                var (dev, pc, mobile) = engine.Reclaimable(original);
                st["reclaimDevice"] = dev; st["reclaimPc"] = pc; st["keptForMobile"] = mobile;
            }
            catch { }
            return R(st);
        }

        /// <summary>adb devices 목록 → (전체 줄, 실제 기기 번호 → 연결 주소). 같은 기기가 USB·무선으로 함께 붙으면 USB 우선</summary>
        private static (List<string[]> entries, Dictionary<string, string> physical) ListDevices(string exe)
        {
            var text = new Adb(exe).Run(new[] { "devices" }, 30).output;
            var entries = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("List ") && !l.StartsWith("*"))
                              .Select(l => l.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)).ToList();
            var physical = new Dictionary<string, string>();
            foreach (var cand in entries.Where(x => x.Length >= 2 && x[1] == "device").Select(x => x[0]).OrderBy(s => s.Contains(':')).ThenBy(s => s))
            {
                if (!Regex.IsMatch(cand, @"^[A-Za-z0-9_.:-]+$")) continue;
                var id = new Adb(exe, cand).Shell(30, false, "getprop", "ro.serialno").output.Trim();
                if (!Adb.SafeId(id)) id = cand;
                if (!physical.ContainsKey(id)) physical[id] = cand;
            }
            return (entries, physical);
        }

        private Dictionary<string, object> LatestReceipt(Req r, string key)
        {
            try
            {
                if (string.IsNullOrEmpty(key)) return null;
                var name = key;
                var dir = Path.Combine(r.StoragePath, name);
                if (!Directory.Exists(dir)) dir = Path.Combine(r.StoragePath, "userdata", name);   // 초기 형식(ITR2) 설치기 위치
                if (!Directory.Exists(dir)) return null;
                var f = Directory.GetFiles(dir, "installation.json", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                return f == null ? null : Bridge.Json.DeserializeObject(File.ReadAllText(f, Encoding.UTF8)) as Dictionary<string, object>;
            }
            catch { return null; }
        }

        // ───────────── 설치 · 복구 ─────────────
        public async Task<object> RunAsync(Dictionary<string, object> a)
        {
            var r = Req.From(a);
            var action = a["action"] as string;
            if (action != "install" && action != "restore" && action != "cleanup") throw new ArgumentException("action");
            bool terms = a.TryGetValue("termsAccepted", out var t) && t is bool b && b;
            lock (_selected) { if (_busy) throw new PatchError("다른 작업이 진행 중입니다."); _busy = true; }
            var logDir = Path.Combine(r.StoragePath, "logs"); Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, DateTime.Now.ToString("yyyyMMdd-HHmmss-ffffff") + "-launcher-" + action + ".log");
            void say(string m, double? p)
            {
                Say(r.Id, m, p);
                if (p == null || (int)p % 10 == 0) try { File.AppendAllText(logPath, DateTime.Now.ToString("o") + " " + m + Environment.NewLine, Encoding.UTF8); } catch { }
            }
            try
            {
                return await Task.Run(async () =>
                {
                    say(action == "install" ? "설치를 준비합니다…" : action == "restore" ? "원본 복구를 준비합니다…" : "저장공간 정리를 준비합니다…", null);
                    if (AdbTools.Find() == null)
                    {
                        if (!terms) throw new PatchError("ADB 다운로드 약관 동의가 필요합니다.");
                        await AdbTools.DownloadAsync(say);
                    }
                    var pkg = await QuestPackage.PrepareAsync(r.Repo, r.PkgKey, r.Tag, r.Asset, r.Url, r.Digest, say);
                    bool alreadyInstalled = false;
                    using (QuestEngine.Lock(r.StoragePath))
                    {
                        var state = Scan(r, pkg);
                        if (!(state.TryGetValue("serial", out var sv) && sv is string serial)) return Done(state, null);
                        if (!SameDevice(r, state)) return Done(state, "연결된 Quest가 화면에서 확인한 기기와 다릅니다. 다시 확인한 뒤 진행해 주세요.");
                        var adb = new Adb(AdbTools.Find(), serial) { DeviceId = state.TryGetValue("deviceId", out var dv) ? dv as string : null };
                        var engine = new QuestEngine(pkg, adb, r.StoragePath, say);
                        using (QuestEngine.Lock(engine.Work))
                        {
                            var journal = Path.Combine(engine.Work, "transaction.json");
                            bool recoveryOnly = File.Exists(journal) &&
                                !new[] { "complete", "rolled_back", "abandoned" }.Contains((Bridge.Json.DeserializeObject(File.ReadAllText(journal, Encoding.UTF8)) as Dictionary<string, object>)?["phase"] as string);
                            using (QuestEngine.Lock(Path.Combine(r.StoragePath, "engine")))
                            using (QuestEngine.Lock(Path.Combine(engine.Work, "engine")))
                            { engine.RecoverJournal(); engine.SweepOwned(); }
                            state = Scan(r, pkg);
                            if (!SameDevice(r, state)) return Done(state, "작업 중 연결된 Quest가 바뀌었습니다. 다시 확인해 주세요.");
                            if (recoveryOnly) return Done(state, "중단된 작업 복구 완료 · 현재 상태를 확인해 주세요.");
                            if (action == "cleanup")
                            {
                                (long dev, long pc) freed;
                                using (QuestEngine.Lock(Path.Combine(r.StoragePath, "engine")))
                                using (QuestEngine.Lock(Path.Combine(engine.Work, "engine")))
                                    freed = engine.Cleanup();
                                var after = Scan(r, pkg);
                                after["freedDevice"] = freed.dev; after["freedPc"] = freed.pc;
                                File.AppendAllText(logPath, "작업 성공 완료: cleanup" + Environment.NewLine);
                                return Done(after, $"저장공간 정리 완료 · Quest {freed.dev / 1073741824.0:0.0}GB · PC {freed.pc / 1073741824.0:0.0}GB 확보", true);
                            }
                            if (!(state[action == "install" ? "canInstall" : "canRestore"] is bool ok && ok)) return Done(state, null);
                            alreadyInstalled = action == "install" && state.TryGetValue("installed", out var ins) && ins is bool bi && bi;
                            using (QuestEngine.Lock(Path.Combine(r.StoragePath, "engine")))
                            using (QuestEngine.Lock(Path.Combine(engine.Work, "engine")))
                                engine.Transact(action);
                        }
                        state = Scan(r, pkg);
                        File.AppendAllText(logPath, "작업 성공 완료: " + action + Environment.NewLine);
                        if (alreadyInstalled) { state["verifiedOnly"] = true; return Done(state, "설치 확인 완료 · 기기 파일이 최신 한글판과 일치합니다.", true); }
                        return Done(state, action == "install" ? "설치 완료 · 게임에서 확인해 주세요." : "원본 복구 완료 · 세이브 진행 상황은 유지됩니다.", true);
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error("quest-run", ex);
                try { File.AppendAllText(logPath, ex + Environment.NewLine); } catch { }
                var s = Err(ex is PatchError || ex is InvalidOperationException ? ex.Message : "작업 중 오류가 발생했습니다: " + ex.Message);
                s["failed"] = true; s["log"] = logPath;
                return s;
            }
            finally { _busy = false; }
        }

        // ───────────── 무선 연결 ─────────────
        public Task<object> WifiAsync(Dictionary<string, object> a)
        {
            string S(string k) => a.TryGetValue(k, out var v) ? v as string : null;
            var action = S("action");
            if (!new[] { "enable", "connect", "discover", "disconnect", "forget" }.Contains(action)) throw new ArgumentException("action");
            return Task.Run(() =>
            {
                lock (_selected) { if (_busy) throw new PatchError("다른 작업이 진행 중입니다."); _busy = true; }
                try
                {
                    var exe = AdbTools.Find() ?? throw new PatchError("먼저 연결 도구(ADB)를 준비해 주세요.");
                    switch (action)
                    {
                        case "enable": return (object)QuestWireless.Enable(exe, S("serial"));
                        case "connect": return QuestWireless.Connect(exe, S("address"));
                        case "discover": return QuestWireless.Discover(exe);
                        case "disconnect": return QuestWireless.Disconnect(exe, S("address"), false);
                        default: return QuestWireless.Disconnect(exe, S("address"), true);
                    }
                }
                finally { _busy = false; }
            });
        }

        /// <summary>화면이 확인한 기기(연결 주소·기기 번호)와 지금 작업할 기기가 같은지</summary>
        private static bool SameDevice(Req r, Dictionary<string, object> state)
        {
            var serial = state.TryGetValue("serial", out var a) ? a as string : null;
            var id = state.TryGetValue("deviceId", out var b) ? b as string : null;
            if (r.Serial != null && serial != r.Serial) return false;
            if (r.DeviceId != null && id != r.DeviceId) return false;
            return true;
        }

        private static object Done(Dictionary<string, object> state, string message, bool success = false)
        {
            if (message != null) state["message"] = message;
            state["success"] = success;
            return state;
        }
    }
}
