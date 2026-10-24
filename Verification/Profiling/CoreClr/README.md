# Unity 6.7 CoreCLR 클래스별 할당·GC 회수 검증

2026-10-24에 설치된 **Unity 6000.7.0b5 (5747dc44953f)**로 확인했다.
대상은 **Windows x64 CoreCLR Development Player / D3D12**다.
게임 C# 코드에 카운터나 계측 코드를 추가하지 않고 클래스별 객체 할당과 GC 회수 개수를 수집했다.

## 실제 측정 결과

기존 패키지 검증 앱을 실행했다. 최종 측정의 프로세스 ID는 `46280`이다.
배열과 제네릭 구체 타입을 포함해 **1,163개 타입**을 기록했다.
GC는 **57회** 발생했다. Gen0 수집은 52회, 전체 세대 수집은 5회였다.

| 타입 | 생성 객체 | GC 회수 객체 | 회수 기록이 없는 객체 |
|---|---:|---:|---:|
| `GpuTextureAtlas` | 35 | 28 | 7 |
| `BlockAtlasLayout` | 43 | 37 | 6 |
| `CompressedAtlasEntry` | 47 | 36 | 11 |
| `GpuAtlasRenderer` | 4 | 0 | 4 |
| `UnityEngine.Texture2D` | 77 | 56 | 21 |
| `System.String` | 4,821,533 | 4,744,401 | 77,132 |

전체 기록은 생성 **8,225,267개**, GC 회수 **8,078,115개**다.
검증 앱의 픽셀 비교 문자열, Addressables, Unity와 .NET 런타임의 할당을 함께 포함한다.
패키지의 프레임당 할당량을 평가할 때는 별도의 실제 사용 장면이 필요하다.

전체 목록: [class-counts.csv](Results/class-counts.csv)

집계: [summary.json](Results/summary.json) · 원본: [unity-profile.json](Results/unity-profile.json)

## 숫자의 의미

- 생성은 관측 구간의 관리 힙 객체 할당 개수다. 생성자 호출 횟수와는 범위가 다르다. 배열과 박싱도 할당에 포함된다.
- GC 회수는 수집 대상 세대에서 살아남지 않은 추적 객체의 개수다. GC 실행 횟수 57회와 구분한다.
- 마지막 열은 `생성 - GC 회수`다. 참조가 유지된 객체, 마지막 GC 이후 생성된 객체, GC 세대를 확인하지 못한 객체를 포함한다. 현재 도달 가능한 객체 수로 해석하지 않는다.
- `Texture2D`와 `GpuAtlasRenderer`는 C# 객체 기준이다. Unity 네이티브 객체의 `Destroy`, GPU 메모리 반환, `Dispose` 호출을 세는 지표는 별도다.
- 프로세스 종료 후 운영체제가 반환한 메모리는 GC 회수 개수에 넣지 않았다.

## 게임 코드를 바꾸지 않은 방법

1. 같은 버전의 공식 Windows CoreCLR 지원 모듈을 기존 `.tooling` Editor에 설치했다. 설치 파일의 공식 MD5와 Unity 서명을 확인했다.
2. 이미 사용하던 패키지 검증 프로젝트를 이 폴더의 `unity-project`로 복사했다.
3. 복사본의 Editor 빌드 스크립트에서 Scripting Backend를 CoreCLR로 설정했다.
4. 외부 네이티브 프로파일러 `AllocationProfiler.dll`을 환경 변수로 연결했다.
5. `ICorProfilerCallback2.ObjectAllocated`로 할당을 기록했다. GC의 이동·생존 범위와 세대 정보를 함께 추적해 회수를 집계했다.

샘플링 방식이 아니다. 게임의 IL 재작성도 하지 않았다.
패키지 코드·셰이더·네이티브 파일 **42개**의 실행 전후 SHA-256이 같았다.
복사한 게임 런타임 C# 파일 **3개**도 원본과 같았다.
원본 `compute` 프로젝트는 이 검증을 위해 수정하지 않았다.

첫 연결 실행의 `loaded-runtime-modules.json`에는 `coreclr.dll`, `UnityClrProfiler.dll`, 외부 `AllocationProfiler.dll`이 같은 프로세스에 로드된 기록이 있다.
최종 측정은 같은 Player 실행 파일을 사용했다. Unity 내장 프로파일러 DLL을 교체하거나 비활성화하지 않았다.

## 수집기의 검산

별도의 일반 .NET 시험 프로그램에서 개수를 미리 정한 객체로 검산했다.
이 시험용 코드와 강제 GC는 게임에 넣지 않았다.

| 검산 타입 | 기대 생성 | 측정 생성 | 기대 회수 | 측정 회수 |
|---|---:|---:|---:|---:|
| `ProfilerCalibrationTransient` | 10,000 | 10,000 | 10,000 | 10,000 |
| `ProfilerCalibrationRetained` | 2,000 | 2,000 | 0 | 0 |
| `ProfilerCalibrationYoung` | 500 | 500 | 500 | 500 |

이동하는 GC, 이동하지 않는 GC, Gen0 수집을 포함했다.
최종 게임 측정의 객체 ID 충돌, 세대 범위 조회 실패, 중첩 GC, 저장 용량 초과, 타입 이름 잘림은 모두 **0**이었다.
해석하지 못한 타입 이름도 **0개**였다.
타입별 회수 합계와 GC별 회수 합계가 일치했다.
모든 타입에서 `생성 = 회수 + 회수 기록이 없는 객체`가 성립했다.

GC당 최대 **2,734개**는 세대 범위를 확인할 수 없었다.
Frozen heap 등의 가능성이 있으나 원인을 확정하지 않았다.
이 객체들은 회수됐다고 추정하지 않고 추적 목록에 남겼다.
따라서 전체 회수 개수의 완전성을 모든 종류의 힙까지 보장하지 않는다.

## 종료 처리와 조건

이 Unity Player는 정상 종료 코드 `0`으로 끝나도 외부 프로파일러의 CLR `Shutdown` 콜백을 호출하지 않았다.
GC 직후 JSON만 저장하면 마지막 GC 이후의 할당이 빠진다.
이를 해결하기 위해 수집기가 파일 매핑에 카운터를 계속 기록한다.
외부 실행 스크립트가 프로세스 종료를 기다린 후 파일을 읽어 최종 JSON을 만든다.
최종 기록의 `captureEnd`는 `process-exit`이다. `shutdown: false`는 CLR 종료 콜백의 부재를 표시한다.
GC 진행 중 종료와 카운터 갱신 중 종료 여부도 검사했다.

이번 검증은 `DOTNET_gcConcurrent=0`을 설정했다.
동시 GC를 켠 일반 게임에서의 범용 추적기 검증은 아직 하지 않았다.
모든 할당에 콜백이 발생하므로 실행 속도와 메모리에 계측 비용이 생긴다.
이번 결과는 할당·회수 기능 검증용이다.

기존 게임 검증도 통과했다: 8회 반복, 430,080픽셀 일치, 입력 16개 해제, 장면 취소 1회, 샘플 렌더러 4개.
종료 로그에는 Unity `JobTempAlloc` 잔여 할당 경고가 있다. 이 네이티브 경고의 원인은 이번 C# 객체 집계 검증에서 조사하지 않았다.

## 파일과 재실행

이 폴더는 진단 수집기와 2026-10-24 결과를 보관한다.
실행 시 필요한 Unity Player는 별도로 빌드한다. Node.js와 Windows C++ 빌드 도구가 필요하다.

- `AllocationProfiler.cpp`, `CallbackStubs.h`: 외부 수집기. SDK 헤더 자체는 설치된 Visual Studio에서 읽는다.
- `build-profiler.ps1`: MSVC와 Windows SDK로 DLL을 빌드한다. 필요하면 `-VisualStudioRoot`와 `-OutputDirectory`를 지정한다.
- `run-profile.ps1`: Player를 실행하고 종료 후 파일 매핑을 JSON에 반영한다. 필요하면 `-ProfilerPath`를 지정한다.
- `finalize-profile.mjs`: 종료 후 저장된 카운터의 일관성을 확인한다.
- `calibration/`: 알려진 개수의 객체로 검사하는 별도 .NET 10 프로그램.
- `Editor/CoreClrProfileBuild.cs`: 기존 패키지 검산 프로젝트를 CoreCLR로 빌드할 때 사용한 Editor 스크립트.
- `Results/`: 원본 JSON, 타입별 CSV, 검산 결과와 패키지 소스 해시.

저장소 루트에서 PowerShell 7로 실행한다. 빌드 결과는 `.validation/coreclr-profiling`에 생성한다.

```powershell
pwsh -File Verification/Profiling/CoreClr/build-profiler.ps1
pwsh -File Verification/Profiling/CoreClr/run-profile.ps1 `
  -Executable 'C:/YourProject/Build/Game.exe' `
  -Output '.validation/coreclr-profiling/game-profile.json' `
  -TimeoutSeconds 600
```

`Executable`은 Windows x64 CoreCLR Player 경로로 바꾼다.
Player가 정상 종료하면 JSON과 `.snapshot.bin`을 남긴다.
수집 중에는 프로세스를 강제로 종료하지 않는다. 종료 상태가 불완전하면 최종 변환이 실패한다.
프로파일러 환경 변수는 위와 같이 별도 PowerShell 프로세스에서 설정한다.

같은 패키지 검산 장면을 재현하려면 먼저 `Tools/Verify-GpuAtlasPackage.ps1`로 별도 검산 프로젝트를 만든다.
그 프로젝트의 `Assets/Verification/Editor`에 `Editor/CoreClrProfileBuild.cs`를 복사한다.
Windows CoreCLR 지원 모듈을 설치한 같은 Unity 버전으로 `-executeMethod CoreClrProfileBuild.Run`을 실행한다.
생성된 `Build/CoreClrPackageProbe.exe`를 측정한다. 실행 인수 `-atlas-report`로 게임 검산 JSON 경로를 전달할 수 있다.

기록된 타입 개수는 실행마다 달라질 수 있다.
`Results/`는 이전 검산 기록으로 유지한다. 새 측정 결과로 자동 덮어쓰지 않는다.

## 공식 자료

- [Unity 6.7 CoreCLR 지원 범위](https://docs.unity.com/en-us/engine/6000.7/manual/programming-environment/scripting-backends/coreclr)
- [Unity Windows Player의 UnityClrProfiler.dll](https://docs.unity.com/en-us/engine/6000.7/manual/platform-specific/windows/standalone-binaries)
- [Microsoft ObjectAllocated 콜백](https://learn.microsoft.com/en-us/dotnet/framework/unmanaged-api/profiling/icorprofilercallback-objectallocated-method)
- [Microsoft 프로파일러 환경 변수](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/debugging-profiling)
