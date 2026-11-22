/* memset/memcpy for the CRT-free build (the compiler may turn byte loops into these calls). */
typedef unsigned long long sz_t;
#pragma function(memset, memcpy)
void *memset(void *d, int c, sz_t n) { volatile unsigned char *p = (volatile unsigned char *)d; while (n--) *p++ = (unsigned char)c; return d; }
void *memcpy(void *d, const void *s, sz_t n) { volatile unsigned char *p = (volatile unsigned char *)d; const unsigned char *q = (const unsigned char *)s; while (n--) *p++ = *q++; return d; }
