using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Zecole.Launcher.Quest
{
    /// <summary>
    /// zlib 1.2.13 레벨 9 압축 (ITR2 폰트 재생성용).
    /// 원래 설치기(Python 3.10 내장 zlib 1.2.13)의 zlib.compress(data, 9) 와 바이트 단위로 같아야 하므로
    /// 공식 zlib 1.2.13 소스를 그대로 빌드한 zecole_zlib.dll(native/zlib)을 EXE 에 넣어 사용.
    /// </summary>
    internal static class Zlib
    {
        private const string Dll = "zecole_zlib.dll";
        private static readonly object Gate = new object();
        private static bool _loaded;
        private static byte[] _work;

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string path);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int zecole_compress9(byte[] src, uint srcLen, byte[] dst, ref uint dstLen, byte[] work, uint workLen);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr zlibVersion();

        private static void Load()
        {
            if (_loaded) return;
            var dir = Path.Combine(Program.DataDir, "runtime", Program.Version);
            var path = Path.Combine(dir, Dll);
            byte[] want;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("lib/" + Dll))
            using (var m = new MemoryStream()) { s.CopyTo(m); want = m.ToArray(); }
            // 같은 버전 폴더라도 내용이 다르면(손상·교체) 다시 풀어 씀
            if (!File.Exists(path) || Hash.Bytes(File.ReadAllBytes(path)) != Hash.Bytes(want))
            {
                Directory.CreateDirectory(dir);
                var tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                File.WriteAllBytes(tmp, want);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            if (LoadLibraryW(path) == IntPtr.Zero) throw new PatchError("압축 모듈을 불러오지 못했습니다. (" + Marshal.GetLastWin32Error() + ")");
            if (Marshal.PtrToStringAnsi(zlibVersion()) != "1.2.13") throw new PatchError("압축 모듈 버전이 다릅니다.");
            _work = new byte[1 << 20];   // deflate 레벨 9 작업 메모리(약 270KB)보다 넉넉하게
            _loaded = true;
        }

        public static string Version { get { lock (Gate) { Load(); return Marshal.PtrToStringAnsi(zlibVersion()); } } }

        public static byte[] Compress9(byte[] raw)
        {
            lock (Gate)
            {
                Load();
                var dst = new byte[raw.Length + raw.Length / 1000 + 64];
                uint n = (uint)dst.Length;
                int r = zecole_compress9(raw, (uint)raw.Length, dst, ref n, _work, (uint)_work.Length);
                if (r != 0) throw new PatchError("압축 처리 오류 (" + r + ")");
                Array.Resize(ref dst, (int)n);
                return dst;
            }
        }

        /// <summary>zlib 스트림(머리말 2바이트 + deflate + Adler-32) 풀기. 머리말·검사값이 맞지 않거나 maxOut 을 넘으면 오류</summary>
        public static byte[] Inflate(byte[] z, int maxOut)
        {
            if (z.Length < 6 || (z[0] & 0x0F) != 8 || (z[0] >> 4) > 7 || ((z[0] << 8) | z[1]) % 31 != 0 || (z[1] & 0x20) != 0)
                throw new PatchError("원본 압축 데이터 형식이 다릅니다.");
            byte[] raw;
            using (var ms = new MemoryStream(z, 2, z.Length - 6))
            using (var ds = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress))
            using (var o = new MemoryStream())
            {
                var buf = new byte[65536]; int n;
                while ((n = ds.Read(buf, 0, buf.Length)) > 0)
                {
                    o.Write(buf, 0, n);
                    if (o.Length > maxOut) throw new PatchError("원본 압축 데이터가 예상보다 큽니다.");
                }
                raw = o.ToArray();
            }
            uint a = 1, b = 0;
            foreach (var x in raw) { a = (a + x) % 65521; b = (b + a) % 65521; }
            uint want = (uint)(z[z.Length - 4] << 24 | z[z.Length - 3] << 16 | z[z.Length - 2] << 8 | z[z.Length - 1]);
            if (((b << 16) | a) != want) throw new PatchError("원본 압축 데이터 검사값이 다릅니다.");
            return raw;
        }
    }
}
