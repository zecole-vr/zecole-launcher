"""AFT Quest 변환표 묶음의 manifest.json 작성 + .zpkg(ZIP) 만들기"""
import sys, os, json, zipfile, hashlib
sys.stdout.reconfigure(encoding='utf-8')
out, zpkg = sys.argv[1], sys.argv[2]
info = json.load(open(os.path.join(out, 'recipe-info.json')))
m = {
    'format': 1,
    'game': "A Fisherman's Tale",
    'name': "A Fisherman's Tale Quest Korean",
    'package': 'com.innerspacevr.afishermanstale',
    'main_name': 'main.25.com.innerspacevr.afishermanstale.obb',
    'source_sha256': '65413a256ef94f7a6b923f90e318199c39df578bd2c4612e33e64b3837e9570b',
    'target_sha256': '744596ceefda6dcd1eb3b46dd1b3742f3721eddceb41a061acb16c621442c7f3',
    'main_bytes': info['main_bytes'],
    'version_name': '1.087',
    'version_code': 25,
    # 런처는 릴리스 태그와 이 값을 비교하므로 태그 버전과 같게 (번역 데이터 버전은 translation_version)
    'release': '1.1.6',
    'patch_version': '1.1.6',
    'translation_version': '1.0.0',
    'installer_version': '1.1.6',
    'tool_build': '20261120.1',
    'platform': 'quest-standalone',
    'installer_host': 'zecole-launcher',
    'backup_suffix': '.zecole-original',
    # 이전 통합 설치기(1.1.x)의 PC 원본 위치: 해시가 맞으면 옮겨서 다시 씀
    'legacy_pc_originals': ['devices/{serial}/main.25.original.obb'],
    # 이전 설치기(1.0.x~1.1.x 시험본)가 만든 한글판 → '업데이트 필요'로 표시
    'compatible_previous': {
        'c7779043c9ee7140c43a9d98234bf185328c7d2e9e7a147d45fb61a40875dc06': '이전 한글판',
        '69fcf08bf585ae07b0e2cd6e2669dca22949a8317000e062fdb347d87094e48b': '이전 한글판',
        '4b5ce858d277a25359ff510506a1a0888eea7567b4929098f7a5976a0f0ce7db': '이전 한글판',
    },
    'recipe_sha256': info['recipe_sha256'],
    'payload_sha256': info['payload_sha256'],
    'payload_bytes': info['payload_bytes'],
    'recipe_ops': info['recipe_ops'],
    'runtime_baseline': 'v1.1.5 통합 설치기 Quest 결과물(744596ce)과 바이트 단위 동일. 원본 PAK 조각을 풀어 zlib 1.2.13 레벨 9로 다시 압축(rezlib).',
}
json.dump(m, open(os.path.join(out, 'manifest.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
# ZIP 맨 위에 세 파일만, 날짜 고정(같은 입력이면 같은 해시)
with zipfile.ZipFile(zpkg, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for name in ('manifest.json', 'data/recipe.json', 'data/payload.bin'):
        zi = zipfile.ZipInfo(name, (2026, 11, 20, 0, 0, 0)); zi.compress_type = zipfile.ZIP_DEFLATED
        z.writestr(zi, open(os.path.join(out, name), 'rb').read())
h = hashlib.sha256(open(zpkg, 'rb').read()).hexdigest()
print('zpkg 완성:', os.path.basename(zpkg), '%.1f MB' % (os.path.getsize(zpkg) / 1e6), 'SHA-256', h)
