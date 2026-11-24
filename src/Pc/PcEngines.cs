using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Zecole.Launcher.Quest;

namespace Zecole.Launcher.Pc
{
    /// <summary>
    /// PC VR 게임 한글패치 엔진 (런처 내장).
    /// 각 게임의 기존 설치 스크립트(Patch.ps1 · engine.py)를 같은 순서로 옮김:
    /// 해시 확인 → 원본 백업(게임 폴더 안, 기존 설치기와 같은 위치) → 임시 결과 생성·검증 → 교체 → 재검증 → 실패 시 되돌리기.
    /// 기존 설치기로 설치한 게임도 그대로 이어서 관리할 수 있음.
    /// </summary>
    internal abstract class PcPatch
    {
        public string Root;   // 게임 설치 폴더
        public string Pkg;    // 검증된 패치 데이터 폴더
        protected Func<string, string> H = Hash.File2;   // 읽기 전용 상태 확인에서는 해시 캐시를 씀

        public abstract string[] Processes { get; }
        /// <summary>쓰기 권한을 확인할 폴더 (Program Files 아래면 관리자 권한 필요)</summary>
        public abstract string WriteDir { get; }
        /// <summary>설치 폴더가 이 게임인지 확인할 표식 (관리자 작업에서 대상 검증용)</summary>
        public abstract bool LooksLikeGame(string root);
        public abstract string PackageVersion { get; }
        /// <summary>버전이 같아도 설치를 누르면 다시 적용 (VR 모드 업데이트로 파일이 바뀌는 게임)</summary>
        public virtual bool AlwaysReapply => false;
        /// <summary>읽기 전용 상태: state(installed/original/mixed/other), version, canRestore, message</summary>
        public abstract Dictionary<string, object> Inspect();
        protected abstract string DoApply(string action, Action<string, double?> say);
        /// <summary>설치 전에 일반 권한으로 미리 받아 둘 것 (관리자 권한 작업에서는 내려받지 않도록)</summary>
        public virtual void Prefetch(Action<string, double?> say) { }
        /// <summary>
        /// true 면 화면 상태가 이미 설치됨/원본이어도 엔진을 항상 실행 (작업 기록 복구·영수증 갱신을 엔진이 잠금 안에서 처리하는 게임)
        /// </summary>
        public virtual bool AlwaysRunEngine => false;
        /// <summary>쓰기 권한을 확인할 폴더들 (하나라도 쓸 수 없으면 관리자 권한으로 실행)</summary>
        public virtual IEnumerable<string> WriteDirs => new[] { WriteDir };

        /// <summary>
        /// 설치·복구. 같은 게임 폴더에 대한 작업은 프로세스(일반·관리자 권한)를 가리지 않고 하나만 실행.
        /// </summary>
        public string Apply(string action, Action<string, double?> say)
        {
            var id = Hash.Bytes(Encoding.UTF8.GetBytes(Path.GetFullPath(Root).TrimEnd('\\').ToLowerInvariant())).Substring(0, 32);
            using (var m = new System.Threading.Mutex(false, @"Local\ZECOLE.PcPatch." + id))
            {
                bool owned;
                try { owned = m.WaitOne(0); } catch (System.Threading.AbandonedMutexException) { owned = true; }
                if (!owned) throw new PatchError("같은 게임에 다른 설치 또는 복구 작업이 진행 중입니다.");
                try { return DoApply(action, say); }
                finally { m.ReleaseMutex(); }
            }
        }

        /// <summary>
        /// 원자적 교체: 같은 폴더에 새 파일을 만들어 해시를 확인한 뒤 한 번에 바꿔치기.
        /// 중간에 꺼져도 대상 파일은 이전 내용이나 새 내용 중 하나로만 남음 (잘린 파일이 생기지 않음).
        /// </summary>
        protected static void ReplaceVerified(string source, string target, string expected)
        {
            var tmp = target + ".zecole-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".new";
            try
            {
                File.Copy(source, tmp);
                using (var f = new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite)) f.Flush(true);
                if (Hash.File2(tmp) != expected) throw new PatchError("교체 준비 파일 검증 실패: " + Path.GetFileName(target));
                if (File.Exists(target)) File.Replace(tmp, target, null, true); else File.Move(tmp, target);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
            if (Hash.File2(target) != expected) throw new PatchError("교체 후 검증 실패: " + Path.GetFileName(target));
        }

        public void UseCache(bool on) => H = on ? (Func<string, string>)HashCache.Get : Hash.File2;
        public bool Running() => Processes.Any(p => Process.GetProcessesByName(p).Length > 0);
        protected void NotRunning() { if (Running()) throw new PatchError("게임을 완전히 종료한 뒤 다시 시도해 주세요."); }

        public static PcPatch Create(string engine, string root, string pkg)
        {
            PcPatch p;
            switch (engine)
            {
                case "pc-aft-steam": p = new AftSteam(); break;
                case "pc-gunman": p = new XorGzPatch(); break;
                case "pc-bg2": p = new OffsetDeltaPatch(); break;
                case "pc-tf2vr": p = new Tf2Vr(); break;
                case "pc-convrgence": p = new ConvrgenceSteam(); break;
                default: throw new PatchError("지원하지 않는 설치 엔진입니다: " + engine);
            }
            p.Root = root; p.Pkg = pkg;
            return p;
        }

        // ── 공용 도우미 ──
        protected static Dictionary<string, object> Json(string path) =>
            Bridge.Json.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>
            ?? throw new PatchError("패치 정보 파일을 읽지 못했습니다.");
        protected static string Str(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? v as string : null;
        protected static string Lower(string s) => s?.ToLowerInvariant();

        protected static Dictionary<string, object> State(string state, string version, bool canRestore, string message) => new Dictionary<string, object>
        {
            ["state"] = state, ["version"] = version, ["canRestore"] = canRestore, ["message"] = message,
        };

        /// <summary>폴더 안의 경로인지, 연결(reparse) 폴더를 거치지 않는지 확인</summary>
        protected static string Inside(string baseDir, string relative)
        {
            if (string.IsNullOrEmpty(relative) || relative.Contains(":") || relative.Contains("..") || Path.IsPathRooted(relative)) throw new PatchError("잘못된 패치 경로입니다.");
            var b = Path.GetFullPath(baseDir).TrimEnd('\\') + "\\";
            var p = Path.GetFullPath(Path.Combine(baseDir, relative.Replace('/', '\\')));
            if (!p.StartsWith(b, StringComparison.OrdinalIgnoreCase)) throw new PatchError("패치 경로가 대상 폴더를 벗어났습니다.");
            for (var x = new FileInfo(p).Directory; x != null && x.FullName.Length >= b.Length - 1; x = x.Parent)
                if (x.Exists && (x.Attributes & FileAttributes.ReparsePoint) != 0) throw new PatchError("연결된 폴더에는 설치하지 않습니다: " + x.FullName);
            return p;
        }

        protected static void CopyVerified(string src, string dst, string expected)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            var tmp = dst + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".copy";
            File.Copy(src, tmp);
            if (Hash.File2(tmp) != expected) { File.Delete(tmp); throw new PatchError("복사 검증 실패: " + Path.GetFileName(src)); }
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(tmp, dst);
        }
    }

    // ═════════════════════════ A Fisherman's Tale (Steam) ═════════════════════════
    /// <summary>한글 자막 PAK 하나를 넣고 빼는 방식 (engine.py Steam 클래스)</summary>
    internal sealed class AftSteam : PcPatch
    {
        private string Paks => Path.Combine(Root, @"Fisherman\Content\Paks");
        private string Original => Path.Combine(Paks, "Fisherman-WindowsNoEditor.pak");
        private string Target => Path.Combine(Paks, "Fisherman-KoreanSubtitles_P.pak");
        private string Payload => Path.Combine(Pkg, @"patch\Fisherman-KoreanSubtitles_P.pak");
        private Dictionary<string, object> Config => Json(Path.Combine(Pkg, "launcher_config.json"));
        private Dictionary<string, object> Manifest => Json(Path.Combine(Pkg, @"patch\manifest.json"));
        private string Expected => Lower(Str(Manifest, "sha256"));
        private HashSet<string> Known => new HashSet<string>(((object[])Manifest["knownHashes"]).Select(x => Lower((string)x))) { Expected };

        public override string[] Processes => new[] { "Fisherman", "Fisherman-Win64-Shipping" };
        public override string WriteDir => Paks;
        public override bool LooksLikeGame(string root) => File.Exists(Path.Combine(root, @"Fisherman\Content\Paks\Fisherman-WindowsNoEditor.pak"));
        public override string PackageVersion => Str(Config, "patch_version");

        private string Current()
        {
            if (!File.Exists(Original)) throw new PatchError("게임 폴더가 올바르지 않습니다.");
            if (H(Original) != Str(Config, "original_sha256")) throw new PatchError($"지원하지 않는 원본 게임 파일입니다. Steam 빌드 {Str(Config, "steam_build")}만 지원합니다.");
            var acf = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Root)), "appmanifest_559330.acf");
            if (File.Exists(acf) && !Regex.IsMatch(File.ReadAllText(acf), "\"buildid\"\\s+\"" + Str(Config, "steam_build") + "\""))
                throw new PatchError("Steam 게임 빌드가 지원 범위와 다릅니다.");
            if (File.Exists(Target) && (new FileInfo(Target).Attributes & FileAttributes.ReparsePoint) != 0) throw new PatchError("연결된 패치 파일은 처리할 수 없습니다.");
            var cur = File.Exists(Target) ? H(Target) : null;
            if (cur != null && !Known.Contains(cur)) throw new PatchError("같은 이름의 알 수 없는 패치가 있습니다. 파일을 변경하지 않습니다.");
            return cur;
        }

        public override Dictionary<string, object> Inspect()
        {
            try
            {
                var cur = Current();
                if (cur == Expected) return State("installed", PackageVersion, true, null);
                if (cur == null) return State("original", null, false, null);
                return State("mixed", "이전 버전", true, "이전 버전 한글패치가 있습니다. 설치하면 최신으로 바뀝니다.");
            }
            catch (PatchError ex) { return State("other", null, false, ex.Message); }
        }

        protected override string DoApply(string action, Action<string, double?> say)
        {
            say("Steam 게임 버전과 패치 파일 검증 중…", 5);
            var before = Current();
            NotRunning();
            if (Hash.File2(Payload) != Expected) throw new PatchError("내장 패치 검증 실패");
            if (action == "install" && before == Expected) return "이미 같은 한글패치가 설치되어 있습니다.";
            if (action == "restore" && before == null) return "이미 원본 상태입니다.";
            // 기존 패치 파일 사본 (실패 시 되돌리기용, 원본 게임 파일이 아니라 패치 PAK 하나)
            var keep = before != null ? Path.Combine(Paks, ".zecole-before-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".pak") : null;
            bool rollbackFailed = false;
            try
            {
                say("교체 직전 게임 상태 재확인 중…", 40);
                NotRunning();
                if (Current() != before) throw new PatchError("준비 중에 게임 파일이 변경되었습니다.");
                if (keep != null) CopyVerified(Target, keep, before);
                try
                {
                    say(action == "install" ? "한글 자막 파일 교체 중…" : "한글 자막 파일 제거 중…", 70);
                    if (action == "install") ReplaceVerified(Payload, Target, Expected);   // 같은 폴더에서 한 번에 바꿔치기
                    else File.Delete(Target);
                    if (Current() != (action == "install" ? Expected : null)) throw new PatchError("설치 결과 검증 실패");
                }
                catch
                {
                    try { if (keep != null) ReplaceVerified(keep, Target, before); else if (File.Exists(Target)) File.Delete(Target); }
                    catch { rollbackFailed = true; }
                    throw;
                }
            }
            finally { if (keep != null && !rollbackFailed && File.Exists(keep)) File.Delete(keep); }
            return action == "install" ? "설치 완료 · 화면·음성은 English, 자막을 켜면 한글로 나옵니다." : "제거 및 원본 복구 완료 · 저장 데이터는 그대로입니다.";
        }
    }

    // ═════════════════════════ 파일별 차분 공통 ═════════════════════════
    internal abstract class FilePatch : PcPatch
    {
        protected sealed class Entry { public string Name, Target, Original, Patched, Payload, PayloadHash; public long Length; public HashSet<string> Known; }
        protected abstract string DataDir { get; }
        protected abstract string BackupDir { get; }     // 원본 백업 폴더 (게임 폴더 안, 기존 설치기와 같은 위치)
        protected abstract List<Entry> Entries();
        protected abstract void BuildPatched(Entry e, string source, string output);
        protected virtual string LegacyBase(Entry e) => null;
        protected abstract string Done(string action);

        public override string WriteDir => DataDir;
        protected string Saved(Entry e) => Inside(BackupDir, e.Name);

        public override Dictionary<string, object> Inspect()
        {
            try
            {
                var es = Entries();
                int patched = 0, original = 0; bool canRestore = true;
                foreach (var e in es)
                {
                    if (!File.Exists(e.Target)) return State("other", null, false, "게임 파일이 없습니다: " + e.Name);
                    var h = H(e.Target);
                    if (!e.Known.Contains(h)) return State("other", null, false, $"지원하지 않는 게임 파일입니다: {e.Name}. 게임이 업데이트되었거나 다른 모드가 설치돼 있습니다.");
                    if (h == e.Patched) patched++;
                    if (h == e.Original) original++;
                    if (h != e.Original && !File.Exists(Saved(e))) canRestore = false;
                }
                if (patched == es.Count) return State("installed", PackageVersion, canRestore, canRestore ? null : "원본 백업이 없어 이 런처로는 복구할 수 없습니다. Steam 게임 파일 무결성 검사로 원본을 되돌릴 수 있습니다.");
                if (original == es.Count) return State("original", null, false, null);
                return State("mixed", "이전 버전", canRestore, "일부 파일만 바뀐 상태이거나 이전 버전 한글패치입니다. 설치하면 최신으로 맞춥니다.");
            }
            catch (PatchError ex) { return State("other", null, false, ex.Message); }
        }

        protected override string DoApply(string action, Action<string, double?> say)
        {
            NotRunning();
            var es = Entries();
            var plans = new List<(Entry e, string cur, string baseFile)>();
            for (int i = 0; i < es.Count; i++)
            {
                var e = es[i];
                say("게임 파일 확인: " + e.Name, 2 + i * 18.0 / es.Count);
                var cur = Hash.File2(e.Target);
                if (!e.Known.Contains(cur)) throw new PatchError($"지원하지 않는 게임 파일입니다: {e.Name}. 게임이 업데이트됐다면 새 한글패치가 필요합니다.");
                var want = action == "install" ? e.Patched : e.Original;
                if (cur == want) continue;
                var saved = Saved(e);
                string baseFile = File.Exists(saved) ? saved : cur == e.Original ? e.Target : LegacyBase(e);
                if (baseFile == null || !File.Exists(baseFile) || Hash.File2(baseFile) != e.Original)
                    throw new PatchError(action == "restore" ? $"복원할 원본 백업이 없습니다: {e.Name}. Steam 게임 파일 무결성 검사로 원본을 되돌려 주세요." : $"원본 백업 확인 실패: {e.Name}");
                if (action == "install" && Hash.File2(e.Payload) != e.PayloadHash) throw new PatchError("패치 데이터가 손상되었습니다: " + e.Name);
                plans.Add((e, cur, baseFile));
            }
            if (plans.Count == 0) return action == "install" ? "이미 최신 한글패치가 설치되어 있습니다. 파일 검증을 마쳤습니다." : "이미 원본 상태입니다.";

            // 작업 폴더: 게임 데이터 폴더 바로 아래 짧은 이름 (원본 백업 폴더 안에 두면 경로가 260자를 넘을 수 있음)
            var stage = Path.Combine(DataDir, "KoreanPatchStage-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            var rollback = Path.Combine(stage, "b"); var ready = Path.Combine(stage, "r");
            Directory.CreateDirectory(rollback); Directory.CreateDirectory(ready);
            var applied = new List<(Entry e, string cur, string baseFile)>();
            bool rollbackFailed = false;
            try
            {
                for (int i = 0; i < plans.Count; i++)
                {
                    var (e, cur, baseFile) = plans[i];
                    say("원본 백업 · 결과 파일 준비: " + e.Name, 20 + i * 55.0 / plans.Count);
                    var saved = Saved(e);
                    if (!File.Exists(saved)) CopyVerified(baseFile, saved, e.Original);
                    else if (Hash.File2(saved) != e.Original) throw new PatchError("기존 원본 백업이 다릅니다. 백업을 덮어쓰지 않았습니다: " + e.Name);
                    CopyVerified(e.Target, Path.Combine(rollback, e.Name.Replace('/', '\\')), cur);
                    var output = Path.Combine(ready, e.Name.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(output));
                    if (action == "install") BuildPatched(e, saved, output); else File.Copy(saved, output);
                    if (Hash.File2(output) != (action == "install" ? e.Patched : e.Original)) throw new PatchError("완성 파일 검증 실패: " + e.Name);
                }
                NotRunning();
                for (int i = 0; i < plans.Count; i++)
                {
                    var (e, cur, _) = plans[i];
                    say("파일 적용: " + e.Name, 75 + i * 22.0 / plans.Count);
                    if (Hash.File2(e.Target) != cur) throw new PatchError("교체 직전에 게임 파일이 변경되었습니다: " + e.Name);
                    applied.Add(plans[i]);
                    // 같은 폴더에서 한 번에 바꿔치기 → 중간에 꺼져도 잘린 파일이 남지 않음
                    ReplaceVerified(Path.Combine(ready, e.Name.Replace('/', '\\')), e.Target, action == "install" ? e.Patched : e.Original);
                }
            }
            catch (Exception ex)
            {
                foreach (var (e, cur, _) in applied)
                    try { if (Hash.File2(e.Target) != cur) ReplaceVerified(Path.Combine(rollback, e.Name.Replace('/', '\\')), e.Target, cur); }
                    catch { rollbackFailed = true; }
                if (rollbackFailed)
                    throw new PatchError(ex.Message + " · 일부 파일을 되돌리지 못했습니다. 작업 직전 파일을 보존했습니다: " + rollback);
                throw;
            }
            finally { if (!rollbackFailed) try { Directory.Delete(stage, true); } catch { } }
            return Done(action);
        }
    }

    // ═════════════════════════ Gunman Contracts (gzip-xor-v1) ═════════════════════════
    internal sealed class XorGzPatch : FilePatch
    {
        private static readonly string[] Allowed = { "level0", "level1", "level2", "level3", "level4", "level5", "resources.assets", "sharedassets0.assets", "sharedassets1.assets", "sharedassets2.assets", "sharedassets3.assets", "sharedassets4.assets", "il2cpp_data/Metadata/global-metadata.dat" };
        private Dictionary<string, object> M => Json(Path.Combine(Pkg, "manifest.json"));
        protected override string DataDir => Path.Combine(Root, "GunmanContracts_Data");
        private string Ver => Regex.IsMatch(Str(M, "version") ?? "", @"^v\.?[0-9.]+$") ? Str(M, "version") : throw new PatchError("패치 버전 정보가 올바르지 않습니다.");
        protected override string BackupDir => Path.Combine(DataDir, "KoreanPatchBackup-" + Ver, "original");
        public override string[] Processes => new[] { "GunmanContracts" };
        public override bool LooksLikeGame(string root) => Directory.Exists(Path.Combine(root, "GunmanContracts_Data"));
        public override string PackageVersion => Regex.Replace(Str(M, "version") ?? "", @"^v\.?", "");

        protected override List<Entry> Entries()
        {
            var m = M;
            if (Str(m, "format") != "gzip-xor-v1") throw new PatchError("지원하지 않는 패치 형식입니다.");
            return ((object[])m["files"]).Cast<Dictionary<string, object>>().Select(f =>
            {
                var name = Str(f, "name");
                if (!Allowed.Contains(name)) throw new PatchError("잘못된 파일 목록입니다.");
                if (!Regex.IsMatch(Str(f, "originalBackup") ?? "", @"^KoreanPatchBackup-[a-zA-Z0-9.-]+$")) throw new PatchError("잘못된 백업 경로입니다.");
                return new Entry
                {
                    Name = name, Target = Inside(DataDir, name), Original = Str(f, "original_sha256"), Patched = Str(f, "patched_sha256"),
                    Payload = Inside(Path.Combine(Pkg, "data"), name + ".xor.gz"), PayloadHash = Str(f, "payload_sha256"),
                    Length = Convert.ToInt64(f["length"]), Known = new HashSet<string>(((object[])f["known_hashes"]).Cast<string>()),
                };
            }).ToList();
        }

        // 이전 버전 설치기의 원본 백업 위치 (originalBackup/파일)
        protected override string LegacyBase(Entry e)
        {
            var f = ((object[])M["files"]).Cast<Dictionary<string, object>>().First(x => Str(x, "name") == e.Name);
            return Inside(Path.Combine(DataDir, Str(f, "originalBackup")), e.Name);
        }

        protected override void BuildPatched(Entry e, string source, string output)
        {
            using (var a = File.OpenRead(source))
            using (var g = new GZipStream(File.OpenRead(e.Payload), CompressionMode.Decompress))
            using (var w = File.Create(output))
            {
                var b = new byte[1 << 20]; var o = new byte[b.Length]; long done = 0; int n;
                while ((n = g.Read(b, 0, b.Length)) > 0)
                {
                    int k = 0, r; while (k < n && (r = a.Read(o, k, n - k)) > 0) k += r;
                    for (int i = 0; i < k; i++) b[i] ^= o[i];
                    w.Write(b, 0, n); done += n;
                    if (done > e.Length) throw new PatchError("차분 길이가 올바르지 않습니다.");
                }
                if (done != e.Length) throw new PatchError("결과 파일 길이가 올바르지 않습니다.");
            }
        }

        protected override string Done(string action) => action == "install"
            ? $"v{PackageVersion} 설치 완료 · 게임 언어에서 한국어(독일어 자리)를 선택하고 자막을 '모두'로 설정하세요."
            : "게임 원본으로 복원했습니다. 게임 언어를 English로 선택하세요. 원본 백업은 보관됩니다.";
    }

    // ═════════════════════════ BattleGroupVR2 (offset delta) ═════════════════════════
    internal sealed class OffsetDeltaPatch : FilePatch
    {
        private Dictionary<string, object> M => Json(Path.Combine(Pkg, "manifest.json"));
        protected override string DataDir => Path.Combine(Root, "BattleGroupVR2_Data");
        // v.1.1.1 부터는 manifest 의 backupFolder 를 씀 (1.1.0 과 같은 원본이라 v110 폴더를 그대로 씀). 없으면 예전처럼 버전 숫자로.
        protected override string BackupDir
        {
            get
            {
                var b = Str(M, "backupFolder");
                if (b != null && !Regex.IsMatch(b, @"^KoreanPatchBackup-v[0-9]{1,6}$")) throw new PatchError("잘못된 백업 경로입니다.");
                return Path.Combine(DataDir, b ?? "KoreanPatchBackup-v" + (Str(M, "version") ?? "").Replace(".", ""));
            }
        }
        public override string[] Processes => new[] { "BattleGroupVR2" };
        public override bool LooksLikeGame(string root) => File.Exists(Path.Combine(root, @"BattleGroupVR2_Data\Managed\Assembly-CSharp.dll"));
        public override string PackageVersion => Str(M, "version");

        protected override List<Entry> Entries()
        {
            if (!Regex.IsMatch(PackageVersion ?? "", @"^\d+\.\d+\.\d+$")) throw new PatchError("패치 버전 정보가 올바르지 않습니다.");
            return ((object[])M["files"]).Cast<Dictionary<string, object>>().Select(f =>
            {
                var path = Str(f, "path"); var name = Str(f, "name");
                if (!Regex.IsMatch(path ?? "", @"^(resources\.assets|level[0-9]|Managed/Assembly-CSharp\.dll)$") || name != Path.GetFileName(path)) throw new PatchError("패키지 파일 경로가 올바르지 않습니다.");
                if (Str(f, "delta") != name + ".delta.gz") throw new PatchError("잘못된 패치 경로입니다.");
                return new Entry
                {
                    Name = name, Target = Inside(DataDir, path), Original = Str(f, "original"), Patched = Str(f, "patched"),
                    Payload = Inside(Path.Combine(Pkg, "payload"), Str(f, "delta")), PayloadHash = Str(f, "deltaHash"),
                    // previous: 이전 한글판(예: 1.1.0)의 해시 → '이전 버전'으로 보고 원본 백업에서 새로 만듦
                    Known = new HashSet<string>(new[] { Str(f, "original"), Str(f, "patched") }
                        .Concat(((f.TryGetValue("previous", out var pv) ? pv as object[] : null) ?? new object[0]).OfType<string>()
                            .Select(x => x.ToLowerInvariant()).Where(x => Regex.IsMatch(x, "^[0-9a-f]{64}$")))),
                };
            }).ToList();
        }

        protected override void BuildPatched(Entry e, string source, string output)
        {
            using (var input = File.OpenRead(source))
            using (var gz = new GZipStream(File.OpenRead(e.Payload), CompressionMode.Decompress))
            using (var r = new BinaryReader(gz))
            using (var w = File.Create(output))
            {
                var buffer = new byte[65536];
                while (true)
                {
                    int first = gz.ReadByte(); if (first < 0) break;
                    var off = new byte[8]; off[0] = (byte)first; for (int i = 1; i < 8; i++) off[i] = r.ReadByte();
                    long offset = BitConverter.ToInt64(off, 0); int count = r.ReadInt32();
                    if (count < 0 || offset < -1 || (offset >= 0 && offset > input.Length - count)) throw new PatchError("차분 데이터가 올바르지 않습니다.");
                    Stream read = offset < 0 ? (Stream)gz : input; if (offset >= 0) input.Position = offset;
                    while (count > 0) { int n = read.Read(buffer, 0, Math.Min(count, buffer.Length)); if (n == 0) throw new PatchError("차분 데이터가 중간에 끝났습니다."); w.Write(buffer, 0, n); count -= n; }
                }
            }
        }

        protected override string Done(string action) => action == "install" ? $"한국어 패치 v{PackageVersion} 적용 및 검증이 완료되었습니다." : "원본 복원이 완료되었습니다.";
    }

    // ═════════════════════════ Titanfall 2 VR ═════════════════════════
    /// <summary>
    /// 모드 폴더 복사 + enabledmods.json 등록 + VR 시작 안내 이미지 교체.
    /// 기존 Install.ps1 의 판단 순서를 C# 으로 그대로 옮김 (내려받은 스크립트를 관리자 권한으로 실행하지 않기 위해).
    /// </summary>
    internal sealed class Tf2Vr : PcPatch
    {
        private string Profile => Path.Combine(Root, "TF2VR");
        private string ModPath => Path.Combine(Profile, @"mods\Korean.MenuTest");
        private string ModJson => Path.Combine(ModPath, "mod.json");
        private string PluginPath => Path.Combine(Profile, @"plugins\Korean.CaptionLayout.dll");
        private string Cockpit => Path.Combine(Profile, @"mods\Titanfall2VR.Cockpit\mod");
        public override string[] Processes => new[] { "Titanfall2", "Titanfall2VRLauncher" };
        public override string WriteDir => Profile;
        public override bool AlwaysReapply => true;
        public override bool LooksLikeGame(string root) => File.Exists(Path.Combine(root, "Titanfall2.exe"));
        public override string PackageVersion => Str(Json(Path.Combine(Pkg, @"Korean.MenuTest\mod.json")), "Version");
        public bool VrInstalled => File.Exists(Path.Combine(Root, "Titanfall2VRLauncher.exe")) && Directory.Exists(Profile);

        public override Dictionary<string, object> Inspect()
        {
            if (!VrInstalled) return State("other", null, false, "타이탄폴 2 VR 모드를 먼저 설치해 주세요. (CircuitLord VR 모드 설치기)");
            if (!File.Exists(ModJson)) return State("original", null, false, null);
            var m = Json(ModJson);
            if (Str(m, "Name") != "Korean.MenuTest") return State("other", null, false, "한글패치 위치에 다른 모드가 있습니다.");
            var v = Str(m, "Version");
            return State(v == PackageVersion ? "installed" : "mixed", v, true, v == PackageVersion ? null : $"v{v}가 설치돼 있습니다. 설치하면 최신으로 바뀝니다.");
        }

        private static bool Same(string a, string b) => a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private bool Matches(string path, string hash) => hash != null && File.Exists(path) && Same(Hash.File2(path), hash);
        private static Dictionary<string, object> Obj(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? v as Dictionary<string, object> : null;

        protected override string DoApply(string action, Action<string, double?> say)
        {
            bool install = action == "install";
            NotRunning();
            if (!File.Exists(Path.Combine(Root, "Titanfall2VRLauncher.exe"))) throw new PatchError("VR 모드를 먼저 설치해 주세요.");
            Json(Path.Combine(Root, @".circuitlord-vr-mods\Titanfall2VR.json"));   // VR 모드 설치 기록이 있어야 함
            if (!Directory.Exists(Profile)) throw new PatchError("VR 모드 프로필 폴더(TF2VR)가 없습니다.");
            var startupPath = Path.Combine(Cockpit, @"resource\tf2vr\startup\text.r8");
            var startupOriginal = Path.Combine(Profile, @"korean-menu-backups\startup-text-original.r8");
            var sm = Json(Path.Combine(Pkg, @"VRStartup\manifest.json"));
            string sOrig = Str(sm, "original_sha256"), sTrans = Str(sm, "translated_sha256");
            var startupHash = File.Exists(startupPath) ? Hash.File2(startupPath) : "";
            bool validBackup = Matches(startupOriginal, sOrig);
            bool replaceStartup = (install && Same(startupHash, sOrig)) || (Same(startupHash, sTrans) && validBackup);
            if (!replaceStartup) say("시작 안내 파일은 현재 VR 모드의 파일을 유지합니다.", 8);
            if (Directory.Exists(ModPath) && Str(Json(ModJson), "Name") != "Korean.MenuTest") throw new PatchError("설치 위치에 다른 모드가 있습니다.");

            string stage = null; bool northstarKnown = false; Dictionary<string, object> sourceManifest = null;
            var skipped = new List<string>();
            if (install)
            {
                say("패치 파일 검증 중…", 10);
                foreach (var kv in Json(Path.Combine(Pkg, "package-hashes.json")))
                    if (!Matches(Inside(Pkg, kv.Key), kv.Value as string)) throw new PatchError("설치 파일 검증 실패: " + kv.Key);
                sourceManifest = Json(Path.Combine(Pkg, @"Korean.MenuTest\mod.json"));
                if (Str(sourceManifest, "Name") != "Korean.MenuTest") throw new PatchError("한글패치 정보가 올바르지 않습니다.");
                stage = Path.Combine(Profile, "korean-menu-staging-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stage);
                var integ = Json(Path.Combine(Pkg, "integration-baseline.json"));
                var cockpitBase = Obj(integ, "cockpit") ?? new Dictionary<string, object>();
                northstarKnown = Matches(Path.Combine(Profile, "Northstar.dll"), Str(integ, "Northstar.dll"));
                var vrDll = Path.Combine(Profile, @"plugins\Titanfall2VR.dll");
                bool vrKnown = Matches(vrDll, Str(integ, "plugins/Titanfall2VR.dll"));
                foreach (var h in (integ.TryGetValue("compatibleVrDllHashes", out var cv) ? cv as object[] : null) ?? new object[0])
                    if (h is string hs && hs.Length > 0 && Matches(vrDll, hs)) vrKnown = true;
                var menuFiles = new[] { "scripts/vscripts/tf2vr_menu.nut", "resource/ui/menus/ingame_sp.menu" };
                bool menuKnown = northstarKnown && vrKnown;
                foreach (var rel in menuFiles)
                    if (!Matches(Path.Combine(Cockpit, rel.Replace('/', '\\')), Str(cockpitBase, rel))) menuKnown = false;
                var variantsAll = Obj(integ, "compatibleFileVariants");

                say("설치할 번역 파일을 고르는 중…", 25);
                var sourceMod = Path.Combine(Pkg, "Korean.MenuTest");
                foreach (var file in Directory.GetFiles(sourceMod, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
                {
                    var copySource = file;
                    var rel = file.Substring(sourceMod.Length + 1).Replace('\\', '/');
                    if ((rel.StartsWith("mod/scripts/") && rel != "mod/scripts/vscripts/korean_menu_test.nut") || rel.StartsWith("mod/resource/ui/"))
                    {
                        var upRel = rel.Substring(4);
                        var upstream = Path.Combine(Cockpit, upRel.Replace('/', '\\'));
                        var baseHash = Str(cockpitBase, upRel);
                        bool keep = northstarKnown;
                        if (cockpitBase.ContainsKey(upRel)) keep = keep && Matches(upstream, baseHash);
                        else if (File.Exists(upstream)) keep = false;
                        if (!keep && northstarKnown && variantsAll != null && variantsAll.TryGetValue(upRel, out var vlist) && vlist is object[] variants)
                            foreach (Dictionary<string, object> variant in variants)
                                if (Matches(upstream, Str(variant, "sha256"))) { copySource = Inside(Pkg, Str(variant, "source")); keep = true; break; }
                        if (menuFiles.Contains(upRel)) keep = keep && menuKnown;
                        if (!keep) { skipped.Add(rel); continue; }
                    }
                    var dest = Path.Combine(stage, "Korean.MenuTest", rel.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(copySource, dest);
                }
                if (northstarKnown) File.Copy(Path.Combine(Pkg, "Korean.CaptionLayout.dll"), Path.Combine(stage, "Korean.CaptionLayout.dll"));
                say("변경된 원본으로 인해 제외한 번역 파일: " + skipped.Count, 45);
            }

            var verDigits = Regex.Replace(PackageVersion ?? "", "[^0-9]", "");
            var backup = Path.Combine(Profile, @"korean-menu-backups\v" + verDigits.PadLeft(4, '0') + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backup);
            if (replaceStartup) File.Copy(startupPath, Path.Combine(backup, "startup-text-before.r8"));
            if (replaceStartup && Same(startupHash, sOrig))
            {
                if (File.Exists(startupOriginal)) File.Copy(startupOriginal, Path.Combine(backup, "previous-startup-backup.r8"));
                Directory.CreateDirectory(Path.GetDirectoryName(startupOriginal));
                File.Copy(startupPath, startupOriginal, true);
            }
            var enabledPath = Path.Combine(Profile, "enabledmods.json");
            if (File.Exists(enabledPath)) File.Copy(enabledPath, Path.Combine(backup, "enabledmods.json"));
            bool modMoved = false, pluginMoved = false, newMod = false, newPlugin = false, startupChanged = false;
            say(install ? "한글패치를 적용하는 중…" : "한글패치를 제거하는 중…", 60);
            try
            {
                if (Directory.Exists(ModPath)) { Directory.Move(ModPath, Path.Combine(backup, "Korean.MenuTest")); modMoved = true; }
                if (File.Exists(PluginPath)) { File.Move(PluginPath, Path.Combine(backup, "Korean.CaptionLayout.dll")); pluginMoved = true; }
                if (install)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(PluginPath));
                    Directory.Move(Path.Combine(stage, "Korean.MenuTest"), ModPath); newMod = true;
                    if (northstarKnown) { File.Move(Path.Combine(stage, "Korean.CaptionLayout.dll"), PluginPath); newPlugin = true; }
                    if (File.Exists(enabledPath))
                    {
                        var enabled = Json(enabledPath);
                        if (!(enabled.TryGetValue("Korean.MenuTest", out var cur) && cur is Dictionary<string, object> km)) { km = new Dictionary<string, object>(); enabled["Korean.MenuTest"] = km; }
                        km[Str(sourceManifest, "Version")] = true;
                        File.WriteAllText(enabledPath, Bridge.Json.Serialize(enabled), new UTF8Encoding(false));
                    }
                }
                if (replaceStartup)
                {
                    startupChanged = true;
                    var src = install ? Path.Combine(Pkg, @"VRStartup\text.r8") : startupOriginal;
                    File.Copy(src, startupPath, true);
                    if (!Same(Hash.File2(startupPath), install ? sTrans : sOrig)) throw new PatchError("VR 시작 안내 파일의 복사 검증에 실패했습니다.");
                }
            }
            catch
            {
                if (startupChanged) File.Copy(Path.Combine(backup, "startup-text-before.r8"), startupPath, true);
                if (newMod) Directory.Move(ModPath, Path.Combine(stage, "Korean.MenuTest"));
                if (newPlugin) File.Move(PluginPath, Path.Combine(stage, "Korean.CaptionLayout.dll"));
                if (modMoved) Directory.Move(Path.Combine(backup, "Korean.MenuTest"), ModPath);
                if (pluginMoved) File.Move(Path.Combine(backup, "Korean.CaptionLayout.dll"), PluginPath);
                if (File.Exists(Path.Combine(backup, "enabledmods.json"))) File.Copy(Path.Combine(backup, "enabledmods.json"), enabledPath, true);
                throw;
            }
            if (stage != null) try { Directory.Delete(stage, true); } catch { }
            return install
                ? "설치 완료 · 게임 설정 → 사운드에서 자막을 켜 주세요." + (skipped.Count > 0 ? $" (VR 모드 버전 차이로 번역 파일 {skipped.Count}개는 원래 파일을 유지)" : "")
                : "한글패치를 제거했습니다. 확인 가능한 시작 안내만 원본으로 복구했습니다.";
        }
    }

    // ═════════════════════════ 게임 위치 찾기 ═════════════════════════
    internal static class GameLocator
    {
        public static string Find(string engine, string appid)
        {
            if (engine == "pc-tf2vr")
            {
                var cands = new List<string>();
                foreach (var key in new[] { @"HKEY_LOCAL_MACHINE\SOFTWARE\Respawn\Titanfall2", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Respawn\Titanfall2" })
                    if (Registry.GetValue(key, "Install Dir", null) is string d) cands.Add(d);
                if (appid != null && Steam.InstalledApps().TryGetValue(appid, out var s)) cands.Add((string)((Dictionary<string, object>)s)["path"]);
                cands.Add(@"C:\Program Files\EA Games\Titanfall2");
                cands.Add(@"C:\Program Files (x86)\Origin Games\Titanfall2");
                // VR 모드가 깔린 폴더를 우선
                var hit = cands.Where(c => File.Exists(Path.Combine(c, "Titanfall2.exe"))).OrderByDescending(c => File.Exists(Path.Combine(c, "Titanfall2VRLauncher.exe"))).FirstOrDefault();
                return hit == null ? null : Norm(hit);
            }
            if (appid == null || !Steam.InstalledApps().TryGetValue(appid, out var app)) return null;
            var path = (string)((Dictionary<string, object>)app)["path"];
            return Directory.Exists(path) ? Norm(path) : null;
        }

        /// <summary>끝의 \ 제거 (명령줄 인자에서 따옴표가 깨지지 않게) · 실제 대소문자로</summary>
        private static string Norm(string p)
        {
            p = Path.GetFullPath(p).TrimEnd('\\');
            try
            {
                // 레지스트리의 소문자 경로(c:\program files (x86)\steam…)를 실제 폴더 이름으로
                var parts = p.Split('\\'); var cur = parts[0].ToUpperInvariant() + "\\";
                for (int i = 1; i < parts.Length; i++)
                {
                    var m = Directory.GetFileSystemEntries(cur, parts[i]).FirstOrDefault();
                    cur = m ?? Path.Combine(cur, parts[i]);
                }
                return cur;
            }
            catch { return p; }
        }
    }

    /// <summary>
    /// 상태 확인용 해시 캐시 (경로 + 크기 + 수정 시각이 같으면 다시 계산하지 않음).
    /// 실제 설치·복구 작업에서는 쓰지 않고 항상 새로 계산함.
    /// </summary>
    internal static class HashCache
    {
        private static readonly string PathFile = Path.Combine(Program.DataDir, "hash-cache.json");
        private static Dictionary<string, object> _map;
        private static readonly object Gate = new object();

        public static string Get(string file)
        {
            var fi = new FileInfo(file);
            var key = fi.FullName.ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            lock (Gate)
            {
                if (_map == null)
                    try { _map = File.Exists(PathFile) ? Bridge.Json.DeserializeObject(File.ReadAllText(PathFile)) as Dictionary<string, object> : null; } catch { }
                _map = _map ?? new Dictionary<string, object>();
                if (_map.TryGetValue(key, out var h) && h is string hs) return hs;
            }
            var v = Hash.File2(file);
            lock (Gate)
            {
                foreach (var old in _map.Keys.Where(k => k.StartsWith(fi.FullName.ToLowerInvariant() + "|")).ToList()) _map.Remove(old);
                _map[key] = v;
                try { File.WriteAllText(PathFile, Bridge.Json.Serialize(_map)); } catch { }
            }
            return v;
        }
    }
}
