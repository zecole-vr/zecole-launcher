# A Fisherman's Tale Quest 변환표(.zpkg) 만들기

1. `python -I aft_recipe.py <원본 OBB> <한글판 OBB> <출력 폴더>`
   - 원본 SHA-256 65413a25…, 한글판(v1.1.5 통합 설치기 결과) 744596ce… 만 받음
   - Python 내장 zlib 1.2.13 필요 (런처 DLL과 같은 판)
   - 생성 직후 변환표만으로 결과를 다시 만들어 해시를 확인함
2. `python -I aft_manifest.py <출력 폴더> AFT_Quest_v<태그버전>.zpkg`
   - manifest 의 release / patch_version 은 GitHub 릴리스 태그 버전과 같게 (런처가 태그와 비교)

새 명령 `rezlib`: 원본 PAK의 zlib 조각을 풀어 레벨 9로 다시 압축 → 원본 게임 데이터는 배포하지 않음.
