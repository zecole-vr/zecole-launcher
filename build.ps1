# ZECOLE 통합 런처 빌드
#   .\build.ps1            → dist\ZECOLE_Launcher.exe + 게임별 이름 붙인 복사본
# 게임별 복사본은 같은 EXE 이고 파일 이름만 다릅니다. 이름에 들어 있는 게임 화면으로 바로 열립니다.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
Push-Location "$root\src"
try { dotnet build -c Release --nologo -v q; if ($LASTEXITCODE -ne 0) { throw "빌드 실패" } } finally { Pop-Location }

$dist = Join-Path $root "dist"
Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$dist\games" | Out-Null
$exe = "$root\src\bin\Release\net48\ZECOLE_Launcher.exe"
Copy-Item $exe "$dist\ZECOLE_Launcher.exe"

# 게임별 배포용 이름: ZECOLE_<게임>_한글패치.exe
$catalog = Get-Content "$root\ui\catalog.json" -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($p in $catalog.items.PSObject.Properties) {
    $item = $p.Value
    if ($item.kind -ne "patch") { continue }
    $label = ($item.name -replace '[^A-Za-z0-9]', '')
    Copy-Item $exe ("$dist\games\ZECOLE_{0}_한글패치.exe" -f $label)
}

$hash = (Get-FileHash "$dist\ZECOLE_Launcher.exe" -Algorithm SHA256).Hash.ToLower()
"{0}  ZECOLE_Launcher.exe" -f $hash | Out-File "$dist\SHA256SUMS.txt" -Encoding ascii
Get-ChildItem $dist -Recurse -File | Select-Object @{n = "파일"; e = { $_.FullName.Substring($dist.Length + 1) } }, @{n = "크기(KB)"; e = { [int]($_.Length / 1KB) } } | Format-Table -AutoSize
