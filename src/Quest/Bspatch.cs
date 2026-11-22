using System;
using System.IO;
using ICSharpCode.SharpZipLib.BZip2;

namespace Zecole.Launcher.Quest
{
    /// <summary>
    /// BSDIFF40 차분 적용 (Python bsdiff4.patch 와 같은 형식).
    /// 헤더: "BSDIFF40" | 제어블록 압축길이 | 차분블록 압축길이 | 결과 크기, 이후 bzip2 블록 3개.
    /// </summary>
    internal static class Bspatch
    {
        public static byte[] Apply(byte[] old, byte[] patch)
        {
            if (patch.Length < 32 || patch[0] != 'B' || patch[1] != 'S' || patch[2] != 'D' || patch[3] != 'I' ||
                patch[4] != 'F' || patch[5] != 'F' || patch[6] != '4' || patch[7] != '0')
                throw new PatchError("차분 데이터 형식이 다릅니다.");
            long ctrlLen = OffIn(patch, 8), diffLen = OffIn(patch, 16), newSize = OffIn(patch, 24);
            if (ctrlLen < 0 || diffLen < 0 || newSize < 0 || newSize > int.MaxValue || 32 + ctrlLen + diffLen > patch.Length)
                throw new PatchError("차분 데이터가 손상되었습니다.");

            var output = new byte[newSize];
            using (var ctrl = Bz(patch, 32, ctrlLen))
            using (var diff = Bz(patch, 32 + ctrlLen, diffLen))
            using (var extra = Bz(patch, 32 + ctrlLen + diffLen, patch.Length - 32 - ctrlLen - diffLen))
            {
                var t = new byte[24];
                long newPos = 0, oldPos = 0;
                while (newPos < newSize)
                {
                    ReadExact(ctrl, t, 0, 24);
                    long x = OffIn(t, 0), y = OffIn(t, 8), z = OffIn(t, 16);
                    if (x < 0 || y < 0 || newPos + x > newSize) throw new PatchError("차분 제어값이 범위를 벗어났습니다.");

                    ReadExact(diff, output, (int)newPos, (int)x);
                    for (long i = 0; i < x; i++)
                    {
                        var o = oldPos + i;
                        if (o >= 0 && o < old.Length) output[newPos + i] += old[o];
                    }
                    newPos += x; oldPos += x;

                    if (newPos + y > newSize) throw new PatchError("차분 추가 데이터가 범위를 벗어났습니다.");
                    ReadExact(extra, output, (int)newPos, (int)y);
                    newPos += y; oldPos += z;
                }
            }
            return output;
        }

        private static Stream Bz(byte[] buf, long off, long len) =>
            new BZip2InputStream(new MemoryStream(buf, (int)off, (int)len, false)) { IsStreamOwner = true };

        private static void ReadExact(Stream s, byte[] buf, int off, int count)
        {
            while (count > 0)
            {
                int n = s.Read(buf, off, count);
                if (n <= 0) throw new PatchError("차분 데이터가 중간에 끝났습니다.");
                off += n; count -= n;
            }
        }

        /// <summary>bsdiff 의 부호-크기 64비트 정수 (리틀엔디언, 최상위 비트가 부호)</summary>
        private static long OffIn(byte[] b, int p)
        {
            long y = b[p + 7] & 0x7F;
            for (int i = 6; i >= 0; i--) y = y * 256 + b[p + i];
            return (b[p + 7] & 0x80) != 0 ? -y : y;
        }
    }

    internal sealed class PatchError : Exception
    {
        public PatchError(string message) : base(message) { }
    }
}
