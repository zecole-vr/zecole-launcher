using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Zecole.Launcher.Quest
{
    /// <summary>
    /// Quest 한글패치 데이터 (format 2): manifest.json + data/recipe.json + data/payload.bin
    ///
    /// 얻는 방법
    ///  1) 릴리스에 *.zpkg (위 세 파일을 담은 zip) 가 있으면 그것을 받음 (가벼움)
    ///  2) 없으면 기존 설치기 EXE 를 받아 그 안의 bundle/ 에서 세 파일만 꺼냄 (기존 릴리스 그대로 지원)
    /// 어느 쪽이든 manifest 의 SHA-256 으로 recipe·payload 를 검증한 뒤에만 사용.
    /// </summary>
    internal sealed class QuestPackage
    {
        public string Dir;
        public Dictionary<string, object> M;      // manifest
        public List<object[]> Ops;                // recipe operations
        public string PayloadPath => Path.Combine(Dir, "data", "payload.bin");

        public static readonly string Root = Path.Combine(Program.DataDir, "packages");

        public string S(string k) => M.TryGetValue(k, out var v) ? v as string : null;
        public int Format => Convert.ToInt32(M["format"]);
        /// <summary>형식 1(PAYDAY·ITR2)은 원본과 결과 크기가 같아 main_bytes 하나만 씀</summary>
        public long SourceBytes => Format == 1 ? L("main_bytes") : L("source_bytes");
        public long TargetBytes => Format == 1 ? L("main_bytes") : L("target_bytes");
        /// <summary>
        /// 초기 형식 1 설치기(ITR2 · questko 엔진): manifest 에 backup_suffix·기기 모델·APK 해시가 없고,
        /// 작업 폴더가 userdata/ 아래, PC 원본이 originals/&lt;main_name&gt;, 두 번째 OBB(patch_name)를 함께 확인함.
        /// </summary>
        public bool Legacy => Format == 1 && !M.ContainsKey("backup_suffix");
        /// <summary>PC 원본 백업 파일 이름 (기존 설치기와 같게)</summary>
        public string PcOriginalName => Legacy ? Path.Combine("originals", S("main_name")) : Format == 1 ? "original.obb" : "original.hdog";
        public long L(string k) => Convert.ToInt64(M[k]);
        public string ObbDir => "/sdcard/Android/obb/" + S("package");
        public string Main => ObbDir + "/" + S("main_name");
        public string Backup => Main + (S("backup_suffix") ?? ".questko-original");
        /// <summary>한글패치가 바꾸지 않지만 같은 빌드인지 확인하는 두 번째 OBB (ITR2)</summary>
        public string PatchObb => S("patch_name") == null ? null : ObbDir + "/" + S("patch_name");
        /// <summary>설치 전에 PC로 백업할 세이브 폴더 (ITR2 설치기와 같은 위치)</summary>
        public string SaveDir => S("save_dir") ?? (S("package") == "com.CMGames.IntoTheRadius2"
            ? "/sdcard/Android/data/com.CMGames.IntoTheRadius2/files/UnrealGame/IntoTheRadius2/IntoTheRadius2/Saved" : null);
        /// <summary>PC 없이 쓰는 Quest 단독 설치기의 기록 폴더. 있으면 그 설치기가 기기 원본 백업을 사용함</summary>
        public string MobileStateDir => ObbDir + "/.zecole-mobile-" + M["version_code"];

        public HashSet<string> KnownHashes
        {
            get
            {
                var h = new HashSet<string> { S("source_sha256"), S("target_sha256") };
                if (M.TryGetValue("compatible_previous", out var cp) && cp is Dictionary<string, object> d) foreach (var k in d.Keys) h.Add(k);
                return h;
            }
        }

        public Dictionary<string, object> Dict(string k) => M.TryGetValue(k, out var v) ? v as Dictionary<string, object> ?? new Dictionary<string, object>() : new Dictionary<string, object>();

        /// <summary>quest_patch.load_release 와 같은 검증</summary>
        public static QuestPackage Load(string dir)
        {
            var mPath = Path.Combine(dir, "manifest.json");
            var m = Bridge.Json.DeserializeObject(File.ReadAllText(mPath, Encoding.UTF8)) as Dictionary<string, object>;
            int format = m == null ? 0 : Convert.ToInt32(m["format"]);
            if (format != 1 && format != 2) throw new PatchError("지원하지 않는 배포 형식입니다.");
            // 기기 경로에 쓰이는 이름은 단순한 파일 이름만 허용
            foreach (var k in new[] { "package", "main_name", "patch_name" })
                if (m.TryGetValue(k, out var nv) && nv != null && !Regex.IsMatch(nv as string ?? "", @"^[A-Za-z0-9_][A-Za-z0-9_.-]*$"))
                    throw new PatchError("배포 정보의 파일 이름이 올바르지 않습니다.");
            if (m.ContainsKey("patch_name") && !Regex.IsMatch(m.TryGetValue("patch_sha256", out var ps) ? ps as string ?? "" : "", "^[a-f0-9]{64}$"))
                throw new PatchError("배포 정보에 추가 데이터 해시가 없습니다.");
            foreach (var (name, key) in new[] { ("recipe.json", "recipe_sha256"), ("payload.bin", "payload_sha256") })
                if (Hash.File(Path.Combine(dir, "data", name)) != (string)m[key]) throw new PatchError("패치 데이터 무결성 오류: " + name);

            var recipe = Bridge.Json.DeserializeObject(File.ReadAllText(Path.Combine(dir, "data", "recipe.json"), Encoding.UTF8)) as Dictionary<string, object>;
            if (recipe == null || Convert.ToInt32(recipe["format"]) != format) throw new PatchError("지원하지 않는 변환 형식입니다.");
            var ops = ((object[])recipe["operations"]).Select(o => (object[])o).ToList();
            long sourceBytes = Convert.ToInt64(m[format == 1 ? "main_bytes" : "source_bytes"]), targetBytes = Convert.ToInt64(m[format == 1 ? "main_bytes" : "target_bytes"]);
            long payloadSize = new FileInfo(Path.Combine(dir, "data", "payload.bin")).Length;
            const long CHUNK = 8 * 1024 * 1024;
            long total = 0;
            foreach (var op in ops)
            {
                if (format == 1)
                {
                    // quest_patch.py format 1: copy / data / zero / zlib(원본 구간을 zlib 레벨 9로 재압축)
                    // rezlib: 원본의 zlib 조각을 풀어 레벨 9로 다시 압축 (A Fisherman's Tale PAK 재구성)
                    var k1 = op?.Length > 0 ? op[0] as string : null;
                    if (op == null || (op.Length != 3 && op.Length != 5) || !new[] { "copy", "data", "zero", "zlib", "rezlib" }.Contains(k1)) throw new PatchError("변환 정보가 손상되었습니다.");
                    if ((k1 == "zlib" || k1 == "rezlib") != (op.Length == 5)) throw new PatchError("변환 정보가 손상되었습니다.");
                    long o1 = N(op[1]), l1 = N(op[2]);
                    if (o1 < 0 || l1 < 0) throw new PatchError("잘못된 변환 범위입니다.");
                    if (k1 == "copy" && (o1 > sourceBytes || l1 > sourceBytes - o1)) throw new PatchError("원본 범위를 벗어난 변환입니다.");
                    if (k1 == "data" && (o1 > payloadSize || l1 > payloadSize - o1)) throw new PatchError("패치 데이터 범위를 벗어났습니다.");
                    if (k1 == "zlib" && (op.Length != 5 || N(op[3]) <= 0 || N(op[3]) > 1024 * 1024 || o1 > sourceBytes || N(op[3]) > sourceBytes - o1 || !Regex.IsMatch(op[4] as string ?? "", "^[a-f0-9]{64}$")))
                        throw new PatchError("잘못된 폰트 처리 범위입니다.");
                    if (k1 == "rezlib" && (l1 <= 0 || l1 > 2 * 1024 * 1024 || N(op[3]) < 6 || N(op[3]) > 2 * 1024 * 1024 || o1 > sourceBytes || N(op[3]) > sourceBytes - o1 || !Regex.IsMatch(op[4] as string ?? "", "^[a-f0-9]{64}$")))
                        throw new PatchError("잘못된 재압축 범위입니다.");
                    total += l1;
                    continue;
                }
                if (op == null || (op.Length != 3 && op.Length != 7)) throw new PatchError("잘못된 변환 정보입니다.");
                var kind = op[0] as string;
                long offset = N(op[1]), length = N(op[2]);
                if (offset < 0 || length <= 0) throw new PatchError("잘못된 변환 범위입니다.");
                if (kind == "copy" && op.Length == 3) { if (offset + length > sourceBytes) throw new PatchError("원본 범위를 벗어났습니다."); }
                else if (kind == "data" && op.Length == 3) { if (offset + length > payloadSize) throw new PatchError("패치 범위를 벗어났습니다."); }
                else if (kind == "bsdiff" && op.Length == 7)
                {
                    long oldOff = N(op[3]), oldLen = N(op[4]), patchLen = N(op[5]);
                    if (oldOff < 0 || oldLen < 0 || patchLen < 0 || oldLen > CHUNK || length > CHUNK || patchLen > CHUNK * 2 ||
                        oldOff + oldLen > sourceBytes || offset + patchLen > payloadSize || !Regex.IsMatch(op[6] as string ?? "", "^[a-f0-9]{64}$"))
                        throw new PatchError("잘못된 차분 범위입니다.");
                }
                else throw new PatchError("지원하지 않는 변환 명령입니다.");
                total += length;
            }
            if (total != targetBytes) throw new PatchError("예상 결과 크기가 다릅니다.");
            return new QuestPackage { Dir = dir, M = m, Ops = ops };
        }

        private static long N(object o)
        {
            if (o is int || o is long || o is decimal) { var v = Convert.ToDecimal(o); if (v != Math.Floor(v)) return -1; return (long)v; }
            return -1;
        }

        /// <summary>릴리스 파일에서 패치 데이터를 준비 (이미 검증된 캐시가 있으면 그대로 사용)</summary>
        public static async Task<QuestPackage> PrepareAsync(string repo, string dirKey, string tag, string assetName, string url, string digest, Action<string, double?> say)
        {
            // dirKey: 받은 데이터 보관 폴더 이름 (같은 저장소를 PC 엔진과 함께 쓰는 Quest판은 별도 이름으로 분리)
            var dir = Path.Combine(Root, dirKey, tag);
            if (File.Exists(Path.Combine(dir, "manifest.json")))
            {
                try { return Load(dir); } catch (Exception ex) { Log.Info("package cache invalid: " + ex.Message); }
            }
            if (!url.StartsWith("https://github.com/zecole-vr/" + repo + "/releases/download/", StringComparison.Ordinal))
                throw new PatchError("zecole-vr 릴리스 파일만 사용할 수 있습니다.");

            var dl = Path.Combine(Root, dirKey, "_download");
            Directory.CreateDirectory(dl);
            var file = Path.Combine(dl, assetName);
            digest = digest?.Replace("sha256:", "").ToLowerInvariant();
            if (!(File.Exists(file) && digest != null && Hash.File(file) == digest))
            {
                say("GitHub에서 한글패치 데이터를 받는 중…", 0);
                await Net.DownloadFileAsync(url, file, (d, t) => say($"한글패치 데이터 받는 중 · {d / 1048576}MB{(t > 0 ? " / " + t / 1048576 + "MB" : "")}", t > 0 ? d * 100.0 / t : (double?)null));
                if (digest != null && Hash.File(file) != digest) { File.Delete(file); throw new PatchError("받은 파일의 해시가 GitHub 기록과 다릅니다."); }
            }

            say("패치 데이터를 꺼내는 중…", null);
            var stage = dir + ".staging";
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            Directory.CreateDirectory(Path.Combine(stage, "data"));
            await Task.Run(() =>
            {
                if (assetName.EndsWith(".zpkg", StringComparison.OrdinalIgnoreCase) || assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    ExtractZip(file, stage);
                else
                    PyInstallerArchive.Extract(file, new Dictionary<string, string>
                    {
                        ["bundle/manifest.json"] = Path.Combine(stage, "manifest.json"),
                        ["bundle/data/recipe.json"] = Path.Combine(stage, "data", "recipe.json"),
                        ["bundle/data/payload.bin"] = Path.Combine(stage, "data", "payload.bin"),
                    });
            });
            var pkg = Load(stage); // 검증 실패 시 예외 → 캐시에 남기지 않음
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(stage, dir);
            try { Directory.Delete(dl, true); } catch { }
            // 같은 게임의 이전 버전 데이터 정리
            foreach (var other in Directory.GetDirectories(Path.Combine(Root, dirKey)))
                if (!string.Equals(Path.GetFileName(other), tag, StringComparison.OrdinalIgnoreCase)) try { Directory.Delete(other, true); } catch { }
            pkg.Dir = dir;
            return pkg;
        }

        internal static void ExtractZip(string zip, string stage)
        {
            using (var z = ZipFile.OpenRead(zip))
                foreach (var name in new[] { "manifest.json", "data/recipe.json", "data/payload.bin" })
                {
                    var e = z.GetEntry(name) ?? throw new PatchError("패치 묶음에 " + name + " 이(가) 없습니다.");
                    e.ExtractToFile(Path.Combine(stage, name.Replace('/', Path.DirectorySeparatorChar)), true);
                }
        }
    }

    /// <summary>PyInstaller 단일 EXE 의 내장 파일 목록(CArchive)에서 지정한 파일만 꺼냄</summary>
    internal static class PyInstallerArchive
    {
        private static readonly byte[] Magic = { (byte)'M', (byte)'E', (byte)'I', 0x0C, 0x0B, 0x0A, 0x0B, 0x0E };

        public static void Extract(string exe, Dictionary<string, string> wanted)
        {
            var found = ExtractWhere(exe, n => wanted.TryGetValue(n, out var t) ? t : null);
            var missing = wanted.Keys.Where(k => !found.Contains(k)).ToList();
            if (missing.Count > 0) throw new PatchError("설치기 안에 패치 데이터가 없습니다: " + string.Join(", ", missing));
        }

        /// <summary>내장 파일 이름마다 저장 경로를 정해 꺼냄 (null 이면 건너뜀). 꺼낸 이름 목록을 돌려줌</summary>
        public static HashSet<string> ExtractWhere(string exe, Func<string, string> targetFor)
        {
            using (var f = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // 쿠키는 파일 끝 근처: magic(8) pkglen(4) tocpos(4) toclen(4) pyver(4) pylib(64) — 빅엔디언
                var tailLen = (int)Math.Min(f.Length, 4096);
                var tail = new byte[tailLen];
                f.Seek(-tailLen, SeekOrigin.End); Read(f, tail, tailLen);
                int at = -1;
                for (int i = tailLen - Magic.Length; i >= 0; i--) if (Match(tail, i)) { at = i; break; }
                if (at < 0 || at + 24 > tailLen) throw new PatchError("설치기 파일에서 패치 데이터를 찾지 못했습니다.");
                long cookiePos = f.Length - tailLen + at;
                long pkgLen = BE(tail, at + 8), tocPos = BE(tail, at + 12), tocLen = BE(tail, at + 16);
                long start = cookiePos + 88 - pkgLen;
                if (start < 0 || tocLen <= 0 || tocLen > 16 * 1024 * 1024) throw new PatchError("설치기 파일 구조를 해석하지 못했습니다.");

                var toc = new byte[tocLen];
                f.Seek(start + tocPos, SeekOrigin.Begin); Read(f, toc, (int)tocLen);
                var found = new HashSet<string>();
                for (int i = 0; i < tocLen;)
                {
                    int size = (int)BE(toc, i);
                    long pos = BE(toc, i + 4), clen = BE(toc, i + 8);
                    byte flag = toc[i + 16];
                    var name = Encoding.UTF8.GetString(toc, i + 18, size - 18).TrimEnd('\0').Replace('\\', '/');
                    if (size <= 18) throw new PatchError("설치기 파일 목록이 손상되었습니다.");
                    i += size;
                    var target = targetFor(name);
                    if (target == null) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    f.Seek(start + pos, SeekOrigin.Begin);
                    using (var src = new SubStream(f, clen))
                    using (var dst = File.Create(target))
                    {
                        if (flag == 1)
                        {
                            src.ReadByte(); src.ReadByte(); // zlib 헤더 2바이트 → 이후 raw deflate
                            using (var inflate = new DeflateStream(src, CompressionMode.Decompress, true)) inflate.CopyTo(dst, 1 << 20);
                        }
                        else src.CopyTo(dst, 1 << 20);
                    }
                    found.Add(name);
                }
                return found;
            }
        }

        private static bool Match(byte[] b, int i) { for (int k = 0; k < Magic.Length; k++) if (b[i + k] != Magic[k]) return false; return true; }
        private static long BE(byte[] b, int p) => ((long)b[p] << 24) | ((long)b[p + 1] << 16) | ((long)b[p + 2] << 8) | b[p + 3];
        private static void Read(Stream s, byte[] buf, int count) { int off = 0; while (off < count) { int n = s.Read(buf, off, count - off); if (n <= 0) throw new EndOfStreamException(); off += n; } }

        /// <summary>원본 스트림의 일부 구간만 읽게 하는 래퍼</summary>
        private sealed class SubStream : Stream
        {
            private readonly Stream _s; private long _left;
            public SubStream(Stream s, long length) { _s = s; _left = length; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_left <= 0) return 0;
                int n = _s.Read(buffer, offset, (int)Math.Min(count, _left));
                _left -= n; return n;
            }
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long o, SeekOrigin r) => throw new NotSupportedException();
            public override void SetLength(long v) => throw new NotSupportedException();
            public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        }
    }

    internal static class Hash
    {
        public static string File(string path, Action<long> progress = null)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                var buf = new byte[8 << 20]; int n; long done = 0, last = 0;
                while ((n = f.Read(buf, 0, buf.Length)) > 0)
                {
                    sha.TransformBlock(buf, 0, n, null, 0);
                    done += n;
                    if (progress != null && done - last > 256L << 20) { last = done; progress(done); }
                }
                sha.TransformFinalBlock(buf, 0, 0);
                return string.Concat(sha.Hash.Select(b => b.ToString("x2")));
            }
        }

        /// <summary>진행 알림 없이 파일 해시 (Func&lt;string,string&gt; 로 넘기기용)</summary>
        public static string File2(string path) => File(path);

        public static string Bytes(byte[] b)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create()) return string.Concat(sha.ComputeHash(b).Select(x => x.ToString("x2")));
        }
    }
}
