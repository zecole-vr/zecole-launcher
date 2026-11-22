using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Zecole.Launcher.Quest
{
    /// <summary>
    /// Quest OBB 한글패치 설치·복구 엔진.
    /// 제콜 H3VR2 설치기(quest_patch.py, launcher.py)를 그대로 옮긴 것으로,
    /// 작업 폴더·기록 파일(transaction.json, installation.json)·기기 백업 이름이 기존 설치기와 같아서
    /// 기존 설치기로 설치한 기기도 이어서 관리하고, 중단된 작업도 서로 복구할 수 있음.
    /// </summary>
    internal sealed class QuestEngine
    {
        private const long CHUNK = 8 * 1024 * 1024;
        private const long GiB = 1024L * 1024 * 1024;
        private static readonly string[] Finished = { "complete", "rolled_back", "abandoned" };
        private static readonly Dictionary<string, string> PhaseLabels = new Dictionary<string, string>
        {
            ["prepared"] = "설치 작업을 기록합니다.", ["transferring"] = "Quest로 파일을 전송합니다…", ["staged_verified"] = "전송 파일 검증 완료",
            ["swapping"] = "게임 파일을 교체합니다…", ["complete"] = "작업 결과 검증 완료", ["rolled_back"] = "직전 상태로 복구했습니다.", ["abandoned"] = "중단된 전송을 정리했습니다.",
        };

        private readonly QuestPackage _p;
        private readonly Adb _adb;
        private readonly Action<string, double?> _say;
        private readonly string _storage;
        private Dictionary<string, object> M => _p.M;

        public QuestEngine(QuestPackage p, Adb adb, string storage, Action<string, double?> say)
        {
            _p = p; _adb = adb; _storage = storage; _say = say;
            _adb.Say = m => say(m, null);
        }

        private void Say(string m, double? pct = null) => _say(m, pct);
        public string Work => _adb.Key.Length == 0 ? throw new PatchError("Quest 기기 번호를 확인하지 못했습니다. 다시 연결한 뒤 시도해 주세요.")
            : Path.Combine(_storage, _p.Legacy ? "userdata" : "", _adb.Key, Convert.ToString(M["version_code"]));

        // ───────────── 검사 ─────────────
        public static bool ModelSupported(Dictionary<string, object> m, string model)
        {
            var list = (m["supported_models"] as object[] ?? new object[0]).Cast<string>();
            var re = m.TryGetValue("supported_model_regex", out var r) ? r as string : null;
            return list.Contains(model) || (re != null && Regex.IsMatch(model, "^(?:" + re.TrimStart('^').TrimEnd('$') + ")$"));
        }

        public void VersionCheck()
        {
            var model = _adb.Shell("getprop", "ro.product.model").output.Trim();
            if (M.ContainsKey("supported_models") && !ModelSupported(M, model)) throw new PatchError($"지원하지 않는 기기입니다: {(model.Length > 0 ? model : "모델명 확인 불가")}. 이번 배포판은 {string.Join(" / ", ((object[])M["supported_models"]).Cast<string>())}용입니다.");
            var text = _adb.Shell("dumpsys", "package", _p.S("package")).output;
            var code = Regex.Match(text, @"\bversionCode=(\d+)");
            var name = Regex.Match(text, @"\bversionName=([^\s]+)");
            bool nameOk = _p.Format == 1 || (name.Success && name.Groups[1].Value == _p.S("version_name")); // 형식 1 은 빌드 번호만 확인
            if (!nameOk || !code.Success || long.Parse(code.Groups[1].Value) != _p.L("version_code"))
                throw new PatchError($"지원 게임은 {Short()} Quest {_p.S("version_name")} / 빌드 {M["version_code"]}입니다. 게임을 최신 버전으로 업데이트했는지 확인해 주세요. 다른 버전은 변경하지 않습니다.");
            if (_p.S("source_apk_sha256") == null) return;   // 초기 형식(ITR2): 빌드 번호와 두 번째 OBB 로 확인
            var loc = _adb.Shell("pm", "path", _p.S("package")).output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (loc.Length != 1 || !loc[0].StartsWith("package:")) throw new PatchError("게임 APK 구성이 달라 중단합니다.");
            Say("게임 APK 확인 중…");
            if (_adb.Hash(loc[0].Substring(8)) != _p.S("source_apk_sha256")) throw new PatchError("게임 APK가 검증된 버전과 다릅니다. 변경하지 않습니다.");
        }

        private string Short() => (_p.S("name") ?? "게임").Replace(" Quest Korean", "");

        /// <summary>한글패치가 바꾸지 않는 두 번째 OBB 가 검증된 빌드 그대로인지 (ITR2 설치기와 같은 확인)</summary>
        private void PatchObbCheck()
        {
            if (_p.PatchObb == null) return;
            Say("게임 추가 데이터 확인 중…");
            if (_adb.Hash(_p.PatchObb, true) != _p.S("patch_sha256")) throw new PatchError("게임 추가 데이터(patch OBB)가 검증된 버전과 달라 중단합니다. 게임을 최신 버전으로 업데이트했는지 확인해 주세요.");
        }

        /// <summary>설치 전 세이브·설정을 PC로 백업 (ITR2 설치기와 같은 위치: 작업 폴더/saves-날짜-xxxx/Saved)</summary>
        private void BackupSaves()
        {
            var dir = _p.SaveDir;
            if (dir == null || !_adb.Exists(dir)) return;
            Say("세이브와 설정을 PC에 백업합니다…");
            var folder = Path.Combine(Work, "saves-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 4));
            Directory.CreateDirectory(folder);
            _adb.Run(new[] { "pull", dir, Path.Combine(folder, "Saved") }, _adb.BulkTimeout(300));
            if (!Directory.Exists(Path.Combine(folder, "Saved"))) throw new PatchError("세이브 백업을 확인하지 못해 설치를 중단했습니다.");
            // 오래된 세이브 백업은 최근 5개만 남김 (각각 1MB 미만)
            foreach (var old in Directory.GetDirectories(Work, "saves-*").OrderByDescending(x => x, StringComparer.Ordinal).Skip(5))
                try { Directory.Delete(old, true); } catch { }
        }

        public bool GameRunning() => _adb.Shell(180, false, "pidof", _p.S("package")).output.Trim().Length > 0;
        public void Stopped() { if (GameRunning()) throw new PatchError("게임이 실행 중입니다. 게임을 종료하고 다시 확인해 주세요."); }

        // ───────────── 기록 ─────────────
        private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'");

        public static void AtomicJson(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".new";
            using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                var b = new UTF8Encoding(false).GetBytes(Bridge.Json.Serialize(value));
                f.Write(b, 0, b.Length); f.Flush(true);
            }
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }

        private static Dictionary<string, object> ReadJson(string path) =>
            Bridge.Json.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;

        private object V(string k) => M.TryGetValue(k, out var v) ? v : null;

        private void SaveReceipt(string current, string backup)
        {
            var old = Path.Combine(Work, "installation.json");
            if (File.Exists(old))
            {
                var raw = File.ReadAllBytes(old);
                var hist = Path.Combine(Work, "history"); Directory.CreateDirectory(hist);
                var saved = Path.Combine(hist, "receipt-" + Hash.Bytes(raw).Substring(0, 16) + ".json");
                if (!File.Exists(saved)) File.WriteAllBytes(saved, raw);
            }
            bool installed = current == _p.S("target_sha256");
            AtomicJson(old, new Dictionary<string, object>
            {
                ["game"] = V("game") ?? V("name"), ["release"] = installed ? V("release") : null, ["patch_version"] = installed ? V("release") : null,
                ["tool_build"] = V("tool_build"), ["platform"] = V("platform"), ["version_code"] = M["version_code"],
                // 초기 설치기(ITR2)가 쓰던 이름도 함께 남김
                ["device_backup"] = backup, ["installed_at"] = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff"),
                ["state"] = installed ? "installed" : "restored", ["installed_sha256"] = current, ["original_sha256"] = M["source_sha256"],
                ["device_original_backup"] = backup, ["backup_policy"] = backup == null ? "pc-only" : "pc+device", ["pc_original_backup"] = Path.Combine(Work, _p.PcOriginalName), ["verified_at"] = Now(),
                ["installer"] = "ZECOLE Launcher " + Program.Version,
            });
            AtomicJson(Path.Combine(Work, "distribution-manifest.json"), M);
        }

        private void Phase(Dictionary<string, object> j, string value)
        {
            j["phase"] = value; j["updated_at"] = Now();
            AtomicJson(Path.Combine(Work, "transaction.json"), j);
            Say(PhaseLabels.TryGetValue(value, out var l) ? l : value);
        }

        // ───────────── 잠금 (msvcrt.locking 과 호환되는 1바이트 LockFile) ─────────────
        public static IDisposable Lock(string dir)
        {
            Directory.CreateDirectory(dir);
            var f = new FileStream(Path.Combine(dir, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (f.Length == 0) { f.WriteByte((byte)'0'); f.Flush(); }
            try { f.Lock(0, 1); }
            catch (IOException) { f.Dispose(); throw new PatchError("다른 설치 또는 복구 작업이 진행 중입니다."); }
            return new Releaser(() => { try { f.Unlock(0, 1); } catch { } f.Dispose(); });
        }
        private sealed class Releaser : IDisposable { private Action _a; public Releaser(Action a) { _a = a; } public void Dispose() { _a?.Invoke(); _a = null; } }

        // ───────────── 복구 ─────────────
        public void RecoverJournal()
        {
            var file = Path.Combine(Work, "transaction.json");
            if (!File.Exists(file)) return;
            var j = ReadJson(file);
            if (Finished.Contains(j.TryGetValue("phase", out var ph) ? ph as string : null)) return;
            string main = _p.Main, backup = _p.Backup, token = j.TryGetValue("id", out var t) ? t as string ?? "" : "";
            var known = _p.KnownHashes;
            if (!Regex.IsMatch(token, "^[a-f0-9]{12}$") || G(j, "main") != main || G(j, "backup") != backup || G(j, "pending") != main + ".zecole-pending-" + token ||
                !known.Contains(G(j, "before_sha256")) || !known.Contains(G(j, "after_sha256")) || G(j, "source_sha256") != _p.S("source_sha256"))
                throw new PatchError("이전 작업 기록을 안전하게 해석할 수 없습니다. 기록과 파일을 보존했습니다.");
            VersionCheck(); Stopped();
            Say("중단된 작업의 파일 상태를 확인합니다…");
            var current = _adb.Hash(main, true); var original = _adb.Hash(backup, true);
            if (original != null && original != _p.S("source_sha256")) throw new PatchError("원본 백업이 달라 자동 복구를 중단합니다.");
            // 기기 백업은 이제 없을 수 있음 (원본은 PC 에만 보관)
            if (current == G(j, "after_sha256") && (original == null || original == _p.S("source_sha256")))
            {
                SaveReceipt(current, original == null ? null : backup); Phase(j, "complete"); CleanupOwned(j); return;
            }
            if (current == G(j, "before_sha256"))
            {
                if (_adb.Exists(G(j, "pending"))) _adb.Shell("rm", G(j, "pending"));
                Phase(j, "abandoned"); return;
            }
            var retired = G(j, "retired");
            if (current == null && (retired == backup || retired == main + ".zecole-retired-" + token) && retired != null && _adb.Hash(retired, true) == G(j, "before_sha256"))
            {
                if (retired == backup) _adb.Shell(900, true, "cp", backup, main); else _adb.Shell("mv", retired, main);
                if (_adb.Hash(main) != G(j, "before_sha256")) throw new PatchError("자동 복구 결과가 일치하지 않습니다.");
                if (_adb.Exists(G(j, "pending"))) _adb.Shell("rm", G(j, "pending"));
                Phase(j, "rolled_back"); Say("중단된 파일 교체를 직전 상태로 복구했습니다."); return;
            }
            throw new PatchError("이전 작업의 파일 상태가 불명확합니다. 자동 변경하지 않고 백업·로그를 보존했습니다.");
        }

        private static string G(Dictionary<string, object> j, string k) => j.TryGetValue(k, out var v) ? v as string : null;

        private void CleanupOwned(Dictionary<string, object> j)
        {
            var retired = G(j, "retired");
            if (retired == _p.Main + ".zecole-retired-" + G(j, "id") && _adb.Hash(retired, true) == G(j, "before_sha256")) _adb.Shell("rm", retired);
            if (_adb.Hash(G(j, "pending"), true) == G(j, "after_sha256")) _adb.Shell("rm", G(j, "pending"));
        }

        private IEnumerable<Dictionary<string, object>> OwnedRecords()
        {
            var files = new List<string> { Path.Combine(Work, "transaction.json") };
            var hist = Path.Combine(Work, "history");
            if (Directory.Exists(hist)) files.AddRange(Directory.GetFiles(hist, "transaction-*.json").OrderBy(x => x));
            foreach (var file in files)
            {
                Dictionary<string, object> j;
                try { if (!File.Exists(file)) continue; j = ReadJson(file); } catch { continue; }
                var token = j == null ? "" : G(j, "id") ?? "";
                if (!Regex.IsMatch(token, "^[a-f0-9]{12}$")) continue;
                if (G(j, "main") != _p.Main || G(j, "backup") != _p.Backup || G(j, "pending") != _p.Main + ".zecole-pending-" + token) continue;
                if (!Finished.Contains(G(j, "phase"))) continue;
                yield return j;
            }
        }

        public void SweepOwned()
        {
            int freed = 0;
            foreach (var j in OwnedRecords())
            {
                string pending = G(j, "pending"), retired = _p.Main + ".zecole-retired-" + G(j, "id");
                if (_adb.Exists(pending) && (G(j, "phase") != "complete" || _adb.Hash(pending) == G(j, "after_sha256"))) { _adb.Shell("rm", pending); freed++; }
                if (G(j, "retired") == retired && _adb.Exists(retired) && _adb.Hash(retired) == G(j, "before_sha256")) { _adb.Shell("rm", retired); freed++; }
            }
            if (freed > 0) Say($"이전에 중단된 작업이 남긴 임시 파일 {freed}개를 정리했습니다.");
        }

        private void PruneGenerated()
        {
            var root = Path.Combine(Work, "generated");
            if (!Directory.Exists(root)) return;
            foreach (var d in Directory.GetDirectories(root))
                if (Regex.IsMatch(Path.GetFileName(d), "^[a-f0-9]{64}$") && Path.GetFileName(d) != _p.S("target_sha256")) try { Directory.Delete(d, true); } catch { }
        }

        private void SpaceCheck(bool install)
        {
            Directory.CreateDirectory(Work);
            var original = Path.Combine(Work, _p.PcOriginalName);
            var generated = Path.Combine(Work, "generated", _p.S("target_sha256"), _p.S("main_name"));
            long required = (File.Exists(original) ? 0 : _p.SourceBytes) + (install && !File.Exists(generated) ? _p.TargetBytes : 0) + GiB;
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Work))).AvailableFreeSpace;
            if (free < required) throw new PatchError($"PC에 최소 {required / (double)GiB:0.0} GiB 여유 공간이 필요합니다.");
            var text = _adb.Shell("df", "-k", "/sdcard").output;
            long devFree;
            try { devFree = long.Parse(text.Split('\n').Last().Split((char[])null, StringSplitOptions.RemoveEmptyEntries)[3]) * 1024; }
            catch { throw new PatchError("Quest 여유 공간을 확인하지 못했습니다."); }
            long devReq = Math.Max(_p.SourceBytes, _p.TargetBytes) + 512L * 1024 * 1024;
            if (devFree < devReq) throw new PatchError($"Quest에 최소 {devReq / (double)GiB:0.0} GiB 여유 공간이 필요합니다.");
            Say($"작업 공간 확인 완료 · PC 추가 필요 {required / (double)GiB:0.0} GiB / Quest {devReq / (double)GiB:0.0} GiB");
        }

        private void PullVerified(string remote, string local, string expected)
        {
            if (File.Exists(local))
            {
                Say("PC 원본 백업 확인 중…");
                if (Hash.File(local) != expected) throw new PatchError("기존 PC 원본 백업이 다릅니다. 덮어쓰지 않습니다.");
                return;
            }
            var tmp = local + ".pull-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            try
            {
                long total = _p.SourceBytes;
                using (Poll(() => { try { var n = new FileInfo(tmp).Exists ? new FileInfo(tmp).Length : 0; Say($"Quest에서 원본 백업을 PC로 복사 중 · {n * 100 / total}%", n * 100.0 / total); } catch { } }))
                    _adb.Run(new[] { "pull", remote, tmp }, _adb.BulkTimeout(900));
                Say("PC 원본 백업 검증 중…");
                if (Hash.File(tmp) != expected) throw new PatchError("내려받은 원본 백업의 체크섬이 다릅니다.");
                File.Move(tmp, local);
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }

        private static IDisposable Poll(Action tick)
        {
            var t = new Timer(_ => tick(), null, 2000, 2000);
            return new Releaser(() => t.Dispose());
        }

        public string LegacyOriginal(string current)
        {
            var builds = _p.Dict("legacy_builds");
            if (current == null || !builds.ContainsKey(current)) return null;
            foreach (var path in (M.TryGetValue("legacy_original_paths", out var lp) ? lp as object[] : null) ?? new object[0])
                if (_adb.Hash((string)path, true) == _p.S("source_sha256")) return (string)path;
            return null;
        }

        public string PcOriginal => Path.Combine(Work, _p.PcOriginalName);

        /// <summary>
        /// manifest 의 legacy_pc_originals 가운데 실제로 있는 파일 (해시는 쓰기 직전에 확인).
        /// 저장 위치 밖이나 연결 폴더(정션·심볼릭 링크)를 거치는 경로는 제외.
        /// </summary>
        public List<string> LegacyPcOriginals()
        {
            var found = new List<string>();
            var root = Path.GetFullPath(_storage).TrimEnd('\\');
            if (!Directory.Exists(root) || IsLink(root)) return found;
            var serialDir = _adb.Key;
            foreach (var rel in ((M.TryGetValue("legacy_pc_originals", out var lpc) ? lpc as object[] : null) ?? new object[0]).OfType<string>())
            {
                if (serialDir.Length == 0 || !Regex.IsMatch(rel, @"^[A-Za-z0-9_.{}-]+(/[A-Za-z0-9_.{}-]+)*$") || rel.Split('/').Any(x => x == "." || x == "..")) continue;
                var old = Path.GetFullPath(Path.Combine(root, rel.Replace("{serial}", serialDir).Replace('/', '\\')));
                if (!old.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) || !File.Exists(old)) continue;
                // 파일과 그 위의 모든 폴더(저장 위치까지)가 연결 폴더가 아니어야 함
                bool linked = false;
                for (var p = old; p.Length > root.Length; p = Path.GetDirectoryName(p))
                    if (IsLink(p)) { linked = true; break; }
                if (!linked) found.Add(old);
            }
            return found;
        }

        private static bool IsLink(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; } catch { return true; }
        }

        /// <summary>
        /// 원본은 PC 한 곳에만 보관. PC 원본이 없으면 기기에 남아 있는 원본(원본 상태의 게임 파일,
        /// 이전 설치기가 만든 기기 백업, 개발 시험본 백업) 가운데 해시가 맞는 것을 PC로 가져옴.
        /// </summary>
        private void EnsurePcOriginal(string current, string saved)
        {
            var src = _p.S("source_sha256");
            if (File.Exists(PcOriginal))
            {
                Say("PC 원본 백업 확인 중…");
                if (Hash.File(PcOriginal) != src) throw new PatchError("PC 원본 백업이 예상 원본과 다릅니다. 변경하지 않습니다.");
                return;
            }
            // 이전 설치기가 같은 저장 위치에 남긴 PC 원본(예: A Fisherman's Tale devices/<기기>/…)이 있으면 해시 확인 후 옮겨 씀
            foreach (var old in LegacyPcOriginals())
            {
                Say("이전 설치기가 보관한 PC 원본 확인 중…");
                if (Hash.File(old) != src) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(PcOriginal));
                File.Move(old, PcOriginal);   // 저장 위치 안(같은 볼륨)에서만 이동
                Log.Info("legacy pc original moved: " + old);
                return;
            }
            string from = current == src ? _p.Main : saved == src ? _p.Backup : LegacyOriginal(current);
            if (from == null)
                throw new PatchError("이 PC에 원본 백업이 없어 변경하지 않습니다. 원래 게임으로 되돌리려면 Meta 앱에서 게임을 삭제한 뒤 다시 설치해 주세요.");
            SpaceCheck(false);
            PullVerified(from, PcOriginal, src);
        }

        /// <summary>Quest 단독 설치기가 이 버전을 관리 중이면 그 설치기의 복구를 위해 기기 원본 백업을 남김</summary>
        public bool MobileInstallerPresent() => _adb.Exists(_p.MobileStateDir);

        /// <summary>
        /// 기기에 남은 원본 사본 후보: 기기 원본 백업, 개발 시험본 백업, 초기 설치기(questko)가 남긴 임시 파일.
        /// 크기만 계산하며, 실제 삭제 전에는 하나씩 해시로 확인함.
        /// </summary>
        private List<(string path, long size, bool backup)> DeviceCopies(bool keepBackup)
        {
            var list = new List<(string, long, bool)>();
            var paths = new List<string>();
            if (!keepBackup) paths.Add(_p.Backup);
            paths.AddRange(((M.TryGetValue("legacy_original_paths", out var lp) ? lp as object[] : null) ?? new object[0]).Cast<string>());
            if (_p.Legacy)
            {
                var names = _adb.Shell(60, false, "ls", "-1", _p.ObbDir).output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var re = new Regex("^" + Regex.Escape(_p.S("main_name")) + @"\.(questko-(upload|retired|restore|failed)-[0-9a-f]{12}|questko-original\.upload-[0-9a-f]{12})$");
                paths.AddRange(names.Select(n => n.Trim()).Where(n => re.IsMatch(n)).Select(n => _p.ObbDir + "/" + n));
            }
            foreach (var path in paths.Distinct())
            {
                var size = _adb.RemoteSize(path);
                if (size.HasValue) list.Add((path, size.Value, path == _p.Backup));
            }
            return list;
        }

        /// <summary>PC 의 쓰지 않는 파일: 모든 버전의 생성 파일, 이전 게임 버전의 원본, (ITR2) 바꾸지 않는 두 번째 OBB 사본</summary>
        private List<(string path, long size)> PcStale()
        {
            var list = new List<(string, long)>();
            var deviceDir = Path.GetDirectoryName(Work);
            if (!Directory.Exists(deviceDir)) return list;
            long DirSize(string d) => Directory.GetFiles(d, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            foreach (var dir in Directory.GetDirectories(deviceDir))
            {
                if (!Regex.IsMatch(Path.GetFileName(dir), @"^\d+$")) continue;
                bool current = Path.GetFileName(dir) == Convert.ToString(M["version_code"]);
                if (!current)
                {
                    foreach (var f in Directory.GetFiles(dir))
                        if (Regex.IsMatch(Path.GetFileName(f), @"^original\.(obb|hdog|apk)$")) list.Add((f, new FileInfo(f).Length));
                    var originals = Path.Combine(dir, "originals");
                    if (Directory.Exists(originals)) list.Add((originals, DirSize(originals)));
                }
                else if (_p.Legacy && _p.S("patch_name") != null)
                {
                    var copy = Path.Combine(dir, "originals", _p.S("patch_name"));
                    if (File.Exists(copy)) list.Add((copy, new FileInfo(copy).Length));
                }
                var gen = Path.Combine(dir, "generated");
                if (Directory.Exists(gen)) list.Add((gen, DirSize(gen)));
            }
            return list;
        }

        /// <summary>정리할 수 있는 크기 (기기, PC)</summary>
        public (long device, long pc, bool keptForMobile) Reclaimable(string saved)
        {
            bool mobile = saved != null && MobileInstallerPresent();
            var src = _p.S("source_sha256");
            long dev = DeviceCopies(mobile).Where(x => !x.backup || saved == src).Sum(x => x.size);
            return (dev, PcStale().Sum(x => x.size), mobile);
        }

        /// <summary>
        /// 저장공간 정리: PC 원본을 해시로 확인한 뒤에만 기기 쪽 원본 사본을 지움.
        /// 게임 파일 자체와 세이브 백업은 건드리지 않음.
        /// </summary>
        public (long device, long pc) Cleanup()
        {
            VersionCheck();
            var src = _p.S("source_sha256");
            Say("Quest의 게임 파일을 확인합니다…");
            var current = _adb.Hash(_p.Main, true); var saved = _adb.Hash(_p.Backup, true);
            if (!_p.KnownHashes.Contains(current)) throw new PatchError("알 수 없거나 업데이트된 파일입니다. 변경하지 않습니다.");
            EnsurePcOriginal(current, saved);   // PC 원본이 확실할 때만 진행 (없으면 기기 사본에서 먼저 가져옴)
            bool mobile = MobileInstallerPresent();
            if (mobile) Say("Quest 단독 설치기가 사용하는 기기 원본 백업은 남겨 둡니다.");
            long dev = 0, pc = 0;
            foreach (var (path, size, isBackup) in DeviceCopies(mobile))
            {
                Say("기기의 원본 사본 확인 중… " + Path.GetFileName(path));
                var h = _adb.Hash(path, true);
                // 기기 원본 백업·시험본은 원본과 같을 때만, 초기 설치기 임시 파일은 알려진 파일(원본·한글판)일 때만 지움
                bool ok = isBackup || !_p.Legacy || path.Contains("questko-original") ? h == src : _p.KnownHashes.Contains(h);
                if (!ok) { Say("확인되지 않은 파일이라 남겨 둡니다: " + Path.GetFileName(path)); continue; }
                _adb.Shell("rm", path);
                if (_adb.Exists(path)) throw new PatchError("기기 파일을 지우지 못했습니다: " + path);
                dev += size;
            }
            foreach (var (path, size) in PcStale())
            {
                if (Directory.Exists(path)) Directory.Delete(path, true); else File.Delete(path);
                pc += size;
            }
            SaveReceipt(current, !mobile || saved != src ? null : _p.Backup);
            Say("저장공간 정리 완료", 100);
            return (dev, pc);
        }

        // ───────────── 한글판 생성 (PC) ─────────────
        public static string Build(QuestPackage p, string source, string output, Action<string, double?> say)
        {
            source = Path.GetFullPath(source); output = Path.GetFullPath(output);
            if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase)) throw new PatchError("원본과 결과 경로는 달라야 합니다.");
            long target = p.TargetBytes;
            say("원본 게임 데이터 체크섬 확인 중…", null);
            if (new FileInfo(source).Length != p.SourceBytes || Hash.File(source, n => say($"원본 게임 데이터 확인 중 · {n * 100 / p.SourceBytes}%", null)) != p.S("source_sha256"))
                throw new PatchError($"지원하는 Quest {p.S("version_name")} / 빌드 {p.M["version_code"]} 원본이 아닙니다. 수정하거나 설치하지 않았습니다.");
            if (File.Exists(output))
            {
                say("이전에 만든 한글판 파일 확인 중…", null);
                if (Hash.File(output) == p.S("target_sha256")) { say("이미 검증된 결과 파일이 있습니다.", null); return output; }
                throw new PatchError("결과 경로에 다른 파일이 있습니다. 덮어쓰지 않습니다.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            if (new DriveInfo(Path.GetPathRoot(output)).AvailableFreeSpace < target + 512L * 1024 * 1024) throw new PatchError("결과 파일을 만들 PC 여유 공간이 부족합니다.");
            var tmp = output + ".building-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                using (var payload = new FileStream(p.PayloadPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                using (var dst = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    long written = 0, lastNotice = 0;
                    var buf = new byte[CHUNK];
                    void Write(byte[] b, int n)
                    {
                        dst.Write(b, 0, n); sha.TransformBlock(b, 0, n, null, 0); written += n;
                        if (written - lastNotice >= 64L * 1024 * 1024) { lastNotice = written; say($"패치 생성 {written * 100 / target}%", written * 100.0 / target); }
                    }
                    foreach (var op in p.Ops)
                    {
                        var kind = (string)op[0]; long offset = Convert.ToInt64(op[1]), count = Convert.ToInt64(op[2]);
                        if (kind == "bsdiff")
                        {
                            long oldOff = Convert.ToInt64(op[3]); int oldLen = Convert.ToInt32(op[4]), patchLen = Convert.ToInt32(op[5]);
                            var old = new byte[oldLen]; src.Seek(oldOff, SeekOrigin.Begin); ReadExact(src, old, oldLen);
                            var patch = new byte[patchLen]; payload.Seek(offset, SeekOrigin.Begin); ReadExact(payload, patch, patchLen);
                            if (patch.Length < 32 || Encoding.ASCII.GetString(patch, 0, 8) != "BSDIFF40" || BitConverter.ToInt64(patch, 24) != count)
                                throw new PatchError("차분 출력 크기가 다릅니다.");
                            var block = Bspatch.Apply(old, patch);
                            if (block.Length != count || Hash.Bytes(block) != (string)op[6]) throw new PatchError("차분 복원 결과가 다릅니다.");
                            Write(block, block.Length);
                        }
                        else if (kind == "zlib")
                        {
                            // 원본 구간을 zlib 1.2.13 레벨 9로 다시 압축 (원본 폰트는 배포하지 않고 사용자 파일에서 재생성)
                            int rawLen = Convert.ToInt32(op[3]);
                            var raw = new byte[rawLen]; src.Seek(offset, SeekOrigin.Begin); ReadExact(src, raw, rawLen);
                            var block = Zlib.Compress9(raw);
                            if (block.Length != count || Hash.Bytes(block) != (string)op[4])
                                throw new PatchError("원본 폰트 재생성 결과가 다릅니다. 기기는 변경하지 않았습니다.");
                            Write(block, block.Length);
                        }
                        else if (kind == "rezlib")
                        {
                            // 원본의 zlib 조각을 풀어 zlib 1.2.13 레벨 9로 다시 압축 (원본 게임 데이터는 배포하지 않음)
                            int srcLen = Convert.ToInt32(op[3]);
                            var comp = new byte[srcLen]; src.Seek(offset, SeekOrigin.Begin); ReadExact(src, comp, srcLen);
                            var block = Zlib.Compress9(Zlib.Inflate(comp, 2 * 1024 * 1024));
                            if (block.Length != count || Hash.Bytes(block) != (string)op[4])
                                throw new PatchError("원본 데이터 재압축 결과가 다릅니다. 기기는 변경하지 않았습니다.");
                            Write(block, block.Length);
                        }
                        else if (kind == "zero")
                        {
                            Array.Clear(buf, 0, buf.Length);
                            while (count > 0) { int n = (int)Math.Min(count, CHUNK); Write(buf, n); count -= n; }
                        }
                        else
                        {
                            var f = kind == "copy" ? src : payload;
                            f.Seek(offset, SeekOrigin.Begin);
                            while (count > 0)
                            {
                                int n = f.Read(buf, 0, (int)Math.Min(count, CHUNK));
                                if (n <= 0) throw new PatchError("입력 파일이 중간에 끝났습니다.");
                                Write(buf, n); count -= n;
                            }
                        }
                    }
                    sha.TransformFinalBlock(buf, 0, 0);
                    dst.Flush(true);
                    var digest = string.Concat(sha.Hash.Select(b => b.ToString("x2")));
                    if (written != target || digest != p.S("target_sha256")) throw new PatchError("생성 결과가 검증된 한글패치와 다릅니다. 설치를 중단했습니다.");
                }
                if (File.Exists(output)) throw new PatchError("결과 경로에 파일이 생겨 덮어쓰기를 중단했습니다.");
                File.Move(tmp, output);
                say("완료: 결과 SHA-256이 검증본과 일치합니다.", 100);
                return output;
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }

        private static void ReadExact(Stream s, byte[] b, int count)
        {
            int off = 0;
            while (off < count) { int n = s.Read(b, off, count - off); if (n <= 0) throw new PatchError("차분 입력 파일이 불완전합니다."); off += n; }
        }

        // ───────────── 설치·복구 트랜잭션 ─────────────
        public void Transact(string action)
        {
            RecoverJournal(); VersionCheck(); Stopped();
            SweepOwned(); PruneGenerated();
            string main = _p.Main, backup = _p.Backup, src = _p.S("source_sha256");
            Say("Quest의 게임 파일을 확인합니다…");
            var current = _adb.Hash(main, true); var saved = _adb.Hash(backup, true);
            var desired = action == "install" ? _p.S("target_sha256") : src;
            if (!_p.KnownHashes.Contains(current)) throw new PatchError("알 수 없거나 업데이트된 파일입니다. 변경하지 않습니다.");
            PatchObbCheck();
            EnsurePcOriginal(current, saved);
            if (current == desired)
            {
                SaveReceipt(current, saved == src ? backup : null);
                Say(action == "install" ? "동일한 파일 검증 완료 · 설치 기록을 확인했습니다." : "이미 원본 상태입니다.");
                return;
            }
            SpaceCheck(action == "install");
            // 보낼 파일: 설치 = PC 원본으로 만든 한글판, 복구 = PC 원본
            string output = action == "install"
                ? Build(_p, PcOriginal, Path.Combine(Work, "generated", _p.S("target_sha256"), _p.S("main_name")), _say)
                : PcOriginal;
            if (action == "install") BackupSaves();
            var token = Guid.NewGuid().ToString("N").Substring(0, 12);
            var pending = main + ".zecole-pending-" + token;
            var retired = main + ".zecole-retired-" + token;   // 교체하는 동안만 잠시 보관, 검증 후 삭제
            if (_adb.Exists(pending) || _adb.Exists(retired)) throw new PatchError("작업 파일 이름이 충돌하여 중단했습니다.");
            var j = new Dictionary<string, object>
            {
                ["id"] = token, ["action"] = action, ["main"] = main, ["backup"] = backup, ["pending"] = pending, ["retired"] = retired,
                ["before_sha256"] = current, ["after_sha256"] = desired, ["source_sha256"] = src, ["created_at"] = Now(),
                ["backup_policy"] = "pc-only",
            };
            var old = Path.Combine(Work, "transaction.json");
            if (File.Exists(old))
            {
                var hist = Path.Combine(Work, "history"); Directory.CreateDirectory(hist);
                File.WriteAllBytes(Path.Combine(hist, "transaction-" + Guid.NewGuid().ToString("N") + ".json"), File.ReadAllBytes(old));
            }
            Phase(j, "prepared"); Phase(j, "transferring");
            long total = action == "install" ? _p.TargetBytes : _p.SourceBytes;
            var probe = new Adb(_adb.Exe, _adb.Serial) { DeviceId = _adb.DeviceId };
            using (Poll(() => { var n = probe.RemoteSize(pending); if (n.HasValue) Say($"Quest로 전송 중 · {n.Value * 100 / total}%", n.Value * 100.0 / total); }))
                _adb.Run(new[] { "push", output, pending }, _adb.BulkTimeout(900));
            if (_p.Format >= 2) // 형식 2(H3VR2) 설치기만 권한을 맞춤. 형식 1 설치기는 하지 않음
            {
                _adb.Shell("chmod", "0666", pending);
                if (_adb.Shell("stat", "-c", "%a", pending).output.Trim() != "666") throw new PatchError("기기 파일 권한을 확인하지 못했습니다.");
            }
            Say("Quest에서 전송 파일 검증 중…");
            if (_adb.Hash(pending) != desired) throw new PatchError("전송 파일 검증 실패. 다음 실행에서 기록에 따라 복구합니다.");
            Phase(j, "staged_verified");
            VersionCheck(); Stopped();
            if (_adb.Hash(main, true) != current || _adb.Hash(backup, true) != saved) throw new PatchError("교체 직전 게임 파일이 변경되어 중단했습니다.");
            Stopped(); Phase(j, "swapping");
            try
            {
                if (current != null) _adb.Shell("mv", main, retired);
                _adb.Shell("mv", pending, main);
                if (_adb.Hash(main) != desired) throw new PatchError("교체 후 검증 실패");
            }
            catch
            {
                try { RecoverJournal(); } catch (Exception r) { Say("자동 복구 보류: " + r.Message); }
                throw;
            }
            SaveReceipt(desired, saved == src ? backup : null); Phase(j, "complete");
            try { CleanupOwned(j); } catch { Say("임시 파일 정리를 마치지 못했습니다. 다음 실행 때 기록을 확인해 정리합니다."); }
            // PC 에 만든 한글판은 다시 만드는 데 몇 초면 되므로 남기지 않음
            try { var gen = Path.Combine(Work, "generated"); if (Directory.Exists(gen)) Directory.Delete(gen, true); } catch { }
            Say(action == "install" ? "설치 완료" : "원본 복구 완료", 100);
        }
    }
}
