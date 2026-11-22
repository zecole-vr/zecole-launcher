@echo off
rem zecole_zlib.dll 다시 빌드하기
rem   build.cmd <zlib-1.2.13 소스 폴더>
rem   소스: https://zlib.net/fossils/zlib-1.2.13.tar.gz
rem         SHA-256 b3a24de97a8fdbc835b9833169501030b8977031bcb54b3b3ac13740f846ab30
rem Windows SDK 없이 MSVC 컴파일러만으로 빌드합니다 (Z_SOLO, CRT 미사용, 외부 DLL 의존 없음).
rem 결과는 Python 3.10 내장 zlib 1.2.13 의 zlib.compress(data, 9) 와 바이트 단위로 같아야 합니다.
if "%~1"=="" (echo usage: build.cmd ^<zlib-1.2.13 dir^> & exit /b 1)
set PATH=C:\Program Files (x86)\Microsoft Visual Studio\Installer;C:\Windows\System32;%PATH%
for /f "usebackq delims=" %%i in (`vswhere -latest -products * -property installationPath`) do set VS=%%i
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
cd /d %~dp0
set Z=%~f1
cl /nologo /c /O2 /GS- /Zl /DZ_SOLO /DNDEBUG /I"%Z%" "%Z%\adler32.c" "%Z%\crc32.c" "%Z%\deflate.c" "%Z%\trees.c" "%Z%\zutil.c" zecole_zlib.c || exit /b 1
cl /nologo /c /O1 /Oi- /GS- /Zl nocrt.c || exit /b 1
link /nologo /DLL /NOENTRY /NODEFAULTLIB /DEF:zecole_zlib.def /DYNAMICBASE /NXCOMPAT /OUT:zecole_zlib.dll adler32.obj crc32.obj deflate.obj trees.obj zutil.obj zecole_zlib.obj nocrt.obj || exit /b 1
del *.obj *.lib *.exp 2>nul
