/* ZECOLE Launcher: official zlib 1.2.13 deflate, same settings as Python zlib.compress(data, 9).
   Z_SOLO build: memory comes from the caller-supplied work buffer. */
#include "zlib.h"
typedef struct { unsigned char *base; unsigned long size, used; } arena;
static voidpf za(voidpf o, uInt n, uInt sz) {
    arena *a = (arena *)o; unsigned long need = ((unsigned long)n * sz + 15UL) & ~15UL;
    voidpf p; if (a->used + need > a->size) return Z_NULL;
    p = a->base + a->used; a->used += need; return p;
}
static void zf(voidpf o, voidpf p) { (void)o; (void)p; }
int zecole_compress9(const unsigned char *src, unsigned long srcLen, unsigned char *dst, unsigned long *dstLen,
                     unsigned char *work, unsigned long workLen) {
    arena a; z_stream s; int r; unsigned long i;
    for (i = 0; i < workLen; i++) ((volatile unsigned char *)work)[i] = 0;
    a.base = work; a.size = workLen; a.used = 0;
    s.zalloc = za; s.zfree = zf; s.opaque = &a;
    s.next_in = (z_const Bytef *)src; s.avail_in = (uInt)srcLen;
    s.next_out = dst; s.avail_out = (uInt)*dstLen; s.total_in = s.total_out = 0;
    r = deflateInit(&s, 9);
    if (r != Z_OK) return r;
    r = deflate(&s, Z_FINISH);
    if (r != Z_STREAM_END) { deflateEnd(&s); return r == Z_OK ? Z_BUF_ERROR : r; }
    *dstLen = s.total_out;
    return deflateEnd(&s);
}
