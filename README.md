# Unity ASTC · BC7 Compute Encoder

`compute/`는 Unity 6000.7.0b5 베타 프로젝트입니다. 이 문서는 사용법과 지원 범위의 기준 문서입니다. 측정 결과와 복원 이미지는 [벤치마크 보고서](Benchmark/Results/report.html)에 있습니다.

현재 Editor는 `.tooling/Unity/6000.7.0b5/Editor/Unity.exe`에 있습니다. Hub에도 이 경로를 등록했습니다. Pipeline은 `0.7.0-exp.1`로 고정합니다. `0.8.0-exp.1`의 CLI 속성 어셈블리 분리와 6.7 SRP의 참조가 맞지 않아 컴파일 오류가 발생합니다. URP는 17.7.0, UGUI는 2.7.0, Input System은 6.7.0입니다. 복구본과 마이그레이션 검산은 `.validation/migration-6000.7.0b5/`에 있습니다.

Windows Player의 스크립팅 백엔드는 기존 Mono입니다. CoreCLR로 전환하지 않았습니다. ASTC와 BC7은 `Fast`·`Balanced`·`HighQuality`를 제공합니다. 기존 품질의 열거형 값과 기본 Fast는 유지합니다. 검산 범위와 실행 방법은 아래 ‘검산’이 기준입니다.

```powershell
& 'C:\Program Files\Unity Hub\resources\unity.exe' open .\compute --editor-version 6000.7.0b5 --args '-force-d3d12'
```

## 현재 범위

GPU가 한 밉 레벨의 LDR RGBA를 ASTC로 압축합니다. 4×4·5×5·6×6·8×8·10×10·12×12 정사각 블록을 지원합니다. 블록은 16바이트입니다. 가장자리의 남은 픽셀은 마지막 유효 픽셀로 채웁니다.

BC7 GPU 인코더도 제공합니다. BC7은 PC용 4×4 형식이며 8bpp입니다. Android·iOS용 경로는 ASTC입니다. BC7 사용법과 제한은 아래 ‘BC7 GPU 압축’에 있습니다.

`Fast`는 자주 압축하는 용도입니다. `Balanced`는 더 많은 격자와 끝점 보정을 탐색합니다. `HighQuality`는 Balanced 후보에 16개 가중치 모드, 추가 초기 끝점, RGB 채널별 분리 평면을 더합니다. 끝점을 4회 보정하고 격자 노드가 영향을 주는 픽셀로 가중치를 보정합니다. 세 품질은 단일 분할, RGB 직접 끝점·RGB scale 끝점·RGBA 직접 끝점을 사용합니다. 끝점은 ASTC 비트·trit·quint 정수열로 저장합니다. 생성 표의 기준은 Arm astcenc 5.7.0입니다.

ASTC의 HDR, 다중 분할, 밉 생성, 노멀 맵 전용 최적화는 없습니다. 입력은 0~1로 제한됩니다. astcenc의 전체 탐색과 같은 품질을 제공하지 않습니다. 알파 UI는 4×4부터 검토하십시오. 6×6에서는 가는 경계와 독립적인 RGB·알파 변화의 손실이 큽니다. HighQuality는 처리 비용이 큽니다. 매 프레임 사용 여부는 목표 기기의 시간·발열 측정으로 판단합니다.

Windows에서 실제 GPU 인코딩과 공식 디코더 대조를 완료했습니다. **Android·iOS 기기의 시간·발열·연속 압축은 측정하지 않았습니다. 네이티브 텍스처 업로드는 실험 기능입니다.** 모바일 셰이더 코드 생성과 기기 실행은 서로 다른 검증입니다.

## 파일 안내

| 경로 | 역할 |
|---|---|
| `compute/Assets/Astc/Shaders/AstcEncoder.compute` | Fast·Balanced·HighQuality 인코딩 |
| `compute/Assets/Astc/Shaders/AstcTables.hlsl` | GPU 표 접근 |
| `compute/Assets/Astc/Runtime/AstcLookup.cs` | 생성된 GPU 표 데이터 |
| `compute/Assets/Astc/Runtime/AstcEncoder.cs` | 동기·비동기 읽기, 재사용 GPU 버퍼와 행 범위 |
| `compute/Assets/Astc/Runtime/AstcImage.cs` | 데이터·텍스처·파일 포장 |
| `compute/Assets/Astc/Runtime/AstcGpuTexture.cs` | 실험용 모바일 GPU 텍스처 갱신 |
| `compute/Assets/Astc/Runtime/AstcGpuStream.cs` | 매 프레임 사용 예제 |
| `compute/Assets/Astc/Runtime/AstcGpuRetirement.cs` | GPU 완료 후 자원 해제 |
| `compute/Assets/Astc/Editor/AstcEncoderWindow.cs` | 내보내기와 기본 예제 씬 |
| `compute/Assets/Astc/Editor/AstcValidation.cs` | 런타임과 분리된 검산 |
| `compute/Assets/Astc/Editor/Benchmark/` | 원래 구현·스칼라 디코더·비교 측정 |
| `compute/Assets/Astc/Editor/Benchmark/AstcPeakMetrics.cs` | RGB·알파·합성의 피크 오차와 최악 블록 계산 |
| `compute/Assets/Bc7/Shaders/Bc7Encoder.compute` | BC7 세 품질의 진입 커널 |
| `compute/Assets/Bc7/Shaders/Bc7HighQuality.hlsl` | 2분할 후보 선택·모드 1·3·7 탐색 |
| `compute/Assets/Bc7/Shaders/Bc7Partitions.hlsl` | Microsoft 참조의 64개 분할·anchor 표 |
| `compute/Assets/Astc/Editor/Benchmark/HighQualityBenchmark.cs` | 6.7에서 세 품질을 같은 조건으로 비교 |
| `compute/Assets/Bc7/Runtime/` | BC7 버퍼·동기·비동기 API, 텍스처와 DDS 포장 |
| `compute/Assets/Bc7/Runtime/Bc7GpuTexture.cs` | Windows D3D12의 GPU 버퍼→BC7 텍스처 갱신 |
| `compute/Assets/Bc7/Runtime/Bc7GpuStream.cs` | 매 프레임 압축·표시 컴포넌트 |
| `compute/Assets/Bc7/Editor/` | 내보내기, 별도 디코더, 하드웨어 대조와 비교 측정 |
| `Tools/Run-Bc7Benchmark.ps1` | 같은 60개 입력의 GPU·Unity BC7 비교 |
| `Tools/Run-HighQualityBenchmark.ps1` | 세 해상도의 540개 GPU 비교 |
| `compute/Assets/TextureCompression/Runtime/FrameSlicedGpuJob.cs` | GPU 스냅샷·프레임별 제출·fence 진행 관리 |
| `Tools/Verify-FrameSliced.ps1` | 분할 바이트·게시·스냅샷·0GC·플랫폼 컴파일 검산 |
| `Tools/Run-FrameSlicedBenchmark.ps1` | 1024 대표 입력 2개의 일괄·행 묶음 처리량 측정 |
| `Tools/Render-FrameSlicedBenchmark.mjs` | 프레임 분산 처리량 보고서 생성 |
| `Tools/Render-HighQualityBenchmark.mjs` | 고퀄 모드의 품질·피크·시간 비교 보고서 |
| `Tools/Generate-Bc7Tables.ps1` | 고정 Microsoft 소스에서 BC7 분할 표 생성 |
| `Tools/Verify-Bc7.ps1` | BC7 형식·비동기 출력·셰이더 컴파일 검산 |
| `Tools/Build-Bc7WindowsPlugin.ps1` | Windows x64 D3D12 복사 플러그인 빌드·설치 |
| `Tools/Verify-Bc7GpuTexture.ps1` | 직접 복사·연속 프레임·자원 해제 검산 |
| `Tools/Run-Bc7GpuTextureBenchmark.ps1` | GPU 압축·BC7 복사·샘플링 처리량 측정 |
| `Tools/Verify-Bc7GpuPlayer.ps1` | 별도 Windows Player의 연속 프레임 검산 |
| `Verification/Bc7GpuPlayerProbe.cs` | 정상 런타임과 분리된 Player 검산 소스 |
| `Tools/Update-AstcPeakMetrics.ps1` | 저장된 복원 이미지에서 피크 지표 갱신 |
| `Native/` | D3D12·Vulkan·Metal 브리지의 기준 소스와 외부 헤더 |
| `compute/Assets/Astc/Plugins/` | Unity에 설치한 네이티브 산출물 |
| `Benchmark/Input/manifest.json` | 입력 60개의 크기·알파 통계·SHA-256 |
| `Benchmark/Sources/sources.json` | 원본 출처·라이선스·SHA-256 |
| `Benchmark/Results/report.html` | 현재 비교 보고서 |

## Git 기록 범위

소스·Unity 프로젝트 설정·입력 원본과 라이선스·측정 JSON/CSV·HTML 보고서를 기록합니다. Unity 캐시, 설치 도구, 임시 검산, Native/build와 벤치마크의 생성 PNG·ASTC·BC7 블록은 제외합니다. 파일은 로컬에 유지합니다. 새 체크아웃에서 복원 이미지 패널을 보려면 벤치마크를 다시 실행합니다. 측정 manifest의 SHA-256을 보존하도록 Git의 줄바꿈 자동 변환을 끕니다.

## CPU 결과와 파일 내보내기

Unity에서 **Tools → ASTC → Encoder**를 엽니다. 원본·블록 크기·Quality를 지정하고 **Encode and export .astc**를 누릅니다. 창의 기본 품질은 Balanced입니다. **Tools → ASTC → Create demo scene**은 기본 비교 씬을 생성합니다. 기존 씬은 변경하지 않습니다.

아래 코드는 Unity 메인 스레드에서 실행합니다. `encoderShader`에는 `AstcEncoder.compute`를 연결합니다.

```csharp
using UnityCompute.Astc;
using UnityEngine;

using var encoder = new AstcEncoder(encoderShader);
AstcImage image = await encoder.EncodeAsync(
    source, AstcBlockSize.Block4x4,
    srgb: true, sampledInputIsSrgb: false, quality: AstcQuality.Fast);
Texture2D compressed = image.CreateTexture();
// 사용이 끝나면 Destroy(compressed)를 호출합니다.
```

작업이 끝날 때까지 원본과 인코더를 유지합니다. 비동기 읽기를 지원하지 않으면 `Encode`를 사용합니다. 동기 API는 GPU를 기다립니다. 두 API는 출력 버퍼와 CPU 배열을 할당합니다. 매 프레임의 기본 경로로 사용하지 마십시오.

원본은 Texture2D 또는 생성된 단일 샘플 RenderTexture입니다. Read/Write 옵션은 필요하지 않습니다. MSAA 원본은 먼저 단일 샘플로 해석합니다.

`image.Data`는 헤더 없는 블록입니다. 크기는 `ceil(width / blockSize) × ceil(height / blockSize) × 16`입니다. `CreateTexture()`는 ASTC 샘플링 지원을 확인한 뒤 CPU 데이터를 업로드합니다. `ToAstcFile()`은 16바이트 헤더를 붙입니다. 파일에는 색 공간과 방향 정보가 없습니다. 데이터는 Unity의 아래쪽 행부터 시작합니다. 외부 뷰어에서는 세로 방향이 반대로 보일 수 있습니다.

## 재사용 GPU 출력

출력 버퍼를 한 번 만들고 재사용할 수 있습니다.

```csharp
var encoder = new AstcEncoder(encoderShader);
var blocks = new ComputeBuffer(
    AstcEncoder.RequiredBlockCount(source, AstcBlockSize.Block4x4), 16);

// 원본 갱신 후 반복 호출합니다.
encoder.EncodeGpu(source, blocks, AstcBlockSize.Block4x4,
    srgb: true, sampledInputIsSrgb: false, quality: AstcQuality.Fast);
```

이 API는 출력 배열을 만들거나 CPU로 읽지 않습니다. 버퍼 요소의 stride는 16바이트입니다. `rowStrideBlocks`가 0이면 행을 연속 배치합니다. 값을 지정하면 한 행의 블록 간격을 지정합니다. 이때 버퍼는 `rowStrideBlocks × ceil(height / blockSize)`개 요소를 수용해야 합니다. 패딩은 쓰지 않습니다.

버퍼만 생성해도 압축 텍스처가 되지는 않습니다. 아래 브리지가 버퍼를 ASTC 텍스처로 복사합니다. 직접 버퍼 API를 사용할 때는 마지막 GPU 사용이 끝날 때까지 원본·인코더·출력 버퍼를 유지합니다. 그 후 버퍼와 인코더를 Dispose합니다.

### 모바일 GPU 텍스처 갱신 — 실험 기능

Android ARM64 Vulkan과 iOS ARM64 Metal용 브리지입니다. GLES 업로드 브리지는 없습니다. Windows Editor에서는 실행할 수 없습니다. ASTC 샘플링·컴퓨트·CPU graphics fence가 필요합니다.

```csharp
// 생성 시 한 번 실행합니다.
var output = new AstcGpuTexture(encoderShader, source.width, source.height,
    AstcBlockSize.Block4x4, srgb: true);
material.SetTexture("_BaseMap", output.Texture);

// 원본 갱신 후, 결과를 그릴 명령을 제출하기 전에 호출합니다.
output.EncodeFrame(source, sampledInputIsSrgb: false, quality: AstcQuality.Fast);

// 마지막 소비자의 그리기 명령 제출 후 사용을 중지하고 호출합니다.
output.Dispose();
```

같은 버퍼·텍스처·CommandBuffer를 재사용합니다. CPU readback과 매 프레임 Apply는 없습니다. 최초 생성 때만 유효한 투명 블록을 업로드합니다. Metal 복사를 위해 행을 256바이트에 맞춥니다. Dispose는 CPU graphics fence 통과 후 네이티브 패킷과 GPU 자원을 해제합니다. 프로세스 종료 시 남은 패킷은 OS가 회수합니다.

원본은 출력과 같은 크기이며 다른 텍스처여야 합니다. `UploadStatus`의 1은 복사 명령 기록 완료이며 GPU 완료를 뜻하지 않습니다. 음수는 브리지 오류입니다. Vulkan의 -4/-5는 자원 접근, -6은 복사 용도·크기·형식 불일치입니다.

Unity graphics queue에서 실행합니다. async compute queue와 커스텀 RenderGraph 통합은 구현하지 않았습니다. `AstcGpuStream` 컴포넌트에 Shader·Source·Target Renderer를 지정하면 반복 호출 예제를 사용할 수 있습니다. 머티리얼에는 `_BaseMap` 또는 `_MainTex`가 필요합니다. 오류는 Console에 표시합니다. 일시 정지나 그래픽 장치 재생성 뒤에는 출력 객체를 다시 만드십시오.

### 네이티브 빌드와 설치

Android 빌드는 Clang/LLD로 외부 런타임 의존성이 없는 ARM64 공유 라이브러리를 만듭니다. 기본 Clang 경로는 현재 설치된 Swift 6.3.2입니다. 다른 환경에서는 `Build-AstcAndroidPlugin.ps1 -Clang <path>`를 지정합니다. Android API 24를 대상으로 합니다.

```powershell
pwsh -File .\Tools\Install-AstcNativePlugins.ps1
# Unity Pipeline 준비 후 실행합니다.
& 'C:\Program Files\Unity Hub\resources\unity.exe' command eval `
  'return UnityCompute.Astc.Editor.AstcNativePluginSetup.Configure();' `
  --project-path .\compute --json --result-only
```

Android .so는 컴파일·링크·ELF64 AArch64 검사를 통과했습니다. Android 빌드는 ARM64와 Vulkan을 선택합니다. 현재 6.7 Editor에 Android Build Support·SDK·NDK·OpenJDK가 있습니다. APK 생성과 기기 실행은 수행하지 않았습니다.

iOS에는 .mm와 Unity 인터페이스 헤더를 복사합니다. `-SkipAndroidBuild`로 iOS 소스만 설치할 수 있습니다. macOS·Xcode에서 컴파일해야 합니다. 현재 환경에는 iOS SDK가 없습니다. 두 플랫폼의 네이티브 복사·알파 표시·방향·색 공간·장시간 반복·자원 해제 검증이 남아 있습니다.

## 색 공간과 알파

`srgb`는 출력 RGB의 색 공간입니다. 알파는 항상 선형 값입니다. 입력은 straight RGBA로 취급합니다. premultiplied 입력을 자동 변환하지 않습니다.

ASTC·BC7의 `EncodeAsync`는 Edit Mode에서 읽기가 완료될 때까지 Editor 갱신과 Scene View 재그리기를 요청합니다. 6.7에서 유휴 화면의 GPU 제출이 늦어지는 경우를 처리합니다. GPU 완료를 동기로 기다리지 않습니다. Player의 비동기 읽기 경로와 `EncodeFrame`에는 이 Editor 처리가 없습니다.

`sampledInputIsSrgb`는 **GPU가 읽은 뒤의 RGB 값**이 sRGB인지 지정합니다. 기본값은 false입니다. Linear 프로젝트에서 sRGB 텍스처를 읽으면 GPU가 이미 선형 값으로 바꾸므로 기본값을 사용합니다. Gamma 프로젝트에서 sRGB 코드 값을 그대로 읽으면 true를 전달합니다. 선형 데이터와 선형 RenderTexture에는 false를 사용합니다.

데스크톱에서도 컴퓨트 지원이 있으면 압축과 내보내기는 가능합니다. 결과 표시에는 해당 ASTC 형식의 샘플링 지원이 필요합니다.

## 여러 프레임에 나누는 압축

`AstcGpuTexture`와 `Bc7GpuTexture`에 `BeginEncode`·`EncodeNextFrame`을 제공합니다. 시작할 때 품질과 색 공간 설정을 고정합니다. 이후 Update마다 일부 블록 행을 제출합니다. 앞선 GPU fence가 남아 있으면 이번 호출은 제출을 미룹니다. CPU에서 GPU 완료를 기다리지 않습니다. Player에서는 같은 프레임에 여러 번 호출해도 압축 묶음은 최대 한 번 제출합니다.

```csharp
// output은 재사용하는 Bc7GpuTexture입니다. Update에서 호출합니다.
if (!output.IsEncoding)
    output.BeginEncode(source, quality: Bc7Quality.HighQuality);
bool completed = output.EncodeNextFrame(maxBlockRows: 4);
float progress = output.EncodingProgress;
```

ASTC도 같은 순서로 호출합니다. 품질에는 `AstcQuality`를 사용합니다. `SubmittedBlockRows`는 제출한 행 수입니다. `CompletedBlockRows`와 `EncodingProgress`는 fence 통과를 확인한 행 수입니다. 마지막 묶음 뒤에 전체 압축 버퍼를 텍스처로 한 번 복사합니다. 처리 중인 일부 행을 화면에 게시하지 않습니다. 같은 출력 텍스처를 유지하며 이전 완성 결과를 계속 표시합니다.

기존 Stream 컴포넌트에서 **Spread Across Frames**를 켜면 이 경로를 사용합니다. **Block Rows Per Frame**은 기본 4이며 압축 블록 단위입니다. 1024 높이의 4×4 형식은 256개 블록 행입니다. 4행씩이면 최소 64번, 32행씩이면 최소 8번 제출합니다. 여기에 스냅샷·완료 확인과 GPU 대기 프레임이 더해질 수 있습니다. 결과 갱신 간격이 길어지며 처리 중 원본 갱신을 별도 작업으로 쌓지 않습니다. Stream의 기존 기본값은 일괄 압축입니다.

**Snapshot Input**은 기본으로 켭니다. 시작할 때 mip 0을 같은 graphics format의 GPU RenderTexture로 복사합니다. 이후 원본 값이 바뀌어도 이번 결과는 캡처한 입력을 사용합니다. 원본 객체는 작업 완료까지 유지합니다. 입력 형식의 RenderTexture와 CopyTexture 지원이 필요합니다. 지원하지 않거나 메모리를 줄여야 하면 `snapshotInput:false`를 지정하고 입력 값을 완료까지 변경하지 않습니다. RGBA8 1024 스냅샷은 약 4MiB를 추가로 사용합니다. 첫 생성과 입력 형식 변경 때는 스냅샷을 할당합니다. 이후 Begin·Step은 같은 자원을 재사용합니다.

작업 중 새 BeginEncode나 일괄 EncodeFrame 호출은 거부합니다. 작업을 중단하려면 마지막 소비자의 그리기를 제출한 뒤 Dispose합니다. 스냅샷·버퍼·출력 텍스처는 GPU fence 뒤에 해제합니다. 매 프레임 CPU readback·Apply·관리 배열 할당은 없습니다. 기존 `EncodeAsync`는 전체 압축을 제출한 뒤 CPU 읽기를 비동기로 완료하는 API입니다. 프레임 분산은 GPU 작업의 제출 단위를 나누는 별도 경로입니다. Unity graphics queue를 사용합니다.

블록 행 수는 엄격한 ms 제한이 아닙니다. 품질·입력 내용·기기에 따라 한 묶음의 비용이 달라집니다. 스냅샷과 마지막 텍스처 복사도 GPU 작업입니다. 여러 출력의 예산은 각각 적용되므로 호출자가 전체 작업량을 조정합니다. [프레임 분산 비교](Benchmark/Results/frame-sliced/report.html)는 알파 UI·비알파 사진 각 1장의 1024 입력에서 모든 행 묶음을 측정합니다. 일괄 압축과 4행·32행 묶음을 비교합니다. CPU 완료 읽기를 일부 포함한 처리량입니다. GPU timestamp·전체 프레임 지연·모바일 기기 시간은 아닙니다.

```powershell
pwsh -File .\Tools\Verify-FrameSliced.ps1
pwsh -File .\Tools\Run-FrameSlicedBenchmark.ps1
node .\Tools\Render-FrameSlicedBenchmark.mjs
```

현재 Windows 검산에서 42개 행 범위·126개 묶음의 바이트가 일괄 결과와 같았습니다. 선택하지 않은 행과 패딩도 유지했습니다. Editor의 분산 12개 작업은 최종 복사를 각각 한 번만 수행했습니다. 원본 변경 중 스냅샷 유지, 재사용 Begin·Step 0바이트, 중간 Dispose를 포함한 출력 7개 해제를 확인했습니다. Player는 같은 프레임에서 완료 fence 뒤에 Step을 다시 호출해도 행 예산을 넘지 않는지 확인합니다.

직접 버퍼를 관리할 때는 `EncodeGpu`의 `firstBlockRow`·`blockRowCount`로 범위를 지정합니다. 출력은 전체 텍스처의 행 위치에 기록합니다. `blockRowCount:0`은 지정한 시작 행부터 끝까지입니다. 버퍼는 전체 행과 `rowStrideBlocks`를 수용해야 합니다. 선택하지 않은 행과 패딩은 쓰지 않습니다. ASTC 초기 baseline 셰이더는 부분 행을 지원하지 않습니다.

## 비교 측정

보관된 성능 결과는 Unity 6000.6.6f1에서 측정했습니다. 프로젝트 마이그레이션으로 기존 측정 시각과 시간을 변경하지 않습니다. 현재 Editor의 검산 결과와 과거 성능 측정은 구분합니다.

동일한 20개 원본을 256·512·1024로 준비합니다. 해상도마다 알파 10장·비알파 10장입니다. ASTC는 4×4와 6×6를 비교합니다. 원래 GPU 구현·Fast·Balanced·astcenc fastest/fast/medium의 720행을 기록합니다. BC7 Fast·Balanced·Unity Normal의 4×4 결과 180행을 더하면 총 900행입니다. 보고서의 4×4 선택에서 두 형식을 같은 8bpp로 비교합니다.

```powershell
pwsh -File .\Tools\Get-AstcBenchmarkTextures.ps1
pwsh -File .\Tools\Verify-Astc.ps1
pwsh -File .\Tools\Run-AstcBenchmark.ps1
pwsh -File .\Tools\Verify-Bc7.ps1
pwsh -File .\Tools\Run-Bc7Benchmark.ps1
pwsh -File .\Tools\Run-Bc7GpuTextureBenchmark.ps1
node .\Tools\Render-AstcBenchmark.mjs
```

입력 생성기는 Windows PowerShell 7과 System.Drawing을 사용합니다. Poly Haven CC0 사진과 Arm 5.7.0 참조 이미지를 받습니다. 독립 알파 마스크와 UI도 만듭니다. Arm 원본 3장은 256×256이며 큰 입력에서는 확대합니다. 출처와 변환 결과는 manifest에 기록합니다.

GPU 동기 API 시간과 재사용 버퍼 묶음 시간을 구분합니다. astcenc는 프로세스 시간과 도구가 보고한 코딩 시간을 구분합니다. Unity BC7 Normal은 `EditorUtility.CompressTexture`의 API 호출 시간입니다. 입력 복사·업로드는 이 시간에서 제외합니다. 내부 백엔드와 스레드 수는 확인하지 않았습니다. ASTC와 BC7은 다른 시각에 측정했습니다. 클록은 고정하지 않았습니다. 측정 범위가 달라 단일 속도 배수로 일반화하지 않습니다. RGB PSNR·알파 PSNR·검은색/흰색 합성 PSNR·SSIM의 정의와 제한은 보고서에 있습니다.

RGB·알파·합성마다 최대 절대오차, 픽셀 오차의 P99, 최악 압축 블록 PSNR과 위치도 기록합니다. 좌표는 원본의 왼쪽 위가 (0,0)입니다. 보고서의 오차 대상 선택은 피크 표와 이미지별 수치에 적용됩니다. 각 복원 카드에서 문제 위치를 확대하면 모든 패널이 같은 원본 위치를 표시합니다. 피크 지표의 정의와 그룹 집계 방식은 보고서의 ‘측정 방법과 한계’가 기준입니다.

기존 압축 결과의 피크 지표만 갱신할 때는 Unity Pipeline을 준비한 뒤 다음을 실행합니다. 저장된 원본·복원 PNG를 사용합니다. 압축과 속도 측정을 다시 실행하지 않습니다. 기존 측정 시각과 수치를 유지하고 `peakMetricsUtc`만 별도 기록합니다.

```powershell
pwsh -File .\Tools\Update-AstcPeakMetrics.ps1
node .\Tools\Render-AstcBenchmark.mjs
```

보고서 생성기는 입력 해시 60개와 900행의 조합·그룹을 검산합니다. `baseline/fast/balanced/bc7`의 `measurements.json`을 현재 결과로 읽습니다. BC7 결과가 없으면 ASTC 720행만 표시합니다. `*-probe*`와 이전 측정에서 남은 파일은 탐색 기록이며 현재 집계에서 제외합니다. `Update-AstcPeakMetrics.ps1`은 ASTC 세 실행의 피크 지표만 갱신합니다. BC7 피크 지표는 BC7 벤치마크에서 계산합니다.

BC7의 직접 텍스처 갱신 시간은 `bc7/gpu-texture-measurements.json`의 120행에 별도로 기록합니다. 기존 900행의 시간은 덮어쓰지 않습니다. 해상도·그룹·품질별 10장 평균을 보고서의 ‘BC7 매 프레임 텍스처 갱신’에 표시합니다. 16회 준비 뒤 16회 EncodeFrame을 3묶음 실행합니다. 묶음 끝에 1픽셀 하드웨어 샘플링·readback으로 완료를 기다리고 시간을 16으로 나눕니다. 이 비용 일부도 측정에 포함됩니다. 매 프레임 API에는 readback이 없습니다. 이 수치는 처리량이며 GPU timestamp나 프레임 지연이 아닙니다. CPU 제출 시간과 관리 힙 할당도 별도로 기록합니다.

### HighQuality 선택과 새 비교

`EncodeGpu`·`Encode`·`EncodeAsync`·`EncodeFrame`의 `quality`에 `AstcQuality.HighQuality` 또는 `Bc7Quality.HighQuality`를 전달합니다. 두 Encoder 창과 Stream 컴포넌트에서도 선택할 수 있습니다. GPU 버퍼와 텍스처를 재사용하는 경로는 같습니다. 기본 Fast는 바꾸지 않습니다.

```csharp
astcOutput.EncodeFrame(source, quality: AstcQuality.HighQuality);
bc7Output.EncodeFrame(source, quality: Bc7Quality.HighQuality);
```

[HighQuality 비교 보고서](Benchmark/Results/high-quality/report.html)는 6.7에서 세 품질을 같은 조건으로 다시 측정합니다. 256·512·1024에서 각각 알파 10장·비알파 10장입니다. ASTC 4×4·6×6와 BC7 4×4의 총 540행입니다. 준비 2회와 동기 API 5회를 측정합니다. 재사용 버퍼와 BC7 텍스처 갱신은 8회씩 3묶음입니다. 기존 6.6의 900행과 120행 직접 복사 시간은 덮어쓰지 않습니다. 보고서 생성기는 해상도별 기록의 구현 해시가 서로 같은지 확인합니다. 현재 코드와 다르면 측정 당시 결과라는 안내를 표시합니다. 현재 행 분산 처리량은 별도 보고서에서 확인합니다.

```powershell
pwsh -File .\Tools\Run-HighQualityBenchmark.ps1
node .\Tools\Render-HighQualityBenchmark.mjs
node .\Tools\Render-AstcBenchmark.mjs
```

다음은 프레임 분산 기능 추가 전 RTX 3070 Ti Laptop GPU의 D3D12 측정입니다. 1024×1024의 각 10장 집계입니다. 품질 향상은 Balanced 대비 RGBA PSNR입니다. 시간은 재사용 버퍼 처리량의 10장 평균입니다.

| 형식 | 알파 품질 향상 | 비알파 품질 향상 | 알파 HQ 시간 | 비알파 HQ 시간 |
|---|---:|---:|---:|---:|
| ASTC 4×4 | +2.80 dB | +0.76 dB | 19.86 ms | 23.01 ms |
| ASTC 6×6 | +0.37 dB | +0.48 dB | 147.60 ms | 140.10 ms |
| BC7 4×4 | +0.68 dB | +1.18 dB | 13.05 ms | 13.75 ms |

BC7의 압축·텍스처 복사·샘플링 처리량은 알파 13.32 ms, 비알파 13.91 ms입니다. 세 해상도와 세 품질에서 매 프레임 호출의 관리 힙 할당은 0바이트였습니다. 이 수치는 데스크톱의 묶음 처리량입니다. 모바일 시간이나 전체 프레임 비용을 뜻하지 않습니다. 자주 압축할 때는 Fast를 기본으로 사용하십시오. 특히 ASTC 6×6 HighQuality는 품질 향상에 비해 처리 비용이 큽니다.
새 보고서는 RGBA·RGB·알파·합성 PSNR, 피크 지표, 블록별 오차 변화, BC7 모드 사용량과 관리 힙 할당을 표시합니다. astcenc medium의 저장 복원은 품질 참고로 사용합니다. 그 CPU 시간과 새 GPU 시간을 같은 실행의 속도 배수로 비교하지 않습니다. 개별 지표는 다른 채널의 품질과 교환될 수 있습니다. ASTC의 탐색 오차와 공식 디코더의 정수 반올림 차이로 일부 블록의 정수 RGBA 오차는 증가할 수 있습니다. BC7은 HighQuality가 Balanced보다 블록 오차를 늘리지 않는지 별도로 검사합니다.

## BC7 GPU 압축

Unity에서 **Tools → BC7 → Encoder**를 엽니다. 원본과 품질을 지정하면 `.dds` 파일로 내보낼 수 있습니다. Fast는 모드 6을 사용하고 필요한 블록에서 독립 알파 모드 5도 비교합니다. Balanced는 모드 4·5·6과 채널 회전, 추가 끝점 보정을 탐색합니다. HighQuality는 GPU에서 실제 Balanced 결과를 먼저 저장합니다. 추가 후보는 이 결과와 비교합니다. 기존 모드의 탐색을 6회로 늘립니다. 64개 2분할 패턴을 빠르게 평가하고 상위 4개를 5회 정밀 보정합니다. 불투명 블록에는 모드 1·3, 알파 블록에는 모드 7을 추가합니다. 모드 0·2, 3분할, 밉 생성은 없습니다. 입력은 LDR RGBA입니다. 전체 모드를 탐색하는 참조 인코더와 품질이 다를 수 있습니다.

```csharp
using UnityCompute.Bc7;
using UnityEngine;

var encoder = new Bc7Encoder(bc7Shader); // Bc7Encoder.compute
var blocks = new ComputeBuffer(Bc7Encoder.RequiredBlockCount(source), 16);
encoder.EncodeGpu(source, blocks,
    srgb: true, sampledInputIsSrgb: false, quality: Bc7Quality.Fast);
```

위 호출은 출력 버퍼를 재사용하며 CPU 배열이나 readback을 만들지 않습니다. `rowStrideBlocks`는 블록 단위 행 간격입니다. 0은 연속 배치입니다. 패딩 영역은 쓰지 않습니다. 마지막 GPU 사용이 끝날 때까지 원본·인코더·버퍼를 유지하고 그 후 Dispose합니다. 원본 조건과 색 공간 인수는 ASTC API와 같습니다.

`Encode`와 `EncodeAsync`는 CPU 데이터가 있는 `Bc7Image`를 반환합니다. 비동기 읽기를 지원하지 않으면 `Encode`를 사용합니다. 두 API는 출력 버퍼와 배열을 할당합니다. `image.CreateTexture()`는 BC7 샘플링 지원을 확인한 뒤 CPU 데이터를 업로드합니다. 매 프레임 표시에는 아래 `Bc7GpuTexture`를 사용합니다. 기존 재사용 벤치마크는 압축 버퍼를 만드는 비용입니다. 텍스처 복사 비용은 별도 측정합니다.

원본 크기가 4의 배수가 아니면 마지막 유효 픽셀로 블록을 채웁니다. `Width/Height`는 원본 크기입니다. `CreateTexture()`는 `TextureWidth/TextureHeight`의 4픽셀 배수 크기로 만듭니다. 원본 영역을 표시하려면 UV에 `image.UvScale`을 곱합니다. 샘플러의 가장자리 주소 모드와 UV 경계도 용도에 맞게 설정합니다.

`Data`는 헤더 없는 16바이트 BC7 블록입니다. `ToDdsFile()`은 DX10 DDS 헤더를 붙입니다. 출력 형식은 BC7 UNORM 또는 BC7 sRGB이며 알파는 straight입니다. 기본 행 순서는 Unity의 아래쪽부터입니다. 외부 DDS를 만들 때는 `Encode` 또는 `EncodeAsync`에 `flipY: true`를 지정합니다. 내보내기 창은 이 방향을 사용합니다. 같은 데이터를 Unity 텍스처로 바로 만들 때는 기본 방향을 사용합니다.

Windows D3D12의 하드웨어 디코더와 별도 C# 디코더를 대조합니다. 검산은 세 품질에서 알파·단색·역상관 RGB·홀수 크기·1픽셀·색 공간·행 반전 42개 사례와 패딩 출력을 확인합니다. 비동기 Texture2D·RenderTexture 출력 6개도 확인합니다. 별도 비트 표본 192개는 모드 1·3·7의 모든 분할·anchor를 대조합니다. 세 커널의 D3D11·D3D12·Vulkan·Metal 데스크톱 변형 12개도 컴파일합니다. 실제 실행 검증은 현재 D3D12에 한정됩니다.

### 매 프레임 BC7 텍스처 갱신

Windows x64 Editor와 Player의 D3D12 경로입니다. Unity 6000.7.0b5의 `IUnityGraphicsD3D12v8`을 사용합니다. BC7 샘플링·컴퓨트·CPU graphics fence가 필요합니다. D3D11·Vulkan·Metal에는 이 BC7 복사 브리지를 제공하지 않습니다.

6.7에서도 `Graphics`·`CommandBuffer`의 공개 `CopyTexture`는 텍스처 간 복사이며 `CopyBuffer`는 버퍼 간 복사입니다. 현재 ComputeBuffer의 압축 블록을 BC7 텍스처로 복사하는 경로는 네이티브 브리지를 사용합니다.

```csharp
// Unity 메인 스레드에서 한 번 생성합니다.
var output = new Bc7GpuTexture(bc7Shader, source.width, source.height, srgb: true);
material.SetTexture("_BaseMap", output.Texture);
material.SetTextureScale("_BaseMap", output.UvScale);

// 원본 갱신 후, 이 텍스처를 사용하는 그리기 명령을 제출하기 전에 호출합니다.
output.EncodeFrame(source, sampledInputIsSrgb: false, quality: Bc7Quality.Fast);

// 마지막 소비자의 그리기 명령을 제출한 뒤 사용을 중지하고 호출합니다.
output.Dispose();
```

같은 ComputeBuffer·BC7 Texture2D·CommandBuffer를 재사용합니다. 압축 셰이더 뒤에 D3D12 `CopyTextureRegion`을 기록합니다. Unity의 자원 상태 API가 UAV 쓰기→복사 읽기와 복사 쓰기→샘플링의 배리어를 관리합니다. 압축 데이터는 GPU에 유지합니다. 매 프레임 CPU readback, CPU 픽셀 복사, Apply, GPU 완료 대기는 없습니다. 최초 생성 때만 유효한 투명 BC7 블록을 업로드합니다.

복사 행은 256바이트에 맞춥니다. 출력 텍스처의 크기는 4픽셀 배수입니다. `Width/Height`는 원본 크기이며 `UvScale`은 패딩을 제외한 영역입니다. 원본은 생성 때 지정한 크기의 다른 2D 텍스처여야 합니다. `Texture`를 외부에서 Destroy하거나 Apply하지 마십시오. 여러 소비자는 같은 프레임 안에서 갱신 뒤에 읽습니다. async compute queue와 커스텀 RenderGraph 통합은 지원하지 않습니다.

`UploadStatus`의 1은 복사 명령 기록 완료입니다. GPU 완료를 뜻하지 않습니다. 음수는 브리지 오류입니다. -1은 인터페이스·그래픽 API, -2는 크기·행 간격, -3은 대상 형식, -4는 버퍼 크기, -5는 명령 목록, -6은 그래픽 장치 세대 불일치입니다. `RecordedCopies`는 기록한 복사 수입니다. 상태 조회는 작은 CPU 상태값만 읽으며 텍스처 픽셀을 읽지 않습니다.

Dispose는 graphics fence 통과 후 패킷·버퍼·텍스처를 해제합니다. Edit Mode에서는 해제 대기 중에만 Editor 프레임과 재그리기를 요청합니다. `IsReleased`로 지연 해제 완료를 확인할 수 있습니다. 원본도 마지막 GPU 사용이 끝날 때까지 유지합니다. 장치 재생성 뒤에는 출력 객체를 다시 만듭니다. 종료나 Editor 도메인 재로드 때 완료되지 않은 패킷은 즉시 해제하지 않습니다. 네이티브 자원 참조를 유지해 대기 중인 콜백의 잘못된 접근을 막습니다. 프로세스 종료 시 OS가 회수합니다.

`Bc7GpuStream`에 Encoder Shader·Source·Target Renderer를 연결하면 Update에서 압축하고 머티리얼에 표시합니다. 머티리얼에는 `_BaseMap` 또는 `_MainTex`가 필요합니다. 원본을 다른 컴포넌트의 Update에서 갱신하면 스크립트 실행 순서를 지정해 원본 갱신이 먼저 실행되게 합니다.

플러그인 빌드에는 MSVC x64와 Windows SDK가 필요합니다. 기본 Editor 버전은 프로젝트의 `ProjectVersion.txt`에서 읽습니다. 설치 경로는 Unity CLI의 Editor 목록에서 찾습니다. 자동 탐색이 안 되면 `-UnityEditor`에 `Unity.exe`가 있는 폴더를 지정합니다. MSVC와 Windows SDK의 경로는 `-VisualStudio`, `-WindowsSdk`로 지정합니다. DLL은 `compute/Assets/Bc7/Plugins/Windows/x86_64/`에 설치합니다.

```powershell
pwsh -File .\Tools\Build-Bc7WindowsPlugin.ps1
# Unity 리컴파일과 Pipeline 준비 뒤 실행합니다.
pwsh -File .\Tools\Verify-Bc7GpuTexture.ps1
pwsh -File .\Tools\Verify-Bc7GpuPlayer.ps1
```

검산은 런타임과 분리합니다. GPU에서 만든 알파 원본을 압축하고 직접 복사한 텍스처를 컴퓨트 셰이더와 픽셀 셰이더로 읽습니다. CPU 데이터를 업로드한 참조 텍스처와 대조합니다. 1픽셀·홀수 크기·256·512·1024, sRGB·선형, 세 품질의 42개 사례를 확인합니다. 연속 원본 갱신, 같은 네이티브 텍스처 유지, 관리 힙 할당과 fence 뒤 해제도 확인합니다. 검산의 readback은 비교용이며 정상 EncodeFrame 경로에는 없습니다.

6.7의 세 품질 검산은 Editor에서 408회 복사·75,623,376개 채널 대조를 통과했습니다. 차이는 0/255이며 출력 45개의 해제를 확인했습니다. 프레임 분산 추가 뒤 Windows Player는 일괄 180프레임과 분산 4개 작업·94프레임을 실행했습니다. 216회 복사·7,995,392개 픽셀 대조를 통과했습니다. 반복 호출과 재사용 분산 Begin·Step의 관리 힙 할당은 0바이트입니다.

Player 검산 도구는 `Verification/`의 소스를 임시 Unity 자산으로 복사하고 Windows 실행 파일을 빌드합니다. 기존 씬을 저장하거나 빌드 목록을 변경하지 않습니다. 검산 Player는 D3D12로 실행합니다. 종료 뒤 알려진 임시 소스·씬만 제거합니다. 빌드와 결과는 `.validation/bc7/`에 남깁니다. Windows Player Build Support가 필요합니다. 플러그인 DLL을 이미 로드한 Editor에서 다시 빌드해 설치하려면 먼저 Editor를 종료합니다. `-NoInstall`은 네이티브 빌드만 수행합니다.

복사 형식과 정렬 규칙은 Microsoft의 [CopyTextureRegion](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12graphicscommandlist-copytextureregion)과 [텍스처의 버퍼 배치](https://learn.microsoft.com/en-us/windows/win32/direct3d12/upload-and-readback-of-texture-data)를 따릅니다. D3D12 인터페이스 헤더는 설치된 6000.7.0b5 SDK에서 복사했습니다. 이 헤더는 6000.6.6f1과 동일합니다.

## 검산

Unity에서 프로젝트를 열고 Pipeline 연결을 기다립니다. `Verify-Astc.ps1`는 공식 astcenc 5.7.0 배포의 SHA-256을 확인합니다. 결과는 `.validation/`에 저장합니다. CLI가 다른 경로에 있으면 `-UnityCli`를 지정합니다.

- Fast·Balanced·HighQuality에서 각각 66개 GPU 인코딩을 공식 Arm 디코더로 복원합니다.
- 여섯 블록 크기, 투명도·알파 변화·역상관 RGB·무늬·홀수 크기·한 행·한 열·1픽셀·색 공간을 확인합니다.
- 스칼라 디코더와 모든 복원 채널을 대조합니다. 최대 1/255 반올림 차이를 허용합니다.
- 패딩 GPU 출력 18개를 연속 출력과 대조합니다. 패딩을 쓰지 않는지도 확인합니다.
- 비동기 RenderTexture 출력 18개를 동기 출력과 대조합니다. Texture2D 입력도 대조합니다.
- 세 커널의 Android Vulkan·GLES와 iOS Metal 변형 9개를 컴파일합니다.
- 별도 피크 지표 검산은 완전 복원·P99 순위·숨은 RGB·알파 합성·부분 블록·좌표를 7개 사례로 확인합니다.

모바일 코드 생성은 Unity 6.7 내부 API를 사용합니다. 실제 자산을 다른 플랫폼 상태로 남기지 않도록 임시 셰이더 사본을 사용합니다. Unity 버전이 바뀌면 도구를 갱신해야 할 수 있습니다. 검산과 벤치마크는 정상 런타임의 시작 조건이 아닙니다. 테스트 통과는 모든 이미지 품질이나 모바일 실행을 보장하지 않습니다.

표를 다시 생성하려면 `.validation/reference/`에 Arm 5.7.0의 `Source/astcenc_quantization.cpp`와 `Source/astcenc_integer_sequence.cpp`를 두고 `Tools/Generate-AstcTables.ps1`을 실행합니다. 생성 후 셰이더 컴파일과 디코더 대조를 다시 수행합니다.

## 근거와 라이선스

ASTC 형식은 [Arm 설명](https://github.com/ARM-software/astc-encoder/blob/5.7.0/Docs/FormatOverview.md)과 [5.7.0 참조 구현](https://github.com/ARM-software/astc-encoder/tree/5.7.0)을 확인했습니다. BC7은 Microsoft의 [형식 설명](https://learn.microsoft.com/en-us/windows/win32/direct3d11/bc7-format)과 [모드별 비트 배치](https://learn.microsoft.com/en-us/windows/win32/direct3d11/bc7-format-mode-reference)를 기준으로 구현했습니다. 생성 표와 Arm 이미지는 [Apache 2.0](ThirdParty/astcenc/LICENSE.txt), Khronos 이미지는 [MIT](Benchmark/Sources/Khronos-LICENSE.txt)입니다. 사진 출처는 [sources.json](Benchmark/Sources/sources.json)에 있습니다.

Unity 헤더는 [Unity Companion License](Native/Unity/LICENSE.md)입니다. Vulkan 헤더는 v1.3.275이며 [라이선스 안내](Native/Vulkan/LICENSE.md)와 원문을 포함했습니다. 플랫폼 조건은 [Unity 컴퓨트 셰이더](https://docs.unity3d.com/6000.0/Documentation/Manual/class-ComputeShader-introduction.html)와 [GPU 텍스처 형식](https://docs.unity3d.com/6000.0/Documentation/Manual/texture-formats-reference.html)을 참조합니다.

BC7 2분할 패턴·anchor 표는 Microsoft DirectXTex의 [고정 리비전 기록](ThirdParty/DirectXTex/source.json)을 사용합니다. [MIT 라이선스](ThirdParty/DirectXTex/LICENSE.txt)를 포함했습니다. 표를 다시 만들려면 해당 `BC6HBC7.cpp`와 리비전을 `.validation/reference/DirectXTex/`에 두고 `Tools/Generate-Bc7Tables.ps1`을 실행합니다. 생성 후 192개 하드웨어 대조를 실행합니다.
