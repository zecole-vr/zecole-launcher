using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Zecole.Launcher.Quest;

namespace Zecole.Launcher.Pc
{
    /// <summary>
    /// CONVRGENCE (Steam PCVR) 한글패치 엔진.
    /// 공개 설치기 1.2.5 의 steam_patch.py + integrated.py 를 같은 순서·같은 기록 형식으로 옮김:
    ///  - 게임 데이터 20개: 원본 해시 확인 → 원본 백업(originals) → ZKO1 차분으로 결과 생성·검증 → 교체
    ///  - 자막 런타임 232개(BepInEx·dotnet·자막 플러그인): 소유 기록(runtime-ownership.json)으로 공유 로더/제콜 소유 파일 구분
    ///  - 작업 기록(transaction.json, schema 2)으로 실패·강제 중단 시 검증된 이전 상태로 복구
    /// 저장 위치·폴더 이름·JSON 필드는 기존 설치기와 같아서, 설치기로 설치한 게임을 그대로 이어서 관리함.
    /// </summary>
    internal sealed class ConvrgenceSteam : PcPatch
    {
        public const string UnityLibUrl = "https://unity.bepinex.dev/libraries/2019.4.2.zip";
        /// <summary>백업·기록·로그 (기존 설치기와 같은 %LOCALAPPDATA%\ZECOLE\CONVRGENCE). 자체 시험에서만 바꿈</summary>
        public string Storage = StorageUnder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        /// <summary>
        /// 기존 설치기(launcher.py)는 LOCALAPPDATA\"ZECOLE"\CONVRGENCE 로 적었으므로 대문자 그대로 씀.
        /// 실제 폴더(Zecole)와 같은 곳이지만, installation.json 의 backup 문자열이 기존 기록과 똑같아짐.
        /// </summary>
        public static string StorageUnder(string localAppData) => Path.Combine(localAppData, "ZECOLE", "CONVRGENCE");
        /// <summary>자체 시험용: n번째 파일 적용 뒤 실패 / 강제 종료(86)</summary>
        public int FailAfter, InterruptAfter;

        public override string[] Processes => new[] { "CONVRGENCE" };
        public override string WriteDir => Root;
        /// <summary>교체 대상이 있는 폴더들 (하나라도 쓸 수 없으면 관리자 권한)</summary>
        public override IEnumerable<string> WriteDirs =>
            new[] { Root, Path.Combine(Root, "CONVRGENCE_Data"), Path.Combine(Root, @"CONVRGENCE_Data\il2cpp_data\Metadata"), Path.Combine(Root, "BepInEx"), Path.Combine(Root, "dotnet") }
            .Where(d => d == Root || Directory.Exists(d));
        /// <summary>중단 복구·영수증 갱신은 엔진이 잠금 안에서 하므로, 화면 상태와 상관없이 항상 엔진을 실행</summary>
        public override bool AlwaysRunEngine => true;
        public override bool LooksLikeGame(string root) => File.Exists(Path.Combine(root, "CONVRGENCE.exe")) && Directory.Exists(Path.Combine(root, "CONVRGENCE_Data"));
        public override string PackageVersion => S(Manifest, "release");

        private Dictionary<string, object> _m;
        private Dictionary<string, object> Manifest => _m ?? (_m = LoadRelease(Pkg));

        // ───────────── 공용 도우미 (steam_patch.py 와 같은 동작) ─────────────
        private static string S(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) ? v as string : null;
        private static Dictionary<string, object> D(object o) => o as Dictionary<string, object>;
        private static IEnumerable<Dictionary<string, object>> Arr(Dictionary<string, object> d, string k) =>
            d.TryGetValue(k, out var v) && v is object[] a ? a.Select(D) : Enumerable.Empty<Dictionary<string, object>>();
        private static IEnumerable<string> Strs(Dictionary<string, object> d, string k) =>
            d != null && d.TryGetValue(k, out var v) && v is object[] a ? a.Select(x => x as string) : Enumerable.Empty<string>();
        private static long L(Dictionary<string, object> d, string k) => Convert.ToInt64(d[k]);

        /// <summary>기록 값이 null 이거나 64자리 소문자 해시인지</summary>
        private static bool HashOrNull(object v) => v == null || (v is string s && Regex.IsMatch(s, "^[0-9a-f]{64}$"));

        private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'");
        private static string Digest(string p) => Hash.File2(p);
        private string HashOrNone(string p)
        {
            if (Directory.Exists(p)) throw new PatchError("파일 경로에 폴더가 있습니다: " + p);
            return File.Exists(p) ? H(p) : null;
        }

        private static Dictionary<string, object> Read(string path) =>
            Bridge.Json.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object> ?? throw new PatchError("기록 파일을 읽지 못했습니다: " + path);

        /// <summary>임시 파일에 쓰고 디스크에 기록한 뒤 한 번에 바꿔치기 (os.replace)</summary>
        private static void Save(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                var b = new UTF8Encoding(false).GetBytes(Bridge.Json.Serialize(value));
                f.Write(b, 0, b.Length); f.Flush(true);
            }
            Native.Replace(temp, path);
        }

        /// <summary>
        /// safe(): 상대 경로 검사 + 기준 폴더 이탈·연결(reparse) 폴더/파일 거부.
        /// 돌려주는 경로는 실제 파일 작업용으로 \\?\ 접두사를 붙임 — 작업 사본·이전 자막 보관 경로가 260자를 넘어도
        /// (Windows 긴 경로 설정이 꺼진 PC 포함) 실패하지 않게 함. 기록 JSON 에는 이 경로를 쓰지 않음.
        /// </summary>
        private static string Safe(string baseDir, string relative)
        {
            var b = Native.Resolve(baseDir);
            if (string.IsNullOrEmpty(relative) || relative.Contains(":") || relative.Contains("\\") || relative.StartsWith("/") || relative.Split('/').Any(x => x == ".." || x == "."))
                throw new PatchError("잘못된 패치 경로입니다.");
            var p = Path.Combine(b, relative.Replace('/', '\\'));
            var full = Native.Resolve(p);
            if (!(full + "\\").StartsWith(b.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new PatchError("패치 경로가 대상 폴더를 벗어났습니다.");
            for (var x = p; x != null; x = Path.GetDirectoryName(x))
            {
                var lx = Native.Long(x);
                if (File.Exists(lx) || Directory.Exists(lx))
                    if ((File.GetAttributes(lx) & FileAttributes.ReparsePoint) != 0) throw new PatchError("연결된 폴더/파일에는 설치하지 않습니다: " + x);
                if (string.Equals(x.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) break;
            }
            return Native.Long(p);
        }

        // ───────────── 배포 데이터 ─────────────
        /// <summary>integrated.load_release: 대상 플랫폼·중복·경로·차분/런타임 원본 해시·허용 다운로드 확인</summary>
        internal static Dictionary<string, object> LoadRelease(string bundle)
        {
            var m = Read(Path.Combine(bundle, "manifest.json"));
            if (S(m, "platform") != "steam-pcvr" || S(m, "host") != "windows-x64" || S(m, "steam_app") != "2609610") throw new PatchError("대상 플랫폼이 다릅니다.");
            // 필수 목록·필드 확인 (누락을 빈 목록으로 읽어 자막 없이 설치하는 일이 없게)
            foreach (var k in new[] { "files", "runtime_files" })
                if (!(m.TryGetValue(k, out var lv) && lv is object[] la && la.Length > 0 && la.All(x => x is Dictionary<string, object>))) throw new PatchError("배포 정보가 손상되었습니다: " + k);
            foreach (var k in new[] { "release", "steam_build", "game_version", "installer_version", "legacy_backup" })
                if (string.IsNullOrEmpty(S(m, k))) throw new PatchError("배포 정보가 손상되었습니다: " + k);
            bool Int(object o) => o is int || o is long;
            foreach (var f in Arr(m, "files"))
                if (!Int(f.TryGetValue("original_bytes", out var a) ? a : null) || !Int(f.TryGetValue("patched_bytes", out var b) ? b : null)
                    || new[] { "name", "patch", "patch_sha256", "original_sha256", "patched_sha256" }.Any(k => string.IsNullOrEmpty(S(f, k))))
                    throw new PatchError("배포 정보의 게임 파일 항목이 손상되었습니다.");
            foreach (var f in Arr(m, "runtime_files"))
                if (!Int(f.TryGetValue("bytes", out var a) ? a : null) || new[] { "name", "sha256", "policy" }.Any(k => string.IsNullOrEmpty(S(f, k)))
                    || !(f.TryGetValue("accepted", out var acc) && acc is object[]) || (S(f, "source") == null && S(f, "download") == null))
                    throw new PatchError("배포 정보의 자막 구성 항목이 손상되었습니다.");
            var files = Arr(m, "files").ToList();
            if (files.Select(f => S(f, "name")).Distinct().Count() != files.Count) throw new PatchError("패치 목록이 중복되었습니다.");
            foreach (var f in files)
            {
                Safe(bundle, S(f, "name"));
                if (Digest(Safe(Path.Combine(bundle, "payload"), S(f, "patch"))) != S(f, "patch_sha256")) throw new PatchError("패치 데이터 손상: " + S(f, "patch"));
            }
            var names = new HashSet<string>();
            foreach (var f in Arr(m, "runtime_files"))
            {
                if (!names.Add(S(f, "name"))) throw new PatchError("중복 자막 구성 파일");
                Safe(bundle, S(f, "name"));
                if (S(f, "source") != null && Digest(Safe(bundle, S(f, "source"))) != S(f, "sha256")) throw new PatchError("자막 구성 파일 손상: " + S(f, "name"));
                if (S(f, "download") != null && S(f, "download") != UnityLibUrl) throw new PatchError("허용되지 않은 자막 도구 다운로드");
            }
            return m;
        }

        // ───────────── 게임 확인 ─────────────
        private static bool IsRunning() => System.Diagnostics.Process.GetProcessesByName("CONVRGENCE").Length > 0;

        /// <summary>validate_game: 게임 폴더 구조, Steam 설치 정보(앱·빌드·설치 완료·폴더 이름), 실행 여부</summary>
        private static string ValidateGame(string root, Dictionary<string, object> m, bool checkRunning = true)
        {
            root = Native.Resolve(root);
            if (!File.Exists(Path.Combine(root, "CONVRGENCE.exe")) || !Directory.Exists(Path.Combine(root, "CONVRGENCE_Data"))) throw new PatchError("CONVRGENCE.exe가 있는 Steam 게임 폴더를 선택하세요.");
            Safe(root, "CONVRGENCE.exe");
            var acf = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(root)) ?? "", "appmanifest_2609610.acf");
            if (!File.Exists(acf)) throw new PatchError("Steam 설치 정보를 찾지 못했습니다. Steam 라이브러리의 게임 폴더를 선택하세요.");
            var text = File.ReadAllText(acf, Encoding.UTF8);
            string Value(string key) { var mt = Regex.Match(text, "\"" + key + "\"\\s+\"([^\"]*)\""); return mt.Success ? mt.Groups[1].Value : null; }
            if (Value("appid") != S(m, "steam_app") || Value("buildid") != S(m, "steam_build") || Value("StateFlags") != "4" || Value("installdir")?.ToLowerInvariant() != Path.GetFileName(root).ToLowerInvariant())
                throw new PatchError("지원 게임: " + S(m, "game_version") + " / Steam 빌드 " + S(m, "steam_build") + ". 업데이트 중이거나 지원하지 않는 버전입니다.");
            if (checkRunning && IsRunning()) throw new PatchError("CONVRGENCE를 완전히 종료한 뒤 다시 확인해 주세요.");
            return root;
        }

        /// <summary>installation_store: 게임 경로(실제 경로, 소문자) SHA-256 앞 20자 / Steam 빌드</summary>
        internal static string InstallationStore(string storage, string root, Dictionary<string, object> m) =>
            Path.Combine(storage, "installations", Hash.Bytes(Encoding.UTF8.GetBytes(Native.Resolve(root).ToLowerInvariant())).Substring(0, 20), S(m, "steam_build"));

        private sealed class AssetEntry { public Dictionary<string, object> F; public string Target, Current, Source, Backup; }

        /// <summary>steam_patch.inspect: 게임 데이터 20개의 상태와 복구용 원본 위치</summary>
        private (string state, List<AssetEntry> entries) BaseInspect(string root, Dictionary<string, object> m, string store)
        {
            ValidateGame(root, m, false);
            var data = Path.Combine(root, "CONVRGENCE_Data"); var list = new List<AssetEntry>();
            foreach (var f in Arr(m, "files"))
            {
                var name = S(f, "name"); var t = Safe(data, name);
                if (!File.Exists(t)) throw new PatchError("게임 파일이 없습니다: " + name + "\nSteam 무결성 검사로 원본을 복원해 주세요.");
                var h = H(t);
                var ok = new HashSet<string>(Strs(f, "previous_patched_sha256")) { S(f, "original_sha256"), S(f, "patched_sha256") };
                if (!ok.Contains(h)) throw new PatchError("알 수 없는 게임 파일: " + name + "\n다른 모드 또는 업데이트 여부를 확인해 주세요.");
                var backup = Safe(Path.Combine(store, "originals"), name); var legacy = Safe(Path.Combine(root, S(m, "legacy_backup")), name);
                string source;
                if (File.Exists(backup)) { if (H(backup) != S(f, "original_sha256")) throw new PatchError("원본 백업 손상: " + name); source = backup; }
                else if (File.Exists(legacy)) { if (H(legacy) != S(f, "original_sha256")) throw new PatchError("이전 설치판의 백업 손상: " + name); source = legacy; }
                else if (h == S(f, "original_sha256")) source = t;
                else throw new PatchError("복구용 원본 백업이 없습니다: " + name + "\nSteam 무결성 검사로 원본을 복원해 주세요.");
                list.Add(new AssetEntry { F = f, Target = t, Current = h, Source = source, Backup = backup });
            }
            var state = list.All(e => e.Current == S(e.F, "original_sha256")) ? "original" : list.All(e => e.Current == S(e.F, "patched_sha256")) ? "patched" : "mixed";
            return (state, list);
        }

        // ───────────── 자막 런타임 (integrated.py) ─────────────
        private static bool OthersUseLoader(string root)
        {
            var own = Path.Combine(root, @"BepInEx\plugins\ZecoleRadioSubtitles") + "\\";
            foreach (var folder in new[] { @"BepInEx\plugins", @"BepInEx\patchers" })
            {
                var dir = Path.Combine(root, folder);
                if (!Directory.Exists(dir)) continue;
                foreach (var p in Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories))
                    if (p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !p.StartsWith(own, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>이전 빌드(manifest 의 runtime_migrate_builds)의 자막 소유 기록만 이어받음. 읽기만 함</summary>
        private static (Dictionary<string, object> ledger, string oldStore) PriorRuntimeLedger(string root, Dictionary<string, object> m, string store)
        {
            var cands = new List<(Dictionary<string, object>, string)>();
            foreach (var build in Strs(m, "runtime_migrate_builds"))
            {
                if (build == null || !Regex.IsMatch(build, "^[0-9]+$") || build == S(m, "steam_build")) throw new PatchError("잘못된 이전 빌드 설정");
                var old = Path.Combine(Path.GetDirectoryName(store), build);
                string rp = Path.Combine(old, "installation.json"), lp = Path.Combine(old, "runtime-ownership.json");
                if (!File.Exists(rp) || !File.Exists(lp)) continue;
                var receipt = Read(rp);
                if (S(receipt, "state") != "installed") continue;
                var vers = Strs(m, "runtime_migrate_patch_versions").ToList(); if (!m.ContainsKey("runtime_migrate_patch_versions")) vers = new List<string> { "1.4.0" };
                if (S(receipt, "game_root") != Native.Resolve(root) || S(receipt, "steam_build") != build || !vers.Contains(S(receipt, "patch_version")))
                    throw new PatchError("이전 설치 기록의 대상/버전을 확인해야 합니다.");
                var tx = Path.Combine(old, "transaction.json");
                if (File.Exists(tx)) { var st = S(Read(tx), "state"); if (st != "completed" && st != "rolled_back") throw new PatchError("이전 빌드의 중단된 설치 작업을 먼저 확인해야 합니다."); }
                cands.Add((Read(lp), old));
            }
            if (cands.Count > 1) throw new PatchError("이전 자막 설치 기록이 여러 개입니다. 원본 기록 확인이 필요합니다.");
            return cands.Count == 1 ? cands[0] : (null, null);
        }

        private sealed class Entry
        {
            public string Key, Name, Scope, Target, Current, After, Backup, Baseline, BaselineSource;
            public Dictionary<string, object> Spec; public bool KeepShared;
        }

        private (List<Entry> runtime, Dictionary<string, object> ledger) RuntimePlan(string root, Dictionary<string, object> m, string store, string action = "install")
        {
            var ledgerPath = Path.Combine(store, "runtime-ownership.json");
            string previous = null; Dictionary<string, object> ledger;
            if (File.Exists(ledgerPath)) ledger = Read(ledgerPath);
            else
            {
                (ledger, previous) = PriorRuntimeLedger(root, m, store);
                if (ledger == null) ledger = new Dictionary<string, object> { ["schema"] = 1, ["root"] = Native.Resolve(root), ["files"] = new Dictionary<string, object>() };
            }
            var lf = D(ledger["files"]);
            var specNames = new HashSet<string>(Arr(m, "runtime_files").Select(f => S(f, "name")));
            if (S(ledger, "root") != Native.Resolve(root) || lf.Keys.Any(k => !specNames.Contains(k))) throw new PatchError("다른 버전의 자막 설치 기록을 확인해야 합니다.");
            bool keepShared = action == "restore" && OthersUseLoader(root);
            var result = new List<Entry>();
            foreach (var f in Arr(m, "runtime_files"))
            {
                var name = S(f, "name"); var target = Safe(root, name); var current = HashOrNone(target);
                var known = new HashSet<string>(Strs(f, "accepted"));
                var old = lf.TryGetValue(name, out var ov) ? D(ov) : null;
                if (old != null)
                {
                    // 기록의 baseline 은 키가 반드시 있어야 함 (없는 키를 null 로 읽어 공유 로더를 지우지 않게)
                    if (!old.ContainsKey("baseline") || !HashOrNull(old["baseline"]) || !(old.TryGetValue("installed_hash", out var ih) && ih is string))
                        throw new PatchError("자막 설치 기록이 손상되었습니다: " + name);
                    if (S(old, "policy") != S(f, "policy") || !known.Contains(S(old, "installed_hash"))) throw new PatchError("지원하지 않는 이전 자막 설치 기록: " + name);
                    if (S(old, "baseline") != null) known.Add(S(old, "baseline"));
                    known.Add(S(old, "installed_hash"));
                }
                if (current != null && !known.Contains(current)) throw new PatchError("다른 모드 또는 수정된 자막 파일을 보존했습니다: " + name + "\n기존 파일을 확인한 뒤 다시 설치해 주세요.");
                if (old == null)
                {
                    old = new Dictionary<string, object> { ["baseline"] = S(f, "policy") == "shared" ? current : null, ["installed_hash"] = S(f, "sha256"), ["policy"] = S(f, "policy") };
                    lf[name] = old;
                }
                var baseline = S(old, "baseline");
                var backup = Safe(Path.Combine(store, "runtime-originals"), name);
                var baselineSource = previous != null ? Safe(Path.Combine(previous, "runtime-originals"), name) : backup;
                if (previous != null && baseline != null && HashOrNone(baselineSource) != baseline) throw new PatchError("이전 자막 원본 백업이 없거나 손상되었습니다: " + name);
                if ((File.Exists(backup) || Directory.Exists(backup)) && HashOrNone(backup) != baseline) throw new PatchError("자막 실행 환경 백업 손상: " + name);
                if (baseline != null && !File.Exists(baselineSource) && current != baseline) throw new PatchError("자막 실행 환경의 원본 백업이 없습니다: " + name);
                var expected = action == "install" ? S(f, "sha256") : baseline;
                if (keepShared && S(f, "policy") == "shared" && expected == null) expected = current;
                result.Add(new Entry { Key = "runtime/" + name, Name = name, Scope = "game", Target = target, Current = current, After = expected, Baseline = baseline, Backup = backup, BaselineSource = baselineSource, Spec = f, KeepShared = keepShared });
            }
            return (result, ledger);
        }

        /// <summary>integrated.inspect: 게임 데이터 상태 + 자막 런타임 완전성</summary>
        private (string state, List<AssetEntry> assets) FullInspect(string root, Dictionary<string, object> m, string store)
        {
            var (state, assets) = BaseInspect(root, m, store);
            var (runtime, _) = RuntimePlan(root, m, store);
            bool complete = runtime.All(e => e.Current == S(e.Spec, "sha256"));
            bool absentOwned = runtime.Where(e => S(e.Spec, "policy") == "owned").All(e => e.Current == null);
            if (state == "patched" && !complete) state = "mixed";
            if (state == "original" && !absentOwned) state = "mixed";
            return (state, assets);
        }

        // ───────────── 화면용 상태 (읽기만) ─────────────
        public override Dictionary<string, object> Inspect()
        {
            try
            {
                var m = Manifest; var root = Native.Resolve(Root); var store = InstallationStore(Storage, root, m);
                var (state, _) = FullInspect(root, m, store);
                var jpath = Path.Combine(store, "transaction.json");
                var pending = File.Exists(jpath) && S(Read(jpath), "state") is var js && js != "completed" && js != "rolled_back";
                string note = pending ? "중단된 작업 기록이 있습니다. 다음 설치/복구 때 검증 후 이전 상태로 복구합니다." : null;
                if (state == "patched") return State("installed", S(m, "release"), true, note);
                if (state == "original") return State("original", null, false, note);
                var rp = Path.Combine(store, "installation.json");
                var rec = File.Exists(rp) ? Read(rp) : null;
                var ver = S(rec, "state") == "installed" ? S(rec, "patch_version") : null;
                return State("mixed", ver ?? "이전 버전", true, note ?? "한글패치가 일부만 적용되어 있거나 이전 버전입니다. 설치하면 최신으로 맞추고, 제거하면 원본으로 되돌립니다.");
            }
            catch (PatchError ex) { return State("other", null, false, ex.Message); }
        }

        // ───────────── 보조 라이브러리 (첫 설치 때 한 번 받음) ─────────────
        /// <summary>
        /// Unity 보조 라이브러리는 Unity 소유 파일이라 재배포하지 않고, 기존 설치기처럼 BepInEx 공식 주소에서 받아 크기·SHA-256 확인.
        /// 관리자 권한 요청 전에 일반 권한으로 미리 받아 둠 (storage\downloads\&lt;sha256&gt;.zip, 기존 설치기와 같은 캐시).
        /// </summary>
        public override void Prefetch(Action<string, double?> say)
        {
            // 실패해도 여기서 멈추지 않음: 엔진이 잠금 안에서 중단 복구를 마친 뒤 필요하면 다시 받음 (기존 설치기와 같은 순서)
            try
            {
                var m = Manifest; var root = Native.Resolve(Root);
                foreach (var f in Arr(m, "runtime_files").Where(f => S(f, "download") != null))
                {
                    if (HashOrNone(Safe(root, S(f, "name"))) == S(f, "sha256")) continue;
                    Download(f, say);
                }
            }
            catch (Exception ex) { Log.Info("convrgence prefetch: " + ex.Message); }
        }

        private string Download(Dictionary<string, object> f, Action<string, double?> say)
        {
            var sha = S(f, "sha256");
            var cache = Path.Combine(Storage, "downloads", sha + ".zip"); Directory.CreateDirectory(Path.GetDirectoryName(cache));
            if (File.Exists(cache))
            {
                if (Digest(cache) == sha) return cache;
                throw new PatchError("도구 다운로드 캐시가 손상되었습니다: " + cache);
            }
            if (S(f, "download") != UnityLibUrl) throw new PatchError("허용되지 않은 자막 도구 다운로드");
            var temp = cache + "." + Guid.NewGuid().ToString("N") + ".download";
            say("첫 설치 준비: Unity 2019.4.2 보조 라이브러리 다운로드 및 무결성 검사", 0);
            try
            {
                using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(130)))
                    Net.DownloadFileAsync(UnityLibUrl, temp, (d, t) => { }, cts.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (!(ex is PatchError))
            {
                try { File.Delete(temp); } catch { }
                throw new PatchError("보조 라이브러리를 다운로드하지 못했습니다. 인터넷 연결을 확인한 뒤 재시도해 주세요. 게임을 변경하지 않았습니다.");
            }
            if (new FileInfo(temp).Length != L(f, "bytes") || Digest(temp) != sha)
            {
                try { File.Delete(temp); } catch { }
                throw new PatchError("Unity 보조 라이브러리 다운로드 검증 실패. 게임을 변경하지 않았습니다.");
            }
            Native.Replace(temp, cache);
            return cache;
        }

        private string Obtain(Dictionary<string, object> f, string current, Action<string, double?> say)
        {
            if (S(f, "source") != null) return Safe(Pkg, S(f, "source"));
            if (HashOrNone(current) == S(f, "sha256")) return current;
            return Download(f, say);
        }

        // ───────────── 파일 처리 ─────────────
        private static void Exact(Stream s, byte[] buf, int n)
        {
            int off = 0;
            while (off < n) { int r = s.Read(buf, off, n - off); if (r <= 0) throw new PatchError("패치 데이터가 잘렸습니다."); off += r; }
        }

        /// <summary>ZKO1 차분 적용: gzip 안에 헤더(ZKO1, 결과 크기) + C(원본 복사)/L(데이터)/E(끝)</summary>
        private static void ApplyDelta(string source, string patch, string output, long expected)
        {
            using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (var raw = new FileStream(patch, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (var delta = new BufferedStream(new GZipStream(raw, CompressionMode.Decompress), 1 << 20))
            using (var outp = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var b = new byte[16];
                Exact(delta, b, 4);
                if (Encoding.ASCII.GetString(b, 0, 4) != "ZKO1") throw new PatchError("패치 헤더 불일치");
                Exact(delta, b, 8);
                if (BitConverter.ToInt64(b, 0) != expected) throw new PatchError("패치 헤더 불일치");
                var buf = new byte[1 << 20];
                while (true)
                {
                    Exact(delta, b, 1);
                    var op = (char)b[0];
                    if (op == 'E') break;
                    long count; Stream inp;
                    if (op == 'C')
                    {
                        Exact(delta, b, 16);
                        long offset = BitConverter.ToInt64(b, 0); count = BitConverter.ToInt64(b, 8);
                        if (offset < 0 || count < 0 || offset > src.Length || count > src.Length - offset) throw new PatchError("잘못된 복사 범위");
                        src.Seek(offset, SeekOrigin.Begin); inp = src;
                    }
                    else if (op == 'L') { Exact(delta, b, 8); count = BitConverter.ToInt64(b, 0); inp = delta; }
                    else throw new PatchError("잘못된 패치 명령");
                    if (count < 0 || count > expected - outp.Position) throw new PatchError("잘못된 출력 크기");
                    while (count > 0)
                    {
                        int n = (int)Math.Min(count, buf.Length);
                        Exact(inp, buf, n); outp.Write(buf, 0, n); count -= n;
                    }
                }
                if (outp.Position != expected || delta.ReadByte() != -1) throw new PatchError("패치 크기 불일치");
                outp.Flush(true);
            }
        }

        /// <summary>shutil.copyfile: 내용만 새 파일에 복사 (읽기 전용 등 속성은 옮기지 않음)</summary>
        private static void CopyContent(string src, string dst)
        {
            using (var s = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (var d = new FileStream(dst, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
                s.CopyTo(d, 1 << 20);
        }

        private static void CopyFileVerified(string src, string dst, string expected)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            if (File.Exists(dst))
            {
                if (Digest(dst) != expected) throw new PatchError("기존 파일이 예상 상태와 다릅니다: " + dst);
                return;
            }
            var temp = dst + "." + Guid.NewGuid().ToString("N") + ".copy";
            CopyContent(src, temp);
            if (Digest(temp) != expected) { try { File.Delete(temp); } catch { } throw new PatchError("복사 검증 실패: " + src); }
            Native.Replace(temp, dst);
        }

        /// <summary>replace_from: 같은 폴더의 .zecole-&lt;작업ID&gt;.tmp 에 준비·검증 후 한 번에 교체</summary>
        private static void ReplaceFrom(string source, string target, string expected, string token)
        {
            var temp = Path.Combine(Path.GetDirectoryName(target), ".zecole-" + token + ".tmp");
            if (File.Exists(temp)) { if (Digest(temp) != expected) throw new PatchError("중단된 임시 파일의 상태를 확인해야 합니다: " + temp); }
            else CopyContent(source, temp);
            if (Digest(temp) != expected) throw new PatchError("교체 준비 파일 검증 실패");
            using (var f = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite)) f.Flush(true);
            Native.Replace(temp, target);
            if (Digest(target) != expected) throw new PatchError("설치 결과 검증 실패");
        }

        private string Location(string root, Dictionary<string, object> item) =>
            Safe(S(item, "scope") == "game" ? root : Path.Combine(root, "CONVRGENCE_Data"), S(item, "name"));

        private static void ApplyChange(string target, string source, string expected, string token)
        {
            if (expected == null) { if (File.Exists(target)) File.Delete(target); }
            else { Directory.CreateDirectory(Path.GetDirectoryName(target)); ReplaceFrom(source, target, expected, token); }
        }

        // ───────────── 중단 복구 (integrated.recover) ─────────────
        private bool Recover(string root, Dictionary<string, object> m, string store, Action<string, double?> say)
        {
            var jpath = Path.Combine(store, "transaction.json");
            if (!File.Exists(jpath)) return false;
            var j = Read(jpath);
            if (S(j, "state") == "completed" || S(j, "state") == "rolled_back") return false;
            // schema 1 기록은 예전 단독 엔진(데이터만)의 것. 이 빌드 기록에는 생기지 않으므로 손대지 않고 멈춤
            if (!(j.TryGetValue("schema", out var sc) && Convert.ToInt32(sc) == 2))
                throw new PatchError("이전 형식의 중단된 작업 기록이 있습니다. 파일을 바꾸지 않았습니다. 기록 폴더: " + store);
            ValidateGame(root, m);
            if (S(j, "root") != Native.Resolve(root) || !Regex.IsMatch(S(j, "id") ?? "", "^[0-9a-f]{32}$")) throw new PatchError("중단된 통합 설치 기록을 확인해야 합니다.");
            var runtime = Arr(m, "runtime_files").ToDictionary(f => S(f, "name")); var assets = Arr(m, "files").ToDictionary(f => S(f, "name"));
            var ledger = Read(Path.Combine(store, "runtime-ownership.json")); var lf = D(ledger["files"]);
            var verified = new List<(Dictionary<string, object> item, string target, string rollback)>(); var seen = new HashSet<string>();
            if (!(j.TryGetValue("items", out var itemsObj) && itemsObj is object[] itemArr && itemArr.All(x => x is Dictionary<string, object>)))
                throw new PatchError("중단된 작업 기록이 손상되었습니다. 파일을 바꾸지 않았습니다.");
            foreach (var item in itemArr.Select(D))
            {
                // 필수 키가 모두 있고, before/after 는 null 또는 해시여야 함 (누락을 '파일 없음'으로 읽어 지우지 않게)
                if (new[] { "key", "name", "scope", "before", "after" }.Any(k => !item.ContainsKey(k)) || !(item["key"] is string) || !(item["name"] is string) || !(item["scope"] is string)
                    || !HashOrNull(item["before"]) || !HashOrNull(item["after"]))
                    throw new PatchError("중단된 작업 기록이 손상되었습니다. 파일을 바꾸지 않았습니다.");
                if (!seen.Add(S(item, "key"))) throw new PatchError("중복 작업 기록");
                HashSet<string> allowed; string key; var name = S(item, "name");
                if (S(item, "scope") == "game" && runtime.ContainsKey(name))
                {
                    var rec = lf.TryGetValue(name, out var ro) ? D(ro) : null;
                    if (rec == null || !rec.ContainsKey("baseline") || !HashOrNull(rec["baseline"])) throw new PatchError("자막 설치 기록이 손상되었습니다: " + name);
                    allowed = new HashSet<string>(Strs(runtime[name], "accepted")) { null, S(rec, "baseline"), S(rec, "installed_hash") };
                    key = "runtime/" + name;
                }
                else if (S(item, "scope") == "data" && assets.ContainsKey(name))
                {
                    var f = assets[name];
                    allowed = new HashSet<string>(Strs(f, "previous_patched_sha256")) { S(f, "original_sha256"), S(f, "patched_sha256") };
                    key = "data/" + name;
                }
                else throw new PatchError("알 수 없는 작업 대상");
                string before = S(item, "before"), after = S(item, "after");
                if (S(item, "key") != key || !allowed.Contains(before) || !allowed.Contains(after)) throw new PatchError("알 수 없는 작업 상태");
                var target = Location(root, item); var rollback = Safe(Path.Combine(store, "transactions", S(j, "id")), "before/" + key);
                var now = HashOrNone(target);
                if ((now != before && now != after) || (before != null && HashOrNone(rollback) != before)) throw new PatchError("중단 이후 파일이 바뀌었습니다. 파일과 백업을 보존했습니다.");
                verified.Add((item, target, rollback));
            }
            say("중단된 통합 설치를 검증된 이전 상태로 복구합니다.", 0);
            for (int i = verified.Count - 1; i >= 0; i--)
            {
                var (item, target, rollback) = verified[i];
                string before = S(item, "before"), after = S(item, "after");
                ValidateGame(root, m);
                var now = HashOrNone(target);
                if (now != before && now != after) throw new PatchError("복구 중 파일 변경 감지");
                if (now != before) ApplyChange(target, rollback, before, S(j, "id"));
                var temp = Path.Combine(Path.GetDirectoryName(target), ".zecole-" + S(j, "id") + ".tmp");
                if (File.Exists(temp))
                {
                    var th = Digest(temp);
                    if (th != before && th != after) throw new PatchError("알 수 없는 임시 파일을 보존했습니다.");
                    File.Delete(temp);
                }
            }
            var rp = Path.Combine(store, "installation.json");
            if (j.TryGetValue("previous_receipt", out var prev) && prev != null) Save(rp, prev);
            else if (File.Exists(rp)) File.Delete(rp);
            j["state"] = "rolled_back"; j["recovered_at"] = Now(); Save(jpath, j);
            return true;
        }

        // ───────────── 설치·복구 (integrated.manage) ─────────────
        protected override string DoApply(string action, Action<string, double?> say)
        {
            if (action != "install" && action != "restore") throw new PatchError("지원하지 않는 동작");
            UseCache(false);   // 실제 작업에서는 해시를 모두 새로 계산
            var m = LoadRelease(Pkg); var root = ValidateGame(Root, m); var store = InstallationStore(Storage, root, m);
            Directory.CreateDirectory(Storage);
            using (var lockFile = new FileStream(Path.Combine(Storage, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                if (lockFile.Length == 0) { lockFile.WriteByte((byte)'0'); lockFile.Flush(); }
                try { lockFile.Lock(0, 1); }
                catch (IOException) { throw new PatchError("다른 설치/복구 작업이 진행 중입니다."); }
                try { return Manage(m, root, store, action, say); }
                finally { try { lockFile.Unlock(0, 1); } catch { } }
            }
        }

        private string Manage(Dictionary<string, object> m, string root, string store, string action, Action<string, double?> say)
        {
            Recover(root, m, store, say);
            var (_, assets) = BaseInspect(root, m, store);
            var (runtime, ledger) = RuntimePlan(root, m, store, action);
            var files = Arr(m, "files").ToList();
            long total = files.Sum(f => L(f, "original_bytes") + Math.Max(L(f, "original_bytes"), L(f, "patched_bytes")) * 2)
                       + Arr(m, "runtime_files").Sum(f => L(f, "bytes") * 4) + 64L * 1024 * 1024;
            long targetNeed = files.Max(f => Math.Max(L(f, "original_bytes"), L(f, "patched_bytes"))) + 16L * 1024 * 1024;
            bool same = string.Equals(Path.GetPathRoot(Native.Resolve(Storage)), Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase);
            if (Native.FreeBytes(Storage) < total + (same ? targetNeed : 0) || Native.FreeBytes(root) < targetNeed)
                throw new PatchError($"여유 공간이 부족합니다. 약 {(total + targetNeed) / 1e9:0.00} GB의 작업 공간이 필요합니다.");
            // 게임 파일을 바꾸기 전에 필요한 자막 도구를 모두 확보·검증
            var sources = new Dictionary<string, string>();
            if (action == "install") foreach (var e in runtime.Where(e => e.Current != e.After)) sources[e.Name] = Obtain(e.Spec, e.Target, say);
            var logDir = Path.Combine(Storage, "logs"); Directory.CreateDirectory(logDir);
            var log = Path.Combine(logDir, Guid.NewGuid().ToString("N") + ".log");
            void Report(string text, double? percent = null)
            {
                try { File.AppendAllText(log, Now() + " " + text + "\n", new UTF8Encoding(false)); } catch { }
                say(text, percent);
            }
            Report($"통합 {action} 시작 · 작업 공간 {(total + targetNeed) / 1e9:0.00} GB · ZECOLE 런처 {Program.Version}", 0);

            var entries = new List<Entry>();
            foreach (var a in assets)
            {
                CopyFileVerified(a.Source, a.Backup, S(a.F, "original_sha256"));
                entries.Add(new Entry { Key = "data/" + S(a.F, "name"), Name = S(a.F, "name"), Scope = "data", Target = a.Target, Current = a.Current, After = S(a.F, action == "install" ? "patched_sha256" : "original_sha256"), Backup = a.Backup, Spec = a.F });
            }
            foreach (var e in runtime)
            {
                if (e.Baseline != null) CopyFileVerified(e.Current == e.Baseline ? e.Target : e.BaselineSource, e.Backup, e.Baseline);
                if (S(e.Spec, "policy") == "owned" && e.Current != null)
                    CopyFileVerified(e.Target, Safe(Path.Combine(store, "legacy-subtitle-archive"), e.Name + "/" + e.Current), e.Current);
            }
            Save(Path.Combine(store, "runtime-ownership.json"), ledger);
            entries.AddRange(runtime);

            var token = Guid.NewGuid().ToString("N"); var tx = Path.Combine(store, "transactions", token);
            var items = new List<Dictionary<string, object>>();
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.Current == e.After) continue;
                var before = Safe(Path.Combine(tx, "before"), e.Key); var after = Safe(Path.Combine(tx, "after"), e.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(after));
                if (e.Current != null) CopyFileVerified(e.Target, before, e.Current);
                if (e.After != null)
                {
                    if (e.Scope == "data" && action == "install") ApplyDelta(e.Backup, Safe(Path.Combine(Pkg, "payload"), S(e.Spec, "patch")), after, L(e.Spec, "patched_bytes"));
                    else CopyFileVerified(e.Scope == "game" && action == "install" ? sources[e.Name] : e.Backup, after, e.After);
                    if (Digest(after) != e.After) throw new PatchError("설치 준비 검증 실패: " + e.Name);
                }
                items.Add(new Dictionary<string, object> { ["key"] = e.Key, ["name"] = e.Name, ["scope"] = e.Scope, ["before"] = e.Current, ["after"] = e.After });
                Report("설치 준비: " + e.Name, (int)(10 + (i + 1) * 45.0 / entries.Count));
            }
            string receiptPath = Path.Combine(store, "installation.json"), jpath = Path.Combine(store, "transaction.json");
            var j = new Dictionary<string, object>
            {
                ["schema"] = 2, ["id"] = token, ["root"] = root, ["state"] = "prepared", ["action"] = action, ["items"] = items.ToArray(),
                ["previous_receipt"] = File.Exists(receiptPath) ? Read(receiptPath) : null,
            };
            Save(jpath, j);
            try
            {
                ValidateGame(root, m);
                foreach (var e in entries) if (HashOrNone(e.Target) != e.Current) throw new PatchError("설치 준비 중 파일이 변경되었습니다: " + e.Name);
                j["state"] = "committing"; Save(jpath, j);
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    ValidateGame(root, m); var target = Location(root, item);
                    if (HashOrNone(target) != S(item, "before")) throw new PatchError("교체 직전 파일 변경: " + S(item, "name"));
                    ApplyChange(target, Safe(Path.Combine(tx, "after"), S(item, "key")), S(item, "after"), token);
                    Report("파일 적용: " + S(item, "name"), (int)(55 + (i + 1) * 40.0 / items.Count));
                    if (FailAfter == i + 1) throw new PatchError("TEST: simulated commit failure");
                    if (InterruptAfter == i + 1) Native.ExitProcess(86);
                }
                foreach (var e in entries) if (HashOrNone(e.Target) != e.After) throw new PatchError("최종 검증 실패: " + e.Name);
                var receipt = new Dictionary<string, object>
                {
                    ["schema"] = 2, ["game"] = "CONVRGENCE", ["game_root"] = root, ["platform"] = S(m, "platform"), ["host"] = S(m, "host"),
                    ["game_version"] = S(m, "game_version"), ["steam_build"] = S(m, "steam_build"),
                    ["patch_version"] = action == "install" ? S(m, "release") : null, ["installer_version"] = S(m, "installer_version"),
                    ["subtitle_version"] = action == "install" ? S(m, "subtitle_version") : null,
                    ["state"] = action == "install" ? "installed" : "restored", ["verified_at"] = Now(), ["backup"] = Path.Combine(store, "originals"),
                    ["files"] = entries.Where(e => e.Scope == "data").ToDictionary(e => e.Name, e => (object)e.After),
                    ["runtime_files"] = runtime.ToDictionary(e => e.Name, e => (object)e.After),
                    ["shared_loader_retained"] = runtime.Any(e => e.KeepShared),
                };
                Save(receiptPath, receipt); j["state"] = "completed"; Save(jpath, j);
                var lf = D(ledger["files"]);
                foreach (var e in runtime) D(lf[e.Name])["installed_hash"] = S(e.Spec, "sha256");
                Save(Path.Combine(store, "runtime-ownership.json"), ledger);
                var done = action == "install" ? "통합 설치 완료 · 게임 설정에서 한국어를 선택하세요." : "한글패치 및 추가 자막 제거 완료 · 원본 복구 완료";
                Report(done, 100);
                if (Directory.Exists(tx))
                {
                    foreach (var item in items)
                        foreach (var (folder, k) in new[] { ("before", "before"), ("after", "after") })
                        {
                            var p = Safe(Path.Combine(tx, folder), S(item, "key"));
                            if (S(item, k) != null && Digest(p) != S(item, k)) throw new PatchError("작업 사본이 변경되어 보존했습니다.");
                        }
                    if (!Native.Resolve(tx).StartsWith(Native.Resolve(Path.Combine(store, "transactions")) + "\\", StringComparison.OrdinalIgnoreCase)) throw new PatchError("잘못된 작업 경로");
                    Directory.Delete(Native.Long(tx), true);
                }
                return done;
            }
            catch
            {
                if (S(Read(jpath), "state") != "completed") { Report("통합 작업 실패 · 이전 상태로 복구합니다."); Recover(root, m, store, (t, p) => Report(t, p)); }
                throw;
            }
        }

        // ───────────── Windows API ─────────────
        private static class Native
        {
            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, StringBuilder buf, uint len, uint flags);
            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern bool MoveFileExW(string from, string to, uint flags);
            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern bool GetDiskFreeSpaceExW(string dir, out ulong free, out ulong total, out ulong totalFree);
            [DllImport("kernel32.dll")]
            public static extern void ExitProcess(uint code);

            /// <summary>Python Path.resolve() 와 같은 실제 경로 (기록 폴더 이름·기록 안 경로가 기존 설치기와 일치해야 함)</summary>
            /// <summary>파일 작업용 긴 경로 형식 (\\?\C:\… / \\?\UNC\서버\…)</summary>
            public static string Long(string p)
            {
                if (p.StartsWith(@"\\?\")) return p;
                if (p.StartsWith(@"\\")) return @"\\?\UNC\" + p.Substring(2);
                return @"\\?\" + p;
            }

            /// <summary>\\?\ 접두사를 뗀 일반 경로 (기록·비교용)</summary>
            public static string Plain(string p)
            {
                if (p.StartsWith(@"\\?\UNC\")) return @"\\" + p.Substring(8);
                if (p.StartsWith(@"\\?\")) return p.Substring(4);
                return p;
            }

            public static string Resolve(string path)
            {
                var full = Path.GetFullPath(Plain(path)).TrimEnd('\\');
                if (full.EndsWith(":")) full += "\\";
                var rest = new Stack<string>(); var cur = full;
                while (cur != null && !File.Exists(Long(cur)) && !Directory.Exists(Long(cur))) { rest.Push(Path.GetFileName(cur)); cur = Path.GetDirectoryName(cur); }
                if (cur == null) return full;
                var fin = Final(cur) ?? cur;
                while (rest.Count > 0) fin = Path.Combine(fin, rest.Pop());
                return fin;
            }

            private static string Final(string path)
            {
                using (var h = CreateFileW(Long(path), 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
                {
                    if (h.IsInvalid) return null;
                    var sb = new StringBuilder(1024);
                    var n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
                    if (n == 0 || n >= sb.Capacity) return null;
                    var s = sb.ToString();
                    if (s.StartsWith(@"\\?\UNC\")) s = @"\\" + s.Substring(8);
                    else if (s.StartsWith(@"\\?\")) s = s.Substring(4);
                    return s;
                }
            }

            /// <summary>os.replace: 같은 볼륨 안에서 대상이 있으면 덮어쓰며 이름 바꾸기</summary>
            public static void Replace(string from, string to)
            {
                if (!MoveFileExW(from, to, 1 | 8)) throw new IOException("파일 교체 실패(" + Marshal.GetLastWin32Error() + "): " + to);
            }

            public static long FreeBytes(string dir)
            {
                if (!GetDiskFreeSpaceExW(Resolve(dir), out var free, out _, out _)) throw new PatchError("여유 공간을 확인하지 못했습니다: " + dir);
                return (long)Math.Min(free, long.MaxValue);
            }
        }
    }
}
