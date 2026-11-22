using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Zecole.Launcher
{
    /// <summary>
    /// 게임별 설치기를 GitHub 릴리스에서 받아 SHA-256 검증 후 실행.
    /// 백업·복구·Quest 전송 같은 실제 설치 로직은 각 게임 설치기가 그대로 담당.
    /// </summary>
    internal sealed class Installer
    {
        public static readonly string DownloadRoot = Path.Combine(Program.DataDir, "downloads");
        private static readonly string RecordsPath = Path.Combine(Program.DataDir, "records.json");
        private const string AllowedPrefix = "https://github.com/zecole-vr/";

        private readonly Action<string, object> _emit;
        private CancellationTokenSource _cts;
        public bool Busy { get; private set; }

        public Installer(Action<string, object> emit) { _emit = emit; }

        public void Cancel() => _cts?.Cancel();

        public async Task<object> StartAsync(Dictionary<string, object> a)
        {
            if (Busy) throw new InvalidOperationException("다른 설치가 진행 중입니다.");
            string id = a["id"] as string, repo = a["repo"] as string, tag = a["tag"] as string,
                   asset = a["asset"] as string, url = a["url"] as string, mode = a["mode"] as string ?? "exe";
            var digest = (a.TryGetValue("digest", out var d) ? d as string : null)?.Replace("sha256:", "").ToLowerInvariant();

            if (url == null || !url.StartsWith(AllowedPrefix + repo + "/releases/download/", StringComparison.Ordinal))
                throw new InvalidOperationException("zecole-vr 릴리스 파일만 설치할 수 있습니다.");
            if (asset.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || tag.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidOperationException("잘못된 파일 이름입니다.");

            Busy = true;
            _cts = new CancellationTokenSource();
            var dir = Path.Combine(DownloadRoot, repo, tag);
            var file = Path.Combine(dir, asset);
            try
            {
                Directory.CreateDirectory(dir);

                // 1) 받기 (이미 받은 같은 파일이 있으면 생략)
                var have = File.Exists(file) && (digest == null || Net.Sha256File(file) == digest);
                if (!have)
                {
                    Emit(id, "download", 0, -1);
                    var sw = Stopwatch.StartNew();
                    await Net.DownloadFileAsync(url, file, (done, total) => Emit(id, "download", done, total, sw.Elapsed.TotalSeconds), _cts.Token);
                }

                // 2) 검증
                Emit(id, "verify", 0, 0);
                if (digest != null)
                {
                    var actual = await Task.Run(() => Net.Sha256File(file));
                    if (actual != digest)
                    {
                        File.Delete(file);
                        throw new InvalidOperationException("받은 파일의 해시가 GitHub 기록과 다릅니다. 다시 시도해 주세요.");
                    }
                }

                // 3) 실행
                int exitCode = 0;
                string extracted = null;
                if (mode == "zip")
                {
                    extracted = Path.Combine(dir, Path.GetFileNameWithoutExtension(asset));
                    Emit(id, "extract", 0, 0);
                    await Task.Run(() =>
                    {
                        if (Directory.Exists(extracted)) Directory.Delete(extracted, true);
                        ZipFile.ExtractToDirectory(file, extracted);
                    });
                    Shell.OpenFolder(extracted);
                }
                else
                {
                    Emit(id, "running", 0, 0);
                    Process p;
                    try
                    {
                        p = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, WorkingDirectory = dir });
                    }
                    catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                    {
                        throw new InvalidOperationException("설치기 실행이 취소되었습니다. (관리자 권한 요청 거절)");
                    }
                    if (p != null) { await Task.Run(() => p.WaitForExit()); exitCode = p.ExitCode; }
                }

                SaveRecord(repo, new Dictionary<string, object>
                {
                    ["tag"] = tag, ["asset"] = asset, ["ranAt"] = DateTime.UtcNow.ToString("o"), ["exitCode"] = exitCode, ["mode"] = mode,
                });
                PruneOld(repo, tag);
                Emit(id, "done", 0, 0);
                return new Dictionary<string, object> { ["exitCode"] = exitCode, ["file"] = file, ["extracted"] = extracted };
            }
            catch (OperationCanceledException)
            {
                TryDelete(file + ".part");
                Emit(id, "cancelled", 0, 0);
                throw new InvalidOperationException("받기를 취소했습니다.");
            }
            catch (Exception ex)
            {
                Log.Error("install " + repo, ex);
                Emit(id, "error", 0, 0);
                throw;
            }
            finally
            {
                Busy = false;
            }
        }

        private void Emit(string id, string phase, long done, long total, double secs = 0) =>
            _emit("install-progress", new Dictionary<string, object> { ["id"] = id, ["phase"] = phase, ["done"] = done, ["total"] = total, ["secs"] = secs });

        /// <summary>같은 게임의 이전 버전 설치기 파일은 지워서 용량 확보</summary>
        private static void PruneOld(string repo, string keepTag)
        {
            try
            {
                foreach (var d in Directory.GetDirectories(Path.Combine(DownloadRoot, repo)))
                    if (!string.Equals(Path.GetFileName(d), keepTag, StringComparison.OrdinalIgnoreCase)) Directory.Delete(d, true);
            }
            catch (Exception ex) { Log.Error("prune", ex); }
        }

        private static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

        public static Dictionary<string, object> LoadRecords()
        {
            try
            {
                if (File.Exists(RecordsPath))
                    return Bridge.Json.DeserializeObject(File.ReadAllText(RecordsPath, Encoding.UTF8)) as Dictionary<string, object> ?? new Dictionary<string, object>();
            }
            catch { }
            return new Dictionary<string, object>();
        }

        private static void SaveRecord(string repo, Dictionary<string, object> rec)
        {
            var all = LoadRecords();
            all[repo] = rec;
            File.WriteAllText(RecordsPath, Bridge.Json.Serialize(all), Encoding.UTF8);
        }
    }

    /// <summary>런처 자체 업데이트: zecole-vr/zecole-launcher 릴리스의 새 EXE 로 교체 후 재시작</summary>
    internal static class SelfUpdate
    {
        private const string AllowedPrefix = "https://github.com/zecole-vr/zecole-launcher/releases/download/";

        public static void CleanupOld()
        {
            try
            {
                var old = Application.ExecutablePath + ".old";
                if (File.Exists(old)) File.Delete(old);
            }
            catch { /* 이전 프로세스가 아직 잡고 있으면 다음 실행 때 정리 */ }
        }

        public static async Task<object> ApplyAsync(string url, string digest, Form form, Action<string, object> emit)
        {
            if (url == null || !url.StartsWith(AllowedPrefix, StringComparison.Ordinal)) throw new InvalidOperationException("허용되지 않은 업데이트 주소");
            var exe = Application.ExecutablePath;
            var fresh = exe + ".new";
            await Net.DownloadFileAsync(url, fresh, (done, total) =>
                emit("update-progress", new Dictionary<string, object> { ["done"] = done, ["total"] = total }));
            digest = digest?.Replace("sha256:", "").ToLowerInvariant();
            if (digest != null && Net.Sha256File(fresh) != digest)
            {
                File.Delete(fresh);
                throw new InvalidOperationException("업데이트 파일 검증에 실패했습니다.");
            }
            // 실행 중인 EXE 는 이름 변경은 가능 → 교체 후 새 EXE 실행
            if (File.Exists(exe + ".old")) File.Delete(exe + ".old");
            File.Move(exe, exe + ".old");
            File.Move(fresh, exe);
            var hint = Program.StartHint;
            Program.ReleaseSingleInstance();
            Process.Start(new ProcessStartInfo(exe, hint != null ? "--game=\"" + hint + "\"" : "") { UseShellExecute = true });
            form.BeginInvoke(new Action(() => Application.Exit()));
            return true;
        }
    }
}
