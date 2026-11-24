using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Zecole.Launcher.Quest;

namespace Zecole.Launcher.Pc
{
    /// <summary>
    /// PC VR 한글패치 설치·복구 (화면 ↔ 엔진).
    /// 게임 폴더에 쓸 권한이 없으면(Program Files) 같은 EXE 를 관리자 권한으로 잠깐 실행해 그 작업만 처리하고,
    /// 진행 상황은 파일로 받아 런처 화면에 그대로 보여줌. 창은 따로 뜨지 않음 (Windows 권한 확인 창만 한 번).
    /// </summary>
    internal sealed class PcService
    {
        private readonly Action<string, object> _emit;
        public bool Busy { get; private set; }
        public PcService(Action<string, object> emit) { _emit = emit; }

        public static readonly string PackageRoot = Path.Combine(Program.DataDir, "packages");
        public static readonly string TaskRoot = Path.Combine(Program.DataDir, "tasks");
        private static readonly Regex SafeName = new Regex(@"^[A-Za-z0-9_.-]+$");

        private sealed class Req
        {
            public string Id, Engine, Repo, Tag, Asset, Url, Digest, AppId, StateDir;
            public static Req From(Dictionary<string, object> a)
            {
                string S(string k) => a.TryGetValue(k, out var v) ? v as string : null;
                var r = new Req { Id = S("id"), Engine = S("engine"), Repo = S("repo"), Tag = S("tag"), Asset = S("asset"), Url = S("url"), Digest = S("digest"), AppId = S("appid"), StateDir = S("stateDir") };
                if (r.Repo == null || !SafeName.IsMatch(r.Repo) || (r.Tag != null && !SafeName.IsMatch(r.Tag))) throw new ArgumentException("repo");
                if (r.Engine == null || !Regex.IsMatch(r.Engine, "^pc-[a-z0-9-]+$")) throw new ArgumentException("engine");
                if (r.AppId != null && !Regex.IsMatch(r.AppId, @"^\d{1,10}$")) throw new ArgumentException("appid");
                if (r.StateDir != null && !SafeName.IsMatch(r.StateDir)) throw new ArgumentException("stateDir");
                return r;
            }
            public string PackageDir => Tag == null ? null : Path.Combine(PackageRoot, Repo, Tag);
        }

        private static Dictionary<string, object> St(string current, string tag, string tone, string message) => new Dictionary<string, object>
        {
            ["current"] = current, ["tag"] = tag, ["tone"] = tone, ["message"] = message, ["canInstall"] = false, ["canRestore"] = false,
        };

        private void Say(string id, string m, double? p) => _emit("pc-progress", new Dictionary<string, object> { ["id"] = id, ["message"] = m, ["percent"] = p });

        // ───────────── 상태 ─────────────
        public Task<object> StateAsync(Dictionary<string, object> a)
        {
            var r = Req.From(a);
            long size = a.TryGetValue("size", out var sz) && sz != null ? Convert.ToInt64(sz) : -1;
            return Task.Run(async () =>
            {
                try
                {
                    // 작은 패치(100MB 이하)는 화면을 열 때 미리 받아 두고 실제 게임 상태를 바로 보여줌
                    if (!Busy && size > 0 && size <= 100L * 1024 * 1024 && ReadyPackage(r) == null && GameLocator.Find(r.Engine, r.AppId) != null)
                        try { await PrepareAsync(r, (m, p) => Say(r.Id, m, p)); } catch (Exception ex) { Log.Error("pc-prefetch", ex); }
                    return (object)Scan(r);
                }
                catch (Exception ex) { Log.Error("pc-scan", ex); return St("상태 확인 필요", "확인 필요", "warning", ex.Message); }
            });
        }

        private static string ReadyPackage(Req r)
        {
            var dir = r.PackageDir;
            if (dir == null || !File.Exists(Path.Combine(dir, ".ready"))) return null;
            // 예전 런처가 글꼴 라이선스 문서를 빼고 풀어 둔 AFT Steam 묶음은 다시 받아 풂
            if (r.Engine == "pc-aft-steam" && !Directory.Exists(Path.Combine(dir, "licenses"))) return null;
            return File.ReadAllText(Path.Combine(dir, ".ready")).Trim() is var rel && rel.Length > 0 ? Path.Combine(dir, rel) : dir;
        }

        private Dictionary<string, object> Scan(Req r)
        {
            var root = GameLocator.Find(r.Engine, r.AppId);
            if (root == null)
            {
                var s = St("게임을 찾지 못했어요", "게임 없음", "neutral", "이 PC에서 게임 설치 폴더를 찾지 못했습니다. 게임을 설치한 뒤 다시 확인해 주세요.");
                s["gameFound"] = false; return s;
            }
            var pkg = ReadyPackage(r);
            Dictionary<string, object> st;
            if (pkg == null)
            {
                st = St("확인 전", "준비됨", "neutral", "설치를 누르면 한글패치 데이터를 받아 게임 파일을 확인합니다.");
                st["canInstall"] = true; st["installLabel"] = "한글패치 설치"; st["packageReady"] = false;
            }
            else
            {
                var eng = PcPatch.Create(r.Engine, root, pkg);
                eng.UseCache(true);
                var i = eng.Inspect();
                var state = (string)i["state"]; var ver = i["version"] as string; var msg = i["message"] as string;
                // 검증된 최신 설치는 릴리스 번호로 표시·기록 (릴리스 번호가 설치기 버전인 저장소도 있음)
                if (state == "installed" && r.Tag != null) ver = Regex.Replace(r.Tag, @"^v\.?", "", RegexOptions.IgnoreCase);
                var running = eng.Running();
                st = state == "installed" ? St("v" + ver, "최신 버전", "good", msg)
                   : state == "original" ? St("미설치", "미설치", "neutral", msg)
                   : state == "mixed" ? St(ver != null && char.IsDigit(ver[0]) ? "v" + ver : "이전 버전", "업데이트 필요", "warning", msg)
                   : St("설치할 수 없음", "확인 필요", "warning", msg);
                st["installLabel"] = state == "installed" ? "설치 확인 / 재설치" : state == "mixed" ? "한글패치 업데이트" : "한글패치 설치";
                st["canInstall"] = state != "other" && !running;
                st["canRestore"] = (bool)i["canRestore"] && state != "original" && !running;
                st["installed"] = state == "installed";
                st["packageReady"] = true;
                if (running) st["message"] = "게임이 실행 중입니다. 게임을 종료한 뒤 다시 확인해 주세요.";
                if (st["message"] == null) st["message"] = NeedsAdmin(eng) ? "게임이 Program Files 아래에 있어 설치할 때 Windows 관리자 권한 확인이 한 번 뜹니다." : "설치 전 게임을 완전히 종료해 주세요.";
                WriteStatus(r, state, ver);
            }
            st["gameFound"] = true; st["gameRoot"] = root;
            return st;
        }

        private static bool NeedsAdmin(PcPatch eng)
        {
            try
            {
                foreach (var dir in eng.WriteDirs)
                {
                    if (!Directory.Exists(dir)) return true;
                    var probe = Path.Combine(dir, ".zecole-write-" + Guid.NewGuid().ToString("N"));
                    using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
                }
                return false;
            }
            catch { return true; }
        }

        /// <summary>홈 화면 목록이 읽는 표준 기록 (%LOCALAPPDATA%\Zecole\{stateDir}\zecole-status.json)</summary>
        private static void WriteStatus(Req r, string state, string version)
        {
            if (r.StateDir == null) return;
            try
            {
                var path = Path.Combine(Program.ZecoleRoot, r.StateDir, "zecole-status.json");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                QuestEngine.AtomicJson(path, new Dictionary<string, object>
                {
                    ["state"] = state == "installed" ? "installed" : state == "original" ? "restored" : state,
                    ["patch_version"] = state == "installed" ? version : null,
                    ["installer"] = "ZECOLE Launcher " + Program.Version, ["checked_at"] = DateTime.UtcNow.ToString("o"),
                });
            }
            catch (Exception ex) { Log.Error("pc-status", ex); }
        }

        // ───────────── 패치 데이터 준비 ─────────────
        private async Task<string> PrepareAsync(Req r, Action<string, double?> say)
        {
            var ready = ReadyPackage(r);
            if (ready != null) return ready;
            if (r.Url == null || r.Asset == null || r.Tag == null) throw new PatchError("릴리스 정보를 아직 받지 못했습니다. 잠시 후 다시 시도해 주세요.");
            if (!r.Url.StartsWith("https://github.com/zecole-vr/" + r.Repo + "/releases/download/", StringComparison.Ordinal)) throw new PatchError("zecole-vr 릴리스 파일만 사용할 수 있습니다.");
            var digest = r.Digest?.Replace("sha256:", "").ToLowerInvariant();
            if (digest == null || !Regex.IsMatch(digest, "^[a-f0-9]{64}$")) throw new PatchError("GitHub에 파일 해시 기록이 없어 설치하지 않습니다.");
            var dl = Path.Combine(PackageRoot, r.Repo, "_download"); Directory.CreateDirectory(dl);
            var file = Path.Combine(dl, Path.GetFileName(r.Asset));
            if (!(File.Exists(file) && Hash.File(file) == digest))
            {
                var t0 = DateTime.UtcNow;
                await Net.DownloadFileAsync(r.Url, file, (d, t) =>
                {
                    var secs = (DateTime.UtcNow - t0).TotalSeconds;
                    say($"한글패치 데이터 받는 중 · {d / 1048576}MB{(t > 0 ? " / " + t / 1048576 + "MB" : "")}{(secs > 1 ? $" · {d / 1048576.0 / secs:0.0}MB/s" : "")}", t > 0 ? d * 100.0 / t : (double?)null);
                });
                say("받은 파일 검증 중…", null);
                if (Hash.File(file) != digest) { File.Delete(file); throw new PatchError("받은 파일의 해시가 GitHub 기록과 다릅니다."); }
            }
            say("패치 데이터를 푸는 중…", null);
            var dir = r.PackageDir; var stage = dir + ".staging";
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            Directory.CreateDirectory(stage);
            var rel = await Task.Run(() => ExtractPackage(r.Engine, file, stage));
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(stage, dir);
            File.WriteAllText(Path.Combine(dir, ".ready"), rel);
            try { Directory.Delete(dl, true); } catch { }
            foreach (var other in Directory.GetDirectories(Path.Combine(PackageRoot, r.Repo)))
                if (!string.Equals(Path.GetFileName(other), r.Tag, StringComparison.OrdinalIgnoreCase)) try { Directory.Delete(other, true); } catch { }
            return ReadyPackage(r);
        }

        /// <summary>패치 묶음을 풀고, 기준 파일(manifest 등)이 있는 하위 폴더의 상대 경로를 돌려줌</summary>
        internal static string ExtractPackage(string engine, string file, string stage)
        {
            Extract(engine, file, stage);
            var marker = engine == "pc-aft-steam" ? "launcher_config.json" : engine == "pc-tf2vr" ? "package-hashes.json" : "manifest.json";
            var found = Directory.GetFiles(stage, marker, SearchOption.AllDirectories).OrderBy(x => x.Length).FirstOrDefault()
                        ?? throw new PatchError("패치 데이터 구성이 예상과 다릅니다.");
            return Path.GetDirectoryName(found).Substring(stage.Length).TrimStart('\\');
        }

        private static void Extract(string engine, string file, string stage)
        {
            string Safe(string name)
            {
                name = name.Replace('\\', '/');
                if (name.Length == 0 || name.StartsWith("/") || name.Contains(":") || name.Split('/').Any(x => x == "..")) throw new PatchError("패치 묶음의 파일 경로가 올바르지 않습니다.");
                return Path.Combine(stage, name.Replace('/', '\\'));
            }
            switch (engine)
            {
                case "pc-aft-steam":
                    if (file.EndsWith(".zpkg", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        // 데이터 전용 묶음(AFT_Steam_v*.zpkg): 루트에 launcher_config.json, patch/, licenses/
                        using (var z = ZipFile.OpenRead(file))
                            foreach (var e in z.Entries.Where(e => !e.FullName.EndsWith("/")))
                            {
                                var t = Safe(e.FullName); Directory.CreateDirectory(Path.GetDirectoryName(t)); e.ExtractToFile(t, true);
                            }
                        foreach (var need in new[] { "launcher_config.json", @"patch\manifest.json", @"patch\Fisherman-KoreanSubtitles_P.pak" })
                            if (!File.Exists(Path.Combine(stage, need))) throw new PatchError("패치 데이터에 " + need + " 이(가) 없습니다.");
                        break;
                    }
                    // 예전 통합 설치기 EXE 안의 Steam 묶음만 (글꼴 등 라이선스 문서도 함께 보관)
                    const string pre = "bundle/platforms/Steam/";
                    var got = PyInstallerArchive.ExtractWhere(file, n => n.StartsWith(pre) && !n.Contains("/ui/") ? Safe(n.Substring(pre.Length)) : null);
                    if (!got.Contains(pre + "patch/Fisherman-KoreanSubtitles_P.pak")) throw new PatchError("설치기 안에 Steam용 패치가 없습니다.");
                    break;
                case "pc-convrgence":
                    if (file.EndsWith(".zpkg", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        // 데이터 전용 묶음(CONVRGENCE_Steam_v*.zpkg): 설치기 bundle/ 과 같은 구성(manifest.json, payload/, runtime/, sources/, licenses/)
                        using (var z = ZipFile.OpenRead(file))
                            foreach (var e in z.Entries.Where(e => !e.FullName.EndsWith("/")))
                            {
                                var t = Safe(e.FullName); Directory.CreateDirectory(Path.GetDirectoryName(t)); e.ExtractToFile(t, true);
                            }
                    }
                    else
                    {
                        // 예전 설치기 EXE 안의 bundle/ (설치기 화면 ui/ 는 제외)
                        PyInstallerArchive.ExtractWhere(file, n => n.StartsWith("bundle/") && !n.StartsWith("bundle/ui/") ? Safe(n.Substring(7)) : null);
                    }
                    if (!File.Exists(Path.Combine(stage, "manifest.json"))) throw new PatchError("패치 데이터에 manifest.json 이(가) 없습니다.");
                    break;
                case "pc-tf2vr":       // GitHub 배포 zip 의 payload 폴더
                    using (var z = ZipFile.OpenRead(file))
                        foreach (var e in z.Entries.Where(e => e.FullName.StartsWith("payload/") && !e.FullName.EndsWith("/")))
                        {
                            var t = Safe(e.FullName.Substring(8)); Directory.CreateDirectory(Path.GetDirectoryName(t)); e.ExtractToFile(t);
                        }
                    break;
                default:               // 비밀번호(AES) zip 전체
                    using (var z = new ICSharpCode.SharpZipLib.Zip.ZipFile(file) { Password = "zecole" })
                        foreach (ICSharpCode.SharpZipLib.Zip.ZipEntry e in z)
                        {
                            if (!e.IsFile) continue;
                            var t = Safe(e.Name); Directory.CreateDirectory(Path.GetDirectoryName(t));
                            using (var s = z.GetInputStream(e)) using (var f = File.Create(t)) s.CopyTo(f, 1 << 20);
                        }
                    break;
            }
        }

        // ───────────── 설치 · 복구 ─────────────
        public async Task<object> RunAsync(Dictionary<string, object> a)
        {
            var r = Req.From(a);
            var action = a["action"] as string;
            if (action != "install" && action != "restore") throw new ArgumentException("action");
            if (Busy) throw new PatchError("다른 작업이 진행 중입니다.");
            Busy = true;
            var logDir = Path.Combine(Program.DataDir, "logs"); Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{r.Id}-{action}.log");
            void say(string m, double? p) { Say(r.Id, m, p); try { File.AppendAllText(logPath, $"{DateTime.Now:o} {m}{Environment.NewLine}"); } catch { } }
            try
            {
                var pkg = await PrepareAsync(r, say);
                var root = GameLocator.Find(r.Engine, r.AppId) ?? throw new PatchError("게임 설치 폴더를 찾지 못했습니다.");
                var eng = PcPatch.Create(r.Engine, root, pkg);
                string message;
                // 바꿀 것이 없으면 관리자 권한 없이 검증만 (해시는 새로 계산)
                say("게임 파일 확인 중…", null);
                var now = await Task.Run(() => eng.Inspect());
                var nowState = (string)now["state"];
                if (nowState == "other") throw new PatchError(now["message"] as string ?? "지원하지 않는 게임 상태입니다.");
                if (action == "install" && nowState == "installed" && !eng.AlwaysReapply && !eng.AlwaysRunEngine) message = "이미 최신 한글패치가 설치되어 있습니다. 게임 파일 검증을 마쳤습니다.";
                else if (action == "restore" && nowState == "original" && !eng.AlwaysRunEngine) message = "이미 원본 상태입니다.";
                else
                {
                    // 필요한 추가 파일은 관리자 권한을 요청하기 전에 일반 권한으로 받아 둠
                    if (action == "install") await Task.Run(() => eng.Prefetch(say));
                    if (NeedsAdmin(eng))
                    {
                        say("Windows 관리자 권한 확인 창에서 '예'를 눌러 주세요…", null);
                        message = await RunElevatedAsync(r.Engine, root, pkg, action, say);
                    }
                    else message = await Task.Run(() => eng.Apply(action, say));
                }
                say(message, 100);
                var st = Scan(r);
                st["success"] = true; st["message"] = message; st["log"] = logPath;
                return st;
            }
            catch (Exception ex)
            {
                Log.Error("pc-run", ex);
                try { File.AppendAllText(logPath, ex + Environment.NewLine); } catch { }
                Dictionary<string, object> st;
                try { st = Scan(r); } catch { st = St("상태 확인 필요", "확인 필요", "warning", null); }
                st["failed"] = true; st["log"] = logPath;
                st["message"] = ex is PatchError ? ex.Message : "작업 중 오류가 발생했습니다: " + ex.Message;
                return st;
            }
            finally { Busy = false; }
        }

        /// <summary>같은 EXE 를 관리자 권한으로 실행해 작업 하나만 처리 (진행 상황은 파일로 전달)</summary>
        private static async Task<string> RunElevatedAsync(string engine, string root, string pkg, string action, Action<string, double?> say)
        {
            Directory.CreateDirectory(TaskRoot);
            var id = Guid.NewGuid().ToString("N");
            var task = Path.Combine(TaskRoot, id + ".json");
            File.WriteAllText(task, Bridge.Json.Serialize(new Dictionary<string, object> { ["engine"] = engine, ["root"] = root, ["pkg"] = pkg, ["action"] = action }), new UTF8Encoding(false));
            var progress = task + ".progress"; var result = task + ".result";
            Process p;
            try
            {
                p = Process.Start(new ProcessStartInfo(System.Windows.Forms.Application.ExecutablePath, $"--pc-task \"{task}\"")
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                File.Delete(task);
                throw new PatchError("관리자 권한 요청을 취소했습니다. 게임 파일은 바뀌지 않았습니다.");
            }
            long read = 0;
            void Pump()
            {
                try
                {
                    if (!File.Exists(progress)) return;
                    using (var f = new FileStream(progress, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        if (f.Length <= read) return;
                        f.Seek(read, SeekOrigin.Begin);
                        var buf = new byte[f.Length - read]; int n = f.Read(buf, 0, buf.Length);
                        var text = Encoding.UTF8.GetString(buf, 0, n);
                        var end = text.LastIndexOf('\n'); if (end < 0) return;
                        read += Encoding.UTF8.GetByteCount(text.Substring(0, end + 1));
                        foreach (var line in text.Substring(0, end).Split('\n'))
                            if (Bridge.Json.DeserializeObject(line) is Dictionary<string, object> d)
                                say(d["m"] as string, d.TryGetValue("p", out var pv) && pv != null ? Convert.ToDouble(pv) : (double?)null);
                    }
                }
                catch { }
            }
            while (!p.HasExited) { Pump(); await Task.Delay(400); }
            Pump();
            try
            {
                if (!File.Exists(result)) throw new PatchError("관리자 작업 결과를 확인하지 못했습니다. 게임 상태를 다시 확인해 주세요.");
                var res = (Dictionary<string, object>)Bridge.Json.DeserializeObject(File.ReadAllText(result, Encoding.UTF8));
                if (!(res["ok"] is bool ok && ok)) throw new PatchError(res["message"] as string ?? "작업에 실패했습니다.");
                return res["message"] as string;
            }
            finally { foreach (var f in new[] { task, progress, result }) try { File.Delete(f); } catch { } }
        }
    }

    /// <summary>관리자 권한으로 실행된 런처 EXE 쪽: 작업 하나를 처리하고 바로 종료</summary>
    internal static class PcTask
    {
        public static int Run(string taskPath)
        {
            // 작업 파일은 ...\Zecole\Launcher\tasks\<guid>.json 형태만 허용. 아니면 어떤 파일도 쓰지 않고 종료
            string full;
            try { full = Path.GetFullPath(taskPath); } catch { return 2; }
            var tasks = Path.GetDirectoryName(full);
            var launcherDir = Path.GetDirectoryName(tasks);
            if (!Regex.IsMatch(Path.GetFileName(full), "^[a-f0-9]{32}\\.json$") || !string.Equals(Path.GetFileName(tasks), "tasks", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(launcherDir), "Launcher", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(Path.GetDirectoryName(launcherDir)), "Zecole", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(full))
                return 2;
            string result = full + ".result", progress = full + ".progress";
            void Write(Dictionary<string, object> d) => File.WriteAllText(result, Bridge.Json.Serialize(d), new UTF8Encoding(false));
            try
            {
                var t = (Dictionary<string, object>)Bridge.Json.DeserializeObject(File.ReadAllText(full, Encoding.UTF8));
                string engine = (string)t["engine"], root = Path.GetFullPath((string)t["root"]), pkg = Path.GetFullPath((string)t["pkg"]), action = (string)t["action"];
                if (!pkg.StartsWith(Path.Combine(launcherDir, "packages") + "\\", StringComparison.OrdinalIgnoreCase)) throw new PatchError("패치 데이터 위치가 올바르지 않습니다.");
                if (action != "install" && action != "restore") throw new PatchError("지원하지 않는 작업입니다.");
                var eng = PcPatch.Create(engine, root, pkg);
                // CONVRGENCE: UAC 에서 다른 관리자 계정으로 승인해도 요청한 사용자의 기록·백업 폴더(…\Zecole\CONVRGENCE)를 씀
                if (eng is ConvrgenceSteam conv) conv.Storage = Path.Combine(Path.GetDirectoryName(launcherDir), "CONVRGENCE");
                if (!Directory.Exists(root) || !eng.LooksLikeGame(root) || (new DirectoryInfo(root).Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new PatchError("게임 설치 폴더가 올바르지 않습니다.");
                var msg = eng.Apply(action, (m, p) =>
                {
                    try { File.AppendAllText(progress, Bridge.Json.Serialize(new Dictionary<string, object> { ["m"] = m, ["p"] = p }) + "\n", new UTF8Encoding(false)); } catch { }
                });
                Write(new Dictionary<string, object> { ["ok"] = true, ["message"] = msg });
                return 0;
            }
            catch (Exception ex)
            {
                try { Write(new Dictionary<string, object> { ["ok"] = false, ["message"] = ex is PatchError ? ex.Message : "작업 중 오류: " + ex.Message }); } catch { }
                return 1;
            }
        }
    }
}
