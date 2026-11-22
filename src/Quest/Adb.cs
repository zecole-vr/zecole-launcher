using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Zecole.Launcher.Quest
{
    /// <summary>adb.exe 실행 래퍼 (quest_patch.Adb 이식)</summary>
    internal sealed class Adb
    {
        public readonly string Exe;
        public string Serial;      // ADB 연결 주소 (USB 기기 번호 또는 무선 "IP:포트")
        public string DeviceId;    // 실제 기기 번호(ro.serialno). 저장 폴더·설치 기록은 이 값 기준 (무선이어도 같은 폴더)
        public Action<string> Say = _ => { };

        public bool Wireless => Serial != null && Serial.Contains(':');
        /// <summary>폴더 이름으로 써도 안전한 기기 번호인지 (., .. 같은 상대 경로 금지)</summary>
        public static bool SafeId(string s) => s != null && Regex.IsMatch(s, @"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,63}$") && !s.Contains("..");
        /// <summary>
        /// PC 보관 폴더 이름에 쓰는 기기 이름.
        /// USB 는 예전과 같이 연결 번호(기존 사용자 기록 그대로), 무선은 실제 기기 번호(USB 때와 같은 폴더).
        /// 무선인데 기기 번호를 모르면 빈 값 → 작업하지 않음.
        /// </summary>
        public string Key
        {
            get
            {
                var k = Wireless ? DeviceId : Regex.Replace(Serial ?? "", @"[^A-Za-z0-9_.-]", "_");
                return SafeId(k) ? k : "";
            }
        }
        /// <summary>대용량 전송 제한 시간: 무선은 USB보다 몇 배 느리므로 넉넉히</summary>
        public int BulkTimeout(int usbSec) => Wireless ? Math.Max(usbSec * 4, 3600) : usbSec;

        public Adb(string exe, string serial = null) { Exe = exe; Serial = serial; }

        public (string output, int code) Run(string[] args, int timeoutSec = 180, bool check = true, CancellationToken ct = default) => Run(args, null, timeoutSec, check, ct);

        /// <summary>input 이 있으면 표준 입력으로 넘김 (비밀 값을 명령줄에 남기지 않기 위해)</summary>
        public (string output, int code) Run(string[] args, string input, int timeoutSec = 180, bool check = true, CancellationToken ct = default)
        {
            var all = (Serial != null ? new[] { "-s", Serial } : new string[0]).Concat(args);
            var psi = new ProcessStartInfo(Exe, string.Join(" ", all.Select(WinQuote)))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input != null,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            using (var p = Process.Start(psi))
            {
                if (input != null)
                {
                    var bytes = new UTF8Encoding(false).GetBytes(input);
                    p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                    p.StandardInput.Close();
                }
                var sb = new StringBuilder();
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                var sw = Stopwatch.StartNew();
                while (!p.WaitForExit(1000))
                {
                    if (ct.IsCancellationRequested || sw.Elapsed.TotalSeconds > timeoutSec)
                    {
                        try { p.Kill(); } catch { }
                        throw new PatchError(ct.IsCancellationRequested ? "작업을 취소했습니다." : "ADB 응답 시간이 초과되었습니다.");
                    }
                    if (sw.Elapsed.TotalSeconds >= 15 && (int)sw.Elapsed.TotalSeconds % 15 == 0) Say($"파일 검사/전송 진행 중... {(int)sw.Elapsed.TotalSeconds}초");
                }
                p.WaitForExit();
                string output; lock (sb) output = sb.ToString().Trim();
                if (check && p.ExitCode != 0) throw new PatchError("ADB 작업 실패: " + output);
                return (output, p.ExitCode);
            }
        }

        /// <summary>adb shell 은 인자를 하나의 문자열로 이어 붙이므로 각 인자를 sh 규칙으로 감쌈 (shlex.quote 와 동일)</summary>
        public (string output, int code) Shell(params object[] args) => Shell(180, true, args);
        public (string output, int code) Shell(int timeoutSec, bool check, params object[] args) =>
            Run(new[] { "shell", string.Join(" ", args.Select(a => ShQuote(a.ToString()))) }, timeoutSec, check);

        /// <summary>
        /// 기기 파일이 있는지. 연결 끊김(특히 무선)을 '파일 없음'으로 오판하지 않도록
        /// 셸이 직접 찍은 표시로만 판단하고, 표시가 없으면 오류로 멈춤.
        /// </summary>
        public bool Exists(string path)
        {
            var (o, _) = Run(new[] { "shell", "if [ -e " + ShQuote(path) + " ]; then echo ZECOLE_EXISTS_1; else echo ZECOLE_EXISTS_0; fi" }, 180, false);
            if (o.Contains("ZECOLE_EXISTS_1")) return true;
            if (o.Contains("ZECOLE_EXISTS_0")) return false;
            throw new PatchError("Quest 연결이 끊겨 기기 파일을 확인하지 못했습니다. 연결을 확인한 뒤 다시 시도해 주세요.");
        }

        public string Hash(string path, bool missingOk = false)
        {
            if (missingOk && !Exists(path)) return null;
            var (o, _) = Shell(600, true, "sha256sum", path);
            if (!Regex.IsMatch(o, @"^[0-9a-fA-F]{64}\s")) throw new PatchError("기기 파일의 체크섬을 읽지 못했습니다: " + path);
            return o.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
        }

        public long? RemoteSize(string path)
        {
            try
            {
                var (o, c) = Shell(20, false, "stat", "-c", "%s", path);
                return c == 0 && long.TryParse(o.Trim(), out var n) ? n : (long?)null;
            }
            catch { return null; }
        }

        private static string ShQuote(string s)
        {
            if (s.Length == 0) return "''";
            if (Regex.IsMatch(s, @"^[\w@%+=:,./-]+$")) return s;
            return "'" + s.Replace("'", "'\"'\"'") + "'";
        }

        /// <summary>Windows 명령줄 인자 규칙으로 감싸기</summary>
        private static string WinQuote(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int bs = 0;
            foreach (var ch in a)
            {
                if (ch == '\\') { bs++; continue; }
                if (ch == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); bs = 0; continue; }
                sb.Append('\\', bs); bs = 0; sb.Append(ch);
            }
            sb.Append('\\', bs * 2).Append('"');
            return sb.ToString();
        }
    }

    /// <summary>
    /// ADB 찾기·준비.
    /// 1) 런처가 받아 둔 공식 ADB  2) 기존 제콜 설치기들이 받아 둔 ADB  3) PATH·SideQuest·Android SDK
    /// 없으면 Google 약관 동의 후 공식 platform-tools 를 받음 (주소·SHA-256 고정).
    /// </summary>
    internal static class AdbTools
    {
        public const string Url = "https://dl.google.com/android/repository/platform-tools_r37.0.0-win.zip";
        public const string ZipHash = "4fe305812db074cea32903a489d061eb4454cbc90a49e8fea677f4b7af764918";
        public const string Terms = "https://developer.android.com/studio/terms";
        private static readonly string[] Required = { "adb.exe", "AdbWinApi.dll", "AdbWinUsbApi.dll", "NOTICE.txt" };
        public static readonly string Home = Path.Combine(Program.DataDir, "tools", "platform-tools-37.0.0");

        public static string Find()
        {
            var own = Path.Combine(Home, "adb.exe");
            if (ValidReceipt(Home)) return own;
            // 기존 설치기들이 약관 동의 후 받아 둔 같은 버전 ADB 재사용
            try
            {
                foreach (var dir in Directory.GetDirectories(Program.ZecoleRoot))
                {
                    var cand = Path.Combine(dir, "Quest", ".tools", "platform-tools-37.0.0");
                    if (ValidReceipt(cand)) return Path.Combine(cand, "adb.exe");
                }
            }
            catch { }
            var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Select(p => Path.Combine(p.Trim('"'), "adb.exe"));
            var known = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SideQuest", "platform-tools", "adb.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe"),
            };
            return path.Concat(known).FirstOrDefault(p => { try { return File.Exists(p); } catch { return false; } });
        }

        private static bool ValidReceipt(string dir)
        {
            try
            {
                var receipt = Path.Combine(dir, "download-receipt.json");
                if (!File.Exists(receipt)) return false;
                var r = Bridge.Json.DeserializeObject(File.ReadAllText(receipt)) as Dictionary<string, object>;
                if (r == null || r["archive_sha256"] as string != ZipHash || !(r["license_accepted"] is bool ok && ok)) return false;
                var files = r["files"] as Dictionary<string, object>;
                return Required.All(n => files.ContainsKey(n)) &&
                       files.All(kv => Path.GetFileName(kv.Key) == kv.Key && File.Exists(Path.Combine(dir, kv.Key)) && Hash.File(Path.Combine(dir, kv.Key)) == kv.Value as string);
            }
            catch { return false; }
        }

        /// <summary>사용자가 약관에 동의한 뒤에만 호출</summary>
        public static async Task<string> DownloadAsync(Action<string, double?> say)
        {
            var parent = Path.GetDirectoryName(Home);
            Directory.CreateDirectory(parent);
            var stage = Path.Combine(parent, "download-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(stage);
            try
            {
                var zip = Path.Combine(stage, "platform-tools.zip");
                say("연결 도구 ADB를 Google 공식 서버에서 받는 중…", null);
                await Net.DownloadFileAsync(Url, zip, (d, t) => say($"ADB 받는 중 · {d / 1024}KB", t > 0 ? d * 100.0 / t : (double?)null));
                if (Hash.File(zip) != ZipHash) throw new PatchError("ADB 다운로드 체크섬이 다릅니다. 실행하지 않고 중단합니다.");
                using (var z = ZipFile.OpenRead(zip))
                {
                    foreach (var e in z.Entries)
                    {
                        var parts = e.FullName.Split('/');
                        if (parts[0] != "platform-tools" || parts.Any(x => x == "." || x == ".." || x.Contains(':') || x.Contains('\\')))
                            throw new PatchError("ADB 압축 파일에 잘못된 경로가 있습니다.");
                    }
                    z.ExtractToDirectory(stage);
                }
                var tool = Path.Combine(stage, "platform-tools");
                foreach (var n in Required) if (!File.Exists(Path.Combine(tool, n))) throw new PatchError("ADB 필수 파일이 빠졌습니다: " + n);
                var receipt = new Dictionary<string, object>
                {
                    ["url"] = Url, ["archive_sha256"] = ZipHash, ["license_accepted"] = true, ["terms_url"] = Terms,
                    ["accepted_at"] = DateTime.Now.ToString("o"),
                    ["files"] = Directory.GetFiles(tool).ToDictionary(Path.GetFileName, f => (object)Hash.File(f)),
                };
                File.WriteAllText(Path.Combine(tool, "download-receipt.json"), Bridge.Json.Serialize(receipt), Encoding.UTF8);
                if (Directory.Exists(Home)) Directory.Delete(Home, true);
                Directory.Move(tool, Home);
                say("ADB 준비 완료", null);
                return Path.Combine(Home, "adb.exe");
            }
            finally { try { Directory.Delete(stage, true); } catch { } }
        }
    }
}
