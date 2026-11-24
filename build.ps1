# ZECOLE 통합 런처 빌드
#   .\build.ps1            → dist\ZECOLE_Launcher.exe
# 게임별 이름 EXE(ZECOLE_<게임>_한글패치.exe)는 더 만들지 않습니다. 런처는 어떤 이름으로 열어도 홈에서 시작하고,
# 이름이나 --game 으로 찾은 게임은 홈 상단 안내 카드와 왼쪽 목록 강조로만 알려 줍니다.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
Push-Location "$root\src"
try { dotnet build -c Release --nologo -v q; if ($LASTEXITCODE -ne 0) { throw "빌드 실패" } } finally { Pop-Location }

$dist = Join-Path $root "dist"
Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $dist | Out-Null
$exe = "$root\src\bin\Release\net48\ZECOLE_Launcher.exe"
Copy-Item $exe "$dist\ZECOLE_Launcher.exe"

$hash = (Get-FileHash "$dist\ZECOLE_Launcher.exe" -Algorithm SHA256).Hash.ToLower()
"{0}  ZECOLE_Launcher.exe" -f $hash | Out-File "$dist\SHA256SUMS.txt" -Encoding ascii
Get-ChildItem $dist -Recurse -File | Select-Object @{n = "파일"; e = { $_.FullName.Substring($dist.Length + 1) } }, @{n = "크기(KB)"; e = { [int]($_.Length / 1KB) } } | Format-Table -AutoSize
