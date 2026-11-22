"""A Fisherman's Tale Quest: 원본 OBB → 한글판 OBB 변환표(format 1) 생성기

사용: python -I aft_recipe.py <원본 obb> <한글판 obb> <출력 폴더>
출력: <출력>/manifest.json, data/recipe.json, data/payload.bin

명령 종류 (런처 quest-recipe-v1):
  ["copy", 원본위치, 길이]                 원본 바이트 그대로
  ["data", 패치위치, 길이]                 payload.bin 바이트
  ["zero", 0, 길이]                         0으로 채움
  ["zlib", 원본위치, 결과길이, 원본길이, sha256]    원본 비압축 구간을 zlib 레벨 9로 압축
  ["rezlib", 원본위치, 결과길이, 원본길이, sha256]  원본 zlib 조각을 풀어 레벨 9로 다시 압축 (새 명령)
"""
import sys, os, mmap, zipfile, struct, zlib, hashlib, json, time
sys.stdout.reconfigure(encoding='utf-8')
PAK = 'Fisherman/Content/Paks/Fisherman-Android_ASTC.pak'
PADDING = 'Fisherman/Content/zecole-padding.bin'
SRC_SHA = '65413a256ef94f7a6b923f90e318199c39df578bd2c4612e33e64b3837e9570b'
DST_SHA = '744596ceefda6dcd1eb3b46dd1b3742f3721eddceb41a061acb16c621442c7f3'
assert zlib.ZLIB_RUNTIME_VERSION == '1.2.13', '이 생성기는 zlib 1.2.13(런처 DLL과 같은 판)에서만 실행해야 합니다: ' + zlib.ZLIB_RUNTIME_VERSION

src_path, dst_path, out = sys.argv[1:4]

def sha256_file(p):
    h = hashlib.sha256()
    with open(p, 'rb') as f:
        for b in iter(lambda: f.read(8 << 20), b''): h.update(b)
    return h.hexdigest()

print('입력 파일 해시 확인 중…', flush=True)
assert sha256_file(src_path) == SRC_SHA, '원본 OBB 해시가 다릅니다'
assert sha256_file(dst_path) == DST_SHA, '한글판 OBB 해시가 다릅니다'

fs, fd = open(src_path, 'rb'), open(dst_path, 'rb')
S = mmap.mmap(fs.fileno(), 0, access=mmap.ACCESS_READ)
D = mmap.mmap(fd.fileno(), 0, access=mmap.ACCESS_READ)

def members(path, M):
    res = {}
    with zipfile.ZipFile(path) as z:
        for i in z.infolist():
            n, e = struct.unpack('<HH', M[i.header_offset + 26:i.header_offset + 30])
            start = i.header_offset + 30 + n + e
            assert i.compress_type == 0, '압축된 zip 멤버는 지원하지 않습니다: ' + i.filename
            res[i.filename] = (start, i.compress_size)
    return res

ms, md = members(src_path, S), members(dst_path, D)

# ── PAK 레코드 읽기 (데이터 영역 안의 FPakEntry 헤더) ──
def footer(M, base, size):
    tail = M[base + size - 300:base + size]
    k = tail.find(struct.pack('<I', 0x5A6F12E1))
    magic, ver, ioff, isz = struct.unpack_from('<IIQQ', tail, k)
    return ver, ioff, isz

def record(M, base, pos):
    """pos 위치 레코드 헤더 → (헤더길이, 압축크기, 원본크기, 압축여부, [(블록시작, 블록끝)] 레코드 기준)"""
    off, size, raw, comp = struct.unpack_from('<QQQI', M, base + pos)
    p = 28 + 20
    blocks = []
    if comp:
        nb = struct.unpack_from('<I', M, base + pos + p)[0]; p += 4
        for j in range(nb):
            blocks.append(struct.unpack_from('<QQ', M, base + pos + p)); p += 16
    flags = M[base + pos + p]; p += 1
    bsize = struct.unpack_from('<I', M, base + pos + p)[0]; p += 4
    assert flags == 0, '암호화된 PAK 레코드'
    return p, size, raw, comp, blocks, bsize

def walk(M, base, size):
    ver, ioff, isz = footer(M, base, size)
    recs, pos = [], 0
    while pos < ioff:
        h, csz, raw, comp, blocks, bsize = record(M, base, pos)
        if comp:
            assert blocks[0][0] == h and blocks[-1][1] == h + csz, '블록 위치가 예상과 다릅니다 (pos %d)' % pos
        recs.append((pos, h, csz, raw, comp, blocks, bsize))
        pos += h + csz
    assert pos == ioff, 'PAK 레코드 끝이 목록 위치와 다릅니다: %d != %d' % (pos, ioff)
    return ver, ioff, isz, recs

def raw_parts(M, base, rec):
    """레코드의 원본(비압축) 내용을 블록 단위로 돌려줌: [(원본 내 절대위치, 길이, 압축여부, 비압축바이트)]"""
    pos, h, csz, raw, comp, blocks, bsize = rec
    if not comp:
        a = base + pos + h
        return [(a, csz, False, None)]
    parts = []
    for b0, b1 in blocks:
        a = base + pos + b0
        parts.append((a, b1 - b0, True, zlib.decompress(M[a:a + (b1 - b0)])))
    return parts

sb, ssz = ms[PAK]; db, dsz = md[PAK]
t = time.time()
sver, sioff, _, srecs = walk(S, sb, ssz)
dver, dioff, _, drecs = walk(D, db, dsz)
print('PAK 레코드: 원본 v%d %d개 · 한글판 v%d %d개 (%.0f초)' % (sver, len(srecs), dver, len(drecs), time.time() - t), flush=True)

# 원본 레코드를 비압축 내용 해시로 색인
t = time.time()
index = {}
for rec in srecs:
    parts = raw_parts(S, sb, rec)
    h = hashlib.sha1()
    for a, n, c, data in parts: h.update(data if c else S[a:a + n])
    index.setdefault((rec[3], h.hexdigest()), rec)
print('원본 내용 색인 완료 (%.0f초)' % (time.time() - t), flush=True)

ops = []           # (대상 위치, 길이, 명령)
payload = bytearray()
stats = dict(copy=0, data=0, zero=0, zlib=0, rezlib=0)

def add_data(tpos, n):
    ops.append((tpos, n, ['data', len(payload), n])); payload.extend(D[tpos:tpos + n]); stats['data'] += n

# 1) PAK 이외 zip 멤버 데이터: 같은 이름·같은 내용이면 원본 복사, 패딩은 0
for name, (start, n) in md.items():
    if name == PAK: continue
    if name == PADDING:
        assert D[start:start + n] == bytes(n), '패딩이 0이 아닙니다'
        ops.append((start, n, ['zero', 0, n])); stats['zero'] += n; continue
    s = ms.get(name)
    if s and s[1] == n and S[s[0]:s[0] + n] == D[start:start + n]:
        ops.append((start, n, ['copy', s[0], n])); stats['copy'] += n
    else:
        add_data(start, n)

# 2) PAK 레코드
t = time.time(); matched = 0
for k, rec in enumerate(drecs):
    pos, h, csz, raw, comp, blocks, bsize = rec
    parts = raw_parts(D, db, rec)
    hh = hashlib.sha1()
    for a, n, c, data in parts: hh.update(data if c else D[a:a + n])
    src = index.get((raw, hh.hexdigest()))
    add_data(db + pos, h)  # 레코드 헤더
    if not src or not comp:
        if csz: add_data(db + pos + h, csz)
        continue
    matched += 1
    # 원본 블록을 같은 크기(64KB) 단위의 비압축 구간으로 정리
    sp = raw_parts(S, sb, src)
    if src[4]:
        sraw = [(a, n, data) for a, n, c, data in sp]
    else:
        a0 = sp[0][0]; sraw = []
    off = 0
    for j, (b0, b1) in enumerate(blocks):
        tpos = db + pos + b0; tl = b1 - b0
        rawblk = parts[j][3]
        done = False
        if src[4] and j < len(sraw) and len(sraw[j][2]) == len(rawblk):
            a, n, data = sraw[j]
            if data == rawblk and zlib.compress(data, 9) == D[tpos:tpos + tl]:
                ops.append((tpos, tl, ['rezlib', a, tl, n, hashlib.sha256(D[tpos:tpos + tl]).hexdigest()])); stats['rezlib'] += tl; done = True
        elif not src[4]:
            a = a0 + off
            if S[a:a + len(rawblk)] == rawblk and zlib.compress(rawblk, 9) == D[tpos:tpos + tl]:
                ops.append((tpos, tl, ['zlib', a, tl, len(rawblk), hashlib.sha256(D[tpos:tpos + tl]).hexdigest()])); stats['zlib'] += tl; done = True
        if not done: add_data(tpos, tl)
        off += len(rawblk)
    if k % 2000 == 0: print('  PAK 처리 %d / %d (%.0f초)' % (k, len(drecs), time.time() - t), flush=True)
print('원본과 내용이 같은 레코드 %d / %d' % (matched, len(drecs)), flush=True)

# 3) PAK 목록·푸터
add_data(db + dioff, dsz - dioff)

# 4) 나머지 틈(zip 로컬 헤더, 중앙 디렉터리 등)은 데이터로 채움
ops.sort(key=lambda o: o[0])
full, cur = [], 0
for tpos, n, op in ops:
    assert tpos >= cur, '겹치는 구간 %d < %d' % (tpos, cur)
    if tpos > cur:
        gap = (cur, tpos - cur, ['data', len(payload), tpos - cur]); payload.extend(D[cur:tpos]); stats['data'] += tpos - cur
        full.append(gap)
    full.append((tpos, n, op)); cur = tpos + n
if cur < len(D):
    full.append((cur, len(D) - cur, ['data', len(payload), len(D) - cur])); payload.extend(D[cur:]); stats['data'] += len(D) - cur

# 5) 이어지는 copy·data 명령 합치기
merged = []
for _, n, op in full:
    if merged and op[0] in ('copy', 'data') and merged[-1][0] == op[0] and merged[-1][1] + merged[-1][2] == op[1]:
        merged[-1][2] += op[2]
    elif merged and op[0] == 'zero' and merged[-1][0] == 'zero':
        merged[-1][2] += op[2]
    else:
        merged.append(list(op))
assert sum(o[2] for o in merged) == len(D)

# 6) 자체 검증: 변환표만으로 결과를 다시 만들어 해시 비교
print('변환표로 결과 재현 검사 중…', flush=True)
h = hashlib.sha256(); P = bytes(payload)
for op in merged:
    k = op[0]
    if k == 'copy': h.update(S[op[1]:op[1] + op[2]])
    elif k == 'data': h.update(P[op[1]:op[1] + op[2]])
    elif k == 'zero':
        left = op[2]
        while left: n = min(left, 1 << 20); h.update(bytes(n)); left -= n
    elif k == 'zlib':
        b = zlib.compress(S[op[1]:op[1] + op[3]], 9); assert len(b) == op[2]; h.update(b)
    elif k == 'rezlib':
        b = zlib.compress(zlib.decompress(S[op[1]:op[1] + op[3]]), 9); assert len(b) == op[2]; h.update(b)
assert h.hexdigest() == DST_SHA, '재현 결과 해시가 다릅니다'
print('재현 결과 해시 일치: %s' % DST_SHA[:16])

os.makedirs(os.path.join(out, 'data'), exist_ok=True)
recipe = json.dumps({'format': 1, 'operations': merged}, separators=(',', ':')).encode()
open(os.path.join(out, 'data', 'recipe.json'), 'wb').write(recipe)
open(os.path.join(out, 'data', 'payload.bin'), 'wb').write(P)
mb = lambda n: '%.1f MB' % (n / 1e6)
print('명령 %d개 · 복사 %s · 다시압축 %s · 비압축압축 %s · 0채움 %s · 패치데이터 %s · 변환표 %s' % (
    len(merged), mb(stats['copy']), mb(stats['rezlib']), mb(stats['zlib']), mb(stats['zero']), mb(len(P)), mb(len(recipe))))
json.dump({'recipe_sha256': hashlib.sha256(recipe).hexdigest(), 'payload_sha256': hashlib.sha256(P).hexdigest(),
           'payload_bytes': len(P), 'recipe_ops': len(merged), 'main_bytes': len(D)},
          open(os.path.join(out, 'recipe-info.json'), 'w'), indent=1)
