# GpuTextureAtlas

Unity용 GPU 텍스처 압축과 동적 압축 아틀라스.

라이선스: [MIT](LICENSE)

GPU에 로드한 Texture2D를 ASTC·BC7 아틀라스로 묶습니다. Read/Write를 끈 입력을 사용할 수 있습니다. 목표 형식·색 공간이 같은 입력은 압축 블록을 GPU에서 그대로 복사합니다. 다른 입력은 기기의 샘플링 지원을 확인한 뒤 GPU에서 변환하고 압축합니다.

실행 중 이미지를 추가·제거·재배치할 수 있습니다. 항목의 ID는 좌표가 바뀌어도 유지됩니다. 전용 렌더러는 새 텍스처와 UV를 자동으로 반영하며 9-slice와 SpriteMask를 지원합니다. ASTC·BC7 인코더는 단독으로도 사용할 수 있습니다.

**UPM 패키지와 Unity 6000.7.0b5 예제 프로젝트**를 제공합니다. `compute/`를 Unity Hub에서 열거나 아래 방법으로 패키지를 설치합니다. 사용법과 지원 범위는 이 문서를 기준으로 합니다.

## 패키지 설치와 데모

1. 이 저장소를 내려받습니다. Unity 6000.7.0b5에서 Package Manager를 엽니다.
2. **Install package from disk**로 `Packages/com.ittodo.gpu-texture-atlas/package.json`을 선택합니다.
3. 패키지의 Samples에서 **Dynamic Atlas**를 Import합니다.
4. **Tools → GpuTextureAtlas → Create sample scene**을 실행합니다. 씬을 저장하고 Play Mode로 들어갑니다.
5. Add·Remove·Repack·9-slice 버튼을 사용합니다. Inspector의 Inputs에는 Read/Write를 끈 임포트 텍스처를 지정할 수 있습니다. 비워 두면 예제 RGBA 이미지를 생성합니다.

예제의 렌더러는 Repack 후에도 같은 고정 ID를 유지합니다. 작업 중 버튼 입력을 막고 기존 아틀라스를 표시합니다. 씬 종료 시 취소를 요청하고 Task가 끝난 뒤 소유 자원을 해제합니다.

런타임 어셈블리는 `UnityCompute.GpuTextureAtlas`입니다. 사용자 코드가 asmdef를 사용하면 이 어셈블리를 참조합니다. 코어는 Addressables와 URP를 요구하지 않습니다. SpriteMask를 사용하면 URP 17.7 이상과 2D Renderer가 필요합니다. URP가 설치되면 선택 어셈블리 `UnityCompute.GpuTextureAtlas.URP`가 연동을 등록합니다. 검산·벤치마크·AI Editor 도구는 패키지 의존성이 아닙니다.

Git URL 설치 경로는 `https://github.com/ittodo/GpuTextureAtlas.git?path=/Packages/com.ittodo.gpu-texture-atlas`입니다. Package Manager의 **Install package from git URL**에서 사용합니다. 다른 Unity 버전과 iOS 실기기 호환성을 검증한 것은 아닙니다.

`.tgz` 파일로 전달하려면 저장소 루트에서 아래 명령을 실행합니다. Node.js의 npm을 사용하며 네트워크에 게시하지 않습니다. 생성한 파일은 Package Manager의 **Install package from tarball**로 설치합니다. 아카이브에는 패키지와 데모만 들어갑니다.

```powershell
New-Item -ItemType Directory -Path .validation/distribution -Force
npm pack ./Packages/com.ittodo.gpu-texture-atlas --offline --cache .validation/npm-cache --pack-destination .validation/distribution
```

## 주요 기능

| 기능 | 동작 |
|---|---|
| GPU 압축 블록 복사 | 같은 형식·색 공간의 입력을 디코딩·재압축·CPU 픽셀 읽기 없이 패킹합니다. |
| 입력 변환 | 다른 형식·색 공간의 입력만 목표 ASTC·BC7 형식으로 GPU 변환합니다. |
| 동적 편집 | Add·Remove·Repack, 빈자리 재사용, 필요 시 아틀라스 확장, 고정 ID를 제공합니다. |
| 전용 렌더러 | ID로 이미지를 연결합니다. 크기·피벗·반전·색상·정렬·9-slice·SpriteMask를 설정합니다. |
| GPU 인코더 | ASTC 정사각 블록 6종과 BC7, Fast·Balanced·HighQuality를 제공합니다. |
| 프레임 분산 | Pack·Add·Repack의 복사·변환·압축에 프레임별 공유 예산을 적용합니다. 완료 후 결과를 게시합니다. |
| Unity 로딩 연동 | Addressables·AssetBundle에서 로드한 Texture2D를 입력합니다. |
| 별도 검산 도구 | 압축 블록 보존·디코더 대조·실제 카메라 출력·Editor와 Player 실행을 확인합니다. |

배치 좌표와 항목 메타데이터는 CPU에서 처리합니다. GPU 경로는 이미지 픽셀을 CPU로 읽지 않습니다. 원본 파일 로딩과 초기 자원 생성에는 CPU 작업이 있습니다. 아틀라스의 Pack·Add·Repack은 GPU 완료를 비동기로 기다립니다. `GpuAtlasFrameBudget`을 전달하면 GPU 제출도 여러 프레임으로 나눕니다.

## 플랫폼과 검증 상태

같은 형식·색 공간의 아틀라스 패킹은 `Graphics.CopyTexture`를 사용합니다. 부분 블록도 포함하며 자체 네이티브 브리지를 호출하지 않습니다. 대상 GPU의 압축 형식 샘플링·텍스처 복사·graphics fence 지원이 필요합니다. 다음 표는 형식 변환과 GPU 인코더 출력에 필요한 네이티브 브리지의 범위입니다.

| 플랫폼 | 그래픽 API | 목표 압축 형식 | 현재 검증 |
|---|---|---|---|
| Windows x64 | Direct3D12 | BC7 | Editor·Windows Player의 GPU 압축·패킹·편집·렌더링 검산 통과 |
| Android ARM64 | Vulkan | ASTC | SM-A426N / Adreno 619에서 복사·편집·GPU 변환·9-slice·SpriteMask 검산 통과 |
| iOS ARM64 | Metal | ASTC | 셰이더 코드 생성 확인. Xcode 네이티브 빌드·실기기 실행 미검증 |

Windows에서도 ASTC 블록을 GPU로 인코딩하고 `.astc` 파일로 내보내는 경로를 검산했습니다. ASTC 텍스처의 실시간 샘플링·GPU 출력은 지원 기기에서 사용합니다. Android 측정 범위와 비용은 아래 결과를 따릅니다. iOS와 Mali GPU 실기기는 아직 검증하지 않았습니다.

GPU 아틀라스 출력은 한 밉 레벨입니다. 밉 체인과 여러 아틀라스 페이지를 자동으로 만들지 않습니다. 전용 렌더러의 SpriteMask는 URP 2D Renderer가 필요합니다. 현재 Unity 베타에서는 마스크가 있는 공유 그룹의 자동 인스턴싱을 끕니다. 해당 조건은 아래 사용법에서 설명합니다.

### Android 모바일 성능

2026-10-24에 **SM-A426N / Adreno 619 / Android 13 / Vulkan / Unity 6000.7.0b5**에서 ASTC 4×4 sRGB를 측정했습니다. 256·512·1024마다 알파 10장과 불투명 10장을 사용했습니다. [Android 보고서](Benchmark/Results/mobile-atlas/report.html)에 입력별 측정, 그룹별 P95·P99·최대값, 제외 사유와 원본을 제공합니다.

다음 값은 **20장을 합산한 중앙값**입니다. 입력 로딩과 예열을 제외했습니다. 즉시 복사는 `frameBudget: null`입니다. 분산 모드는 복사 16,384블록, Fast 압축 128블록, 변환 65,536픽셀/프레임으로 제한했습니다. CPU 열은 분산 복사의 최초 호출 비용입니다. 완료 열에는 프레임과 GPU fence 대기가 포함됩니다.

| 해상도 | ASTC 복사: CPU 호출 ms | ASTC 즉시 복사: 완료 ms | ASTC 분산 복사: 완료 ms | RGBA→ASTC Fast 분산: 완료 ms |
|---|---:|---:|---:|---:|
| 256×256 | 0.37 | 17.13 | 16.76 | 1,083.19 |
| 512×512 | 0.36 | 16.79 | 33.56 | 4,348.42 |
| 1024×1024 | 0.45 | 16.99 | 133.39 | 17,443.34 |

**이 기기에서 자주 갱신할 입력은 미리 압축한 ASTC를 복사하는 경로부터 사용하십시오.** 작은 프레임 예산은 작업을 나누지만 전체 완료 지연을 늘립니다. 위 Fast 설정으로 1024 이미지를 매 프레임 새로 압축할 수는 없습니다. HighQuality는 32블록/프레임으로 측정했으며, 1024 표본의 완료 중앙값은 141.60초였습니다. 90ms를 넘는 GPU 프레임도 있어 60fps용 설정으로 권장하지 않습니다. 라이브러리의 기본 압축 예산 1,024블록이 이 기기의 측정 설정과 같다는 뜻은 아닙니다.

256 입력 20장을 순환한 600초 반복 검사에서 Add·Remove·Repack을 **828회** 완료했습니다. 검사 중 5초간 백그라운드로 보낸 뒤 복귀했습니다. 배터리 센서는 27.3°C, Android 열 상태는 0을 기록했습니다. Unity 전체 할당량 추정치는 약 202.9~204.4MiB였습니다. 이 값에는 측정 앱과 보고서 메모리도 포함됩니다. GPU 온도와 VRAM 사용량을 확인한 결과는 아닙니다.

측정 앱에는 단순 카메라와 상태 UI가 있습니다. 아틀라스를 렌더링하는 게임 장면의 부하는 포함하지 않았습니다. FrameTimingManager 값은 앱 전체 GPU 프레임이며 압축 커널 시간이 아닙니다. IL2CPP의 호출별 관리 힙 할당 카운터와 기기의 그래픽 메모리 조회는 사용할 수 없어 N/A로 표시합니다. 같은 입력에 대한 반복 횟수는 1회입니다. 입력·기기·예산이 달라지면 다시 측정하십시오.

성능 측정 중 앱이 장시간 백그라운드에 있었던 `1024/brown_leather/HighQuality` 한 건은 같은 APK로 다시 측정해 교체했습니다. 교체 전 원본과 재측정 원본을 보고서에 보존했습니다. 전체 실행의 백그라운드/복귀 2회 중 1회는 이 중단이며, 나머지 1회는 600초 반복 검사의 의도적인 복귀 검사입니다.

## 사용법 안내

- [처음 사용하는 순서](#처음-사용하는-순서)와 [GPU 아틀라스 생성](#gpu-경로--readwrite-끄기)
- [패키지 설치와 데모](#패키지-설치와-데모), [공유 프레임 예산과 취소](#아틀라스-작업의-프레임-분산과-취소)
- [이미지 추가](#기존-gpu-아틀라스에-이미지-추가), [제거와 재배치](#제거와-재배치)
- [전용 렌더러](#고정-id를-사용하는-전용-렌더러), [9-slice](#9-slice), [SpriteMask](#spritemask)
- [Addressables·AssetBundle 로딩과 해제](#addressables와-assetbundle), [문제 확인](#문제-확인)
- [ASTC 인코더](#astc-인코더의-지원-범위), [BC7 인코더](#bc7-gpu-압축), [프레임 분산 압축](#여러-프레임에-나누는-압축)
- [벤치마크 보고서](Benchmark/Results/report.html), [HighQuality 보고서](Benchmark/Results/high-quality/report.html), [Android 보고서](Benchmark/Results/mobile-atlas/report.html), [검산 절차](#검산)

Windows의 최근 아틀라스 렌더러 검산은 Editor·Player 각각 122프레임·562,176픽셀을 통과했습니다. 이는 렌더링 정확도 검산입니다. 성능 측정의 환경·입력·측정 시점은 각 보고서에 따로 기록합니다.

## 별도 관리 객체 진단

[CoreCLR·Android IL2CPP 할당 진단](Verification/Profiling/README.md)에 수집기 소스와 2026-10-24 검산 결과를 보관합니다. UPM 패키지에는 포함하지 않습니다. Android의 지표는 GC 후 약한 참조 소실이며 정확한 메모리 반환 횟수가 아닙니다.

## 임포트한 ASTC·BC7 텍스처의 아틀라스 패킹

**기본 Texture Importer를 그대로 사용합니다. 새 확장자와 전용 임포터는 필요하지 않습니다.** 원본 PNG 등의 Inspector 미리보기와 임포트 설정도 유지합니다. 플랫폼 Override에서 Android·iOS는 ASTC, PC는 BC7을 지정합니다. GPU 경로는 Read/Write Enabled를 끈 뒤 Addressables 또는 AssetBundle을 빌드합니다. 다른 형식으로 임포트한 입력도 받을 수 있습니다. 원본 크기를 유지하려면 Non Power of 2를 None으로 설정하고 Max Size도 확인합니다.

### 처음 사용하는 순서

1. Unity 6000.7.0b5로 `compute`를 엽니다. Windows에서는 Graphics API를 Direct3D12로 설정합니다. Android는 Vulkan, iOS는 Metal을 사용합니다.
2. 원본 텍스처의 Read/Write Enabled를 끕니다. 색상 이미지에는 sRGB를 켭니다. 알파가 있으면 Alpha Source를 Input Texture Alpha로 설정합니다. 보통 밉 스트리밍은 끄고, 사용할 밉이 메모리에 있는 상태에서 패킹합니다.
3. 플랫폼별 텍스처 Override를 설정합니다. Windows는 BC7, Android·iOS는 ASTC를 선택합니다. 같은 형식·색 공간으로 준비하면 런타임 재압축 비용을 줄일 수 있습니다. 다른 형식·색 공간도 아래 GPU 변환 경로로 입력할 수 있습니다.
4. 해당 플랫폼의 Addressables 콘텐츠 또는 AssetBundle을 빌드합니다. 다른 플랫폼의 번들을 재사용하지 않습니다. 로더에서 Texture2D를 읽고 아래 `PackTexturesAsync`를 await합니다.
5. `GpuAtlasRenderer`를 추가하고 `Bind(atlas, imageId)`를 호출합니다. 처음 입력 목록의 ID는 0부터 시작합니다. 애플리케이션의 이미지 키와 ID를 함께 보관합니다. 번들·Addressables의 로드 순서나 중복 가능한 이름을 영구 키로 가정하지 않습니다.
6. 크기·피벗·정렬을 설정합니다. 9-slice는 [아래 설정](#9-slice)을 사용합니다. SpriteMask를 쓰는 카메라는 [2D Renderer 설정](#spritemask)을 적용합니다.
7. 실행 중 추가·제거·재배치는 아래 API를 순서대로 호출합니다. 같은 아틀라스의 이전 갱신 Task가 끝난 뒤 다음 갱신을 실행합니다. 연결된 렌더러는 자동 갱신됩니다.
8. 패킹·추가 Task가 끝나면 원본 로드 핸들을 해제할 수 있습니다. 마지막 사용 뒤 view를 Unbind하고 아틀라스를 Dispose합니다. 자세한 수명 예제는 [로딩과 해제](#addressables와-assetbundle)에 있습니다.

| 대상 | 목표 형식 | 변환 인코더 | 변환 셰이더 |
|---|---|---|---|
| Windows x64 / D3D12 | `TextureFormat.BC7` | `Packages/com.ittodo.gpu-texture-atlas/Shaders/Bc7/Bc7Encoder.compute` | `Packages/com.ittodo.gpu-texture-atlas/Shaders/Atlas/GpuAtlasConvert.compute` |
| Android ARM64 / Vulkan, iOS ARM64 / Metal | `TextureFormat.ASTC_4x4` 등 | `Packages/com.ittodo.gpu-texture-atlas/Shaders/Astc/AstcEncoder.compute` | 위와 같음 |

모바일에서 같은 ASTC 형식·색 공간을 복사할 때는 Compute 셰이더와 자체 네이티브 브리지를 호출하지 않습니다. 다른 형식·색 공간을 변환할 때는 대상 기기의 Compute 지원과 네이티브 인코더 출력 브리지가 필요합니다. 세부 지원 범위는 아래 GPU 경로 설명과 검산 결과를 따릅니다. 투명 UI의 경계 품질은 ASTC 4×4부터 확인합니다. 임포트한 Texture2D 전체가 입력 한 장입니다. Sprite Mode Multiple의 서브 Sprite 영역을 자동으로 추출하지 않습니다.

### GPU 경로 — Read/Write 끄기

`GpuTextureAtlas`는 모든 입력을 지정한 목표 형식·색 공간의 한 아틀라스에 넣습니다. 실제 GPU GraphicsFormat이 목표와 같으면 압축 블록을 그대로 복사합니다. 형식·색 공간·ASTC 블록 크기가 다르면 그 입력만 GPU에서 읽어 변환하고 목표 인코더로 압축합니다. 입력을 건너뛰지 않습니다. 목표는 BC7 또는 정사각 LDR ASTC 4·5·6·8·10·12입니다.

```csharp
using UnityCompute.TextureCompression;
using UnityEngine;

// Addressables 또는 AssetBundle에서 로드한 Texture2D 배열입니다.
GpuTextureAtlas atlas = await GpuTextureAtlas.PackTexturesAsync(
    sourceTextures, TextureFormat.BC7, srgb: true,
    encoderShader: bc7EncoderShader, conversionShader: atlasConversionShader,
    quality: GpuAtlasQuality.Fast, maxSize: 4096);
material.mainTexture = atlas.Texture;

// 배치 순서는 크기에 따라 달라져도 Entries의 순서는 입력과 같습니다.
CompressedAtlasEntry entry = atlas.Entries[0];
Vector2 atlasUv = entry.MapUv(new Vector2(0.5f, 0.5f));
// atlas를 컴포넌트 필드에 보관합니다. 마지막 사용 뒤 atlas.Dispose()를 호출합니다.
```

`bc7EncoderShader`에는 `Bc7Encoder.compute`를 지정합니다. 모바일 ASTC 목표에는 `AstcEncoder.compute`를 지정합니다. `atlasConversionShader`에는 `GpuAtlasConvert.compute`를 지정합니다. Inspector 참조 등으로 셰이더가 Player 빌드에 포함되게 하십시오. 입력이 전부 목표 형식·색 공간이면 두 셰이더 참조가 없어도 됩니다. 품질 설정은 변환하는 입력에만 적용합니다. 같은 입력은 HighQuality를 선택해도 재압축하지 않습니다.

같은 형식만 패킹할 때는 다음과 같이 호출합니다. Read/Write를 꺼도 됩니다. 추가·확장·재배치에도 `CopyTexture`를 사용합니다. ASTC 입력에는 해당 `TextureFormat.ASTC_*`와 색 공간을 지정합니다. 셰이더 없이 다른 형식·색 공간을 전달하면 입력 검사에서 오류를 반환합니다.

```csharp
GpuTextureAtlas atlas = await GpuTextureAtlas.PackTexturesAsync(
    sourceTextures, TextureFormat.BC7, srgb: true, maxSize: 4096);
await atlas.AddAsync(extraTexture);
atlas.Remove(imageId);
await atlas.RepackAsync();
```

`Entries`는 입력 순서를 유지합니다. `CopiedSourceIndices`는 그대로 복사한 입력이고 `ConvertedSourceIndices`는 변환한 입력입니다. sRGB UNorm과 선형 UNorm은 다른 형식으로 판단합니다. 변환 셰이더가 읽을 때 하드웨어가 sRGB RGB를 선형 값으로 바꿉니다. 선형 RGBA half-float 임시 텍스처를 거쳐 인코더가 목표 sRGB 변환을 한 번 적용합니다. 알파는 선형 값입니다. 변환 입력은 손실 압축을 다시 거치므로 원본과 픽셀 값이 달라질 수 있습니다. LDR 범위를 벗어난 값은 0~1로 제한합니다. Premultiplied 알파를 자동으로 바꾸지 않습니다.

크기는 서로 달라도 됩니다. 예를 들어 101×57의 4×4 입력은 104×60 영역을 예약합니다. `PixelRect`는 유효 크기이고 `PackedRect`는 마지막 블록까지 포함합니다. 같은 형식에서 크기가 블록 배수이면 아틀라스에 바로 영역 복사합니다. 배수가 아니면 **전체 밉 복사 오버로드**로 블록 수가 같은 정렬된 임시 텍스처에 복사한 뒤, 그 텍스처를 아틀라스에 영역 복사합니다. 두 단계 모두 `Graphics.CopyTexture`이며 디코딩·재압축·CPU 읽기가 없습니다. 영역 복사의 크기만 올림하면 원본 범위를 벗어나므로 사용할 수 없습니다. Unity의 [영역 복사 정렬 조건](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Graphics.CopyTexture.html)을 따릅니다. 임시 텍스처는 크기별로 재사용하고 GPU 완료 후 해제합니다. 원래 유효 영역 밖의 블록 texel도 그대로 보존하며 투명색이나 가장자리 복제색으로 바꾸지 않습니다.

다른 형식·색 공간의 입력은 GPU에서 마지막 유효 픽셀을 복제해 목표 블록 크기의 배수까지 채웁니다. 변환용 텍스처와 인코더 출력은 크기별로 재사용합니다. 재압축 결과를 GPU 버퍼에서 압축 Texture2D로 옮기는 단계에는 네이티브 브리지가 필요합니다. BC7은 Windows x64 D3D12, ASTC는 Android ARM64 Vulkan·iOS ARM64 Metal입니다. ASTC 변환은 현재 모바일 Player용입니다. Windows에서 ASTC 목표로 대체하지 않습니다. 지원하지 않는 GPU·대상·업로드 브리지는 오류를 반환합니다. Android 실기기 검산 범위는 아래 결과를 따릅니다. iOS 실행과 Mali GPU 검증은 남아 있습니다.

`mipLevel`은 각 텍스처의 **현재 GPU에 업로드된** 밉 체인에서의 번호입니다. 0은 현재 GPU 리소스의 첫 번째 밉입니다. `GraphicsTexture`의 실제 크기·밉을 사용하므로 원본 Texture2D의 논리 크기와 다를 수 있습니다. 작업이 끝날 때까지 밉 제한·스트리밍 상태와 원본을 변경하지 마십시오.

### 기존 GPU 아틀라스에 이미지 추가

초기 패킹이 끝난 뒤 `AddAsync`로 Texture2D 하나 또는 목록을 추가합니다. 목표 형식·색 공간은 최초 생성 설정을 유지합니다. 같은 형식은 GPU 블록 복사로 넣습니다. 다른 형식·색 공간·ASTC 블록 크기는 새 입력만 변환·압축합니다. 기존 항목은 다시 압축하지 않습니다. 최초 원본 번들은 이미 해제되어 있어도 됩니다.

```csharp
int firstNewId = atlas.NextSourceIndex;
await atlas.AddAsync(moreTextures,
    encoderShader: bc7EncoderShader, conversionShader: atlasConversionShader,
    quality: GpuAtlasQuality.Balanced);

// 확장 시 Texture가 교체됩니다. 완료 직후 소비자의 텍스처 참조를 갱신합니다.
material.mainTexture = atlas.Texture;
CompressedAtlasEntry added = atlas.GetEntry(firstNewId);
// 기존 항목의 UV도 atlas.Entries에서 다시 가져와 머티리얼·메시 등에 적용합니다.
```

빈 공간에 들어가면 같은 Texture2D를 유지하며 기존 항목의 UV도 바뀌지 않습니다. 공간이 부족하면 두 변을 줄이지 않는 후보 중 작은 면적부터 확장을 시도합니다. 기존 전체 아틀라스를 GPU에서 복사하므로 이전 픽셀 좌표와 압축 블록은 유지합니다. 확장 영역의 빈 블록은 이전 아틀라스를 타일 복사해 채웁니다. 확장 시 정규화 UV는 달라집니다. 이전 `Texture`와 `CompressedAtlasEntry`를 계속 사용하지 마십시오. 소비자의 참조는 완료 직후 갱신합니다. 이전 텍스처는 마지막 제출 명령의 fence 뒤에 해제합니다. 호출자가 직접 파괴하지 않습니다.

`maxSize`와 `paddingBlocks`는 생성할 때 지정하며 이후 유지합니다. `MaxSize`는 기기 한도로 제한된 실제 최대 변 길이이고 `PaddingBlocks`는 예약 블록 수입니다. 기존 예약 공간과 픽셀 좌표를 유지하며 추가하므로 공간이 조각나면 추가가 실패할 수 있습니다. 아래의 `RepackAsync`로 먼저 정리한 뒤 추가를 다시 시도할 수 있습니다. 총 크기가 한도를 넘으면 다른 아틀라스를 만들거나 입력을 나누십시오. 자동 재배치와 밉 생성은 제공하지 않습니다.

`Entries`는 활성 항목만 보관하며 이전 항목 뒤에 새 입력 순서로 이어집니다. GPU 항목의 `SourceIndex`는 고정 ID입니다. 삭제 후에는 `Entries`의 목록 위치와 다를 수 있습니다. 이름이 같아도 ID는 다르며 삭제한 ID를 재사용하지 않습니다. 최신 좌표는 `GetEntry(id)` 또는 `TryGetEntry(id, out entry)`로 조회합니다. 없는 ID는 각각 KeyNotFoundException 또는 false를 반환합니다. `NextSourceIndex`는 다음 추가 작업의 첫 ID입니다. `CopiedSourceIndices`·`ConvertedSourceIndices`는 활성 ID만 포함합니다. 재배치해도 최초 복사·변환 분류는 유지합니다. `CopyCount`는 성공한 작업의 누적 복사 수입니다. `Version`은 생성 시 1이며 추가·실제 제거·재배치 성공마다 증가합니다. 추가의 `mipLevel`은 새 입력에만 적용하고 기본값은 0입니다. `SourceMipLevel`은 최초 패킹의 선택값입니다. CPU `CompressedAtlas`의 SourceIndex는 기존처럼 입력 목록 위치입니다.

한 아틀라스에는 한 번에 한 갱신 작업만 실행합니다. 이전 `AddAsync`·`RepackAsync`를 await한 뒤 추가·제거·재배치를 호출합니다. `IsUpdating`이 true인 동안에도 기존에 공개된 항목은 사용할 수 있습니다. 새 항목·텍스처·UV는 GPU 완료 뒤 함께 공개합니다. 입력·크기 오류와 GPU 작업 실패 시 이전에 공개한 항목은 유지합니다. 실패한 빈 공간 쓰기는 새 항목으로 공개하지 않습니다. 초기 패킹이 미완료이거나 이미 Dispose한 객체에는 갱신할 수 없습니다.

추가 입력의 Addressables 핸들·AssetBundle도 `AddAsync`가 정상 또는 예외로 끝날 때까지 유지합니다. 실패 시에도 이미 제출한 GPU 작업을 비동기로 기다린 뒤 예외를 반환합니다. 추가 중 `Dispose`한 경우에도 반환된 Task를 기다린 뒤 입력을 해제합니다. 프레임별 작업량 제한은 아래의 `frameBudget`으로 설정합니다.

### 제거와 재배치

`Remove(id)`는 고정 ID 하나를 제거하며 성공 시 true를 반환합니다. `Remove(ids)`는 목록에 있는 활성 ID를 한 번씩 제거하고 제거한 수를 반환합니다. 없는 ID·이미 제거한 ID·목록의 중복은 무시합니다. 아무것도 제거하지 않으면 Version도 유지합니다. 남은 이미지의 픽셀 좌표·UV·Texture는 바뀌지 않습니다. 예약 여백도 함께 비워 다음 추가에 사용합니다. 제거는 메타데이터만 바꾸므로 GPU 완료 대기가 없습니다. 예전 블록을 즉시 지우지는 않습니다. 제거한 항목의 UV는 이후 사용하지 마십시오.

`RepackAsync()`는 현재 남은 이미지의 크기로 배치를 다시 계산합니다. 압축 블록을 새 GPU 텍스처로 복사하며 디코딩·재압축·CPU 픽셀 읽기는 없습니다. ASTC·BC7 모두 같은 경로를 사용합니다. 원본 Addressables 핸들·번들·인코더 셰이더는 필요하지 않습니다. 조각난 공간을 정리하며 결과 크기는 배치 휴리스틱에 따라 정합니다. 항상 최소 크기를 보장하지 않습니다. 항목의 ID와 유효 크기·이름은 유지하고 픽셀 좌표와 UV는 달라질 수 있습니다. Texture는 매번 교체합니다. 완료 직후 텍스처 참조와 모든 활성 항목의 UV를 갱신합니다.

```csharp
// removeId와 keptId는 항목의 SourceIndex를 보관한 값입니다.
atlas.Remove(removeId);
await atlas.RepackAsync();
material.mainTexture = atlas.Texture;
if (atlas.TryGetEntry(keptId, out CompressedAtlasEntry current))
{
    Vector4 scaleOffset = current.UvScaleOffset;
    Vector4 uvClamp = current.UvClamp;
    // 소비자의 메시·머티리얼 UV 정보를 갱신합니다.
}
```

전부 제거해도 객체와 ID 카운터는 유지합니다. 빈 상태에서 재배치하면 압축 블록 하나 크기의 텍스처가 됩니다. 이 블록의 색은 보장하지 않습니다. 이후 `AddAsync`로 다시 채울 수 있습니다. 제거한 ID는 다시 유효해지지 않습니다. 재배치가 실패하거나 작업 중 Dispose되면 새 결과는 공개하지 않습니다. GPU 자원은 제출한 복사와 마지막 소비자의 fence 뒤에 해제합니다.

### 아틀라스 작업의 프레임 분산과 취소

Play Mode 또는 Player에서 같은 `GpuAtlasFrameBudget` 객체를 여러 아틀라스에 전달합니다. 이 객체가 같은 프레임의 사용량을 합산합니다. 아틀라스마다 새 객체를 만들면 각자 예산을 사용합니다. 같은 아틀라스의 갱신 작업은 순서대로 await합니다.

```csharp
using System.Threading;

// 로더 또는 매니저가 하나를 소유하고 여러 작업에 재사용합니다.
var budget = new GpuAtlasFrameBudget(
    copyBlocksPerFrame: 16384,
    encodeBlocksPerFrame: 1024,
    conversionPixelsPerFrame: 65536);
using var cancellation = new CancellationTokenSource();

var atlas = await GpuTextureAtlas.PackTexturesAsync(textures,
    targetFormat, srgb: true, encoderShader: encoder, conversionShader: convert,
    frameBudget: budget, cancellationToken: cancellation.Token);
await atlas.AddAsync(moreTextures, encoderShader: encoder, conversionShader: convert,
    frameBudget: budget, cancellationToken: cancellation.Token);
atlas.Remove(removeId);
await atlas.RepackAsync(frameBudget: budget, cancellationToken: cancellation.Token);
```

예산의 단위는 복사 블록 수, 압축 블록 수, 변환 픽셀 수입니다. 기본값은 위 예제와 같습니다. 변환 예산은 입력의 픽셀 한 행을 담을 수 있어야 합니다. 부족하면 입력 오류를 반환합니다. 압축 예산은 한 블록부터 설정할 수 있습니다. 블록 복사와 압축은 예산에 맞춰 직사각형 영역으로 나눕니다. 가로 블록 수보다 작은 압축 예산도 사용할 수 있습니다. 형식 변환은 픽셀 행 단위로 나눕니다. `frameBudget: null`은 기존 즉시 제출 방식입니다. 일반 Edit Mode에는 프레임 예산을 사용할 수 없습니다.

예산은 **작업량 제한이며 GPU 시간(ms) 보장이 아닙니다.** 텍스처 생성과 마지막 네이티브 업로드는 한 번에 실행합니다. 부분 블록을 보존하는 whole-mip 복사도 나눌 수 없습니다. 이 복사는 다른 복사 작업이 없는 프레임에서 한 번 실행하며 복사 예산을 넘을 수 있습니다. 큰 이미지와 고품질 압축은 완료까지 많은 프레임이 필요합니다. 기기별 안정성과 실행 시간을 확인한 뒤 예산을 정합니다.

Add·Repack이 진행되는 동안 렌더러는 기존 텍스처와 좌표를 사용합니다. 완료 후 새 상태를 함께 공개합니다. `CancellationToken` 취소나 `Dispose`는 이후 제출을 중단합니다. 이미 제출한 GPU 작업을 기다린 뒤 Task가 끝나므로 취소도 즉시 완료되지 않을 수 있습니다. 실패·취소된 결과는 공개하지 않습니다. 호출자는 해당 Task가 끝난 뒤 원본 로딩 핸들을 해제합니다. 작업 중에는 입력 텍스처와 밉 스트리밍 상태를 변경하지 않습니다. 씬 종료 시에도 취소 후 Task 완료와 핸들 해제를 처리해야 합니다.

아틀라스와 GPU 인코더가 소유한 출력·임시 텍스처에는 `HideFlags.DontSave`를 설정합니다. 아직 렌더러에 연결하지 않았더라도 `Resources.UnloadUnusedAssets`로 제거되지 않습니다. 소유 객체의 `Dispose`가 해제를 담당합니다. 호출자가 출력 Texture2D를 직접 Destroy하거나 hideFlags를 변경하지 않습니다.

### 고정 ID를 사용하는 전용 렌더러

`GpuAtlasRenderer`는 Sprite를 만들지 않고 공유 격자 메시와 GPU의 ID→UV 조회 표로 이미지를 그립니다. **이 컴포넌트는 Add·Remove·Repack 후 텍스처와 UV를 자동으로 갱신합니다.** 위 예제처럼 재질과 메시를 직접 사용하는 소비자는 수동 갱신이 필요합니다. ASTC·BC7 텍스처 모두 같은 렌더 API를 사용하며 Read/Write를 켜지 않습니다.

Inspector의 Add Component → Rendering → GPU Atlas Renderer로 추가하거나 코드에서 생성합니다. 아틀라스 객체는 초기 패킹을 await한 뒤 `Bind`로 연결합니다. 원본 이미지의 고정 `SourceIndex`를 ID로 전달합니다.

```csharp
using UnityEngine;
using UnityCompute.TextureCompression;

// 패킹을 끝낸 로더의 async 함수 안에서 사용합니다.
var imageObject = new GameObject("Atlas image");
var view = imageObject.AddComponent<GpuAtlasRenderer>();
view.Bind(atlas, imageId);
view.PixelsPerUnit = 100;
view.Color = Color.white;
view.SortingOrder = 10;

// 원본 픽셀 크기 대신 로컬 단위로 크기를 지정할 수도 있습니다.
view.Size = new Vector2(2, 1);
imageObject.transform.position = new Vector3(0, 0, 0);

await atlas.RepackAsync(); // view는 같은 ID의 새 위치를 자동으로 표시합니다.
atlas.Remove(imageId);    // 해당 ID를 사용하는 view가 자동으로 숨겨집니다.
```

Transform의 위치·회전·스케일은 일반 Unity 오브젝트처럼 사용합니다. 기본 `UseNativeSize`는 true이며 유효 픽셀 크기를 `PixelsPerUnit`으로 나눈 크기로 표시합니다. `Size`를 지정하면 UseNativeSize가 false가 됩니다. `Pivot`의 기본값은 (0.5, 0.5)입니다. `FlipX`·`FlipY`는 피벗을 기준으로 그림을 반전합니다. `Color`는 셰이더에서 선형 RGB·알파에 곱하는 색입니다. `SortingLayerId`와 `SortingOrder`는 생성한 MeshRenderer의 정렬 설정에 전달합니다. 부모 오브젝트의 레이어는 자식 렌더 오브젝트에 반영합니다.

목록 위치와 ID를 혼동하지 마십시오. `Bind(atlas, id)`를 다시 호출하면 다른 이미지를 표시합니다. 없는 ID는 숨기며, 이후 그 ID가 실제로 추가되면 자동으로 표시합니다. 제거한 ID는 다시 쓰지 않으므로 다른 이미지가 그 자리에 들어와도 예전 view는 계속 숨깁니다. `Unbind()`는 연결을 해제합니다. 컴포넌트나 오브젝트를 비활성화하면 공유 자원 참조를 놓고, 다시 활성화하면 연결된 아틀라스의 현재 상태로 표시합니다. Scene View에서도 코드로 연결한 결과를 표시할 수 있습니다. 런타임 아틀라스 객체 자체는 씬·프리팹에 직렬화하지 않습니다.

같은 아틀라스·셰이더를 쓰는 view는 재질·격자 메시·조회 표를 공유합니다. 격자는 16정점·18삼각형이며 Simple과 Sliced에서 함께 씁니다. 아틀라스 변경이 커밋될 때 활성 ID만 해시 표로 다시 업로드합니다. 표의 크기는 활성 항목 수에 따라 정하며 과거에 발급한 최대 ID를 따라 커지지 않습니다. ID는 정수로 전달하므로 큰 ID를 float로 반올림하지 않습니다. 위치·색상·테두리 변경과 변화 없는 프레임은 UV 표를 다시 만들지 않습니다. 재배치 때 개별 Sprite 생성이나 메시 UV 수정은 없습니다. GPU 인스턴싱용 셰이더 변형도 제공합니다. 투명도 정렬과 Unity의 배칭 판단 때문에 전체 이미지가 한 draw call이 된다고 보장하지 않습니다. 처리 시간과 할당량은 대상 장면에서 따로 측정하십시오.

컴포넌트가 만든 자식 MeshRenderer와 공유 GPU 자원은 컴포넌트가 관리합니다. 마지막 view가 연결을 놓으면 마지막 제출 명령의 fence 뒤에 해제합니다. 아틀라스를 Dispose하면 연결된 view는 즉시 숨기고 렌더 자원도 해제합니다. 아틀라스 소유권은 로더·호출자에게 있습니다. view 하나를 제거해도 아틀라스나 다른 view를 Dispose하지 않습니다. 아틀라스의 `IsDisposed`로 해제 상태를 확인할 수 있습니다. `LookupCapacity`와 `LookupUploadCount`는 현재 공유 표의 진단값이며 공유 상태를 다시 만들면 업로드 횟수도 새로 셉니다.

기본 셰이더는 Resources에 있어 Player 빌드에 포함됩니다. Shader Model 4.5와 구조화 GPU 버퍼가 필요합니다. UV는 유효 픽셀 중심으로 제한하므로 Bilinear에서도 다른 항목의 색을 읽지 않습니다. Unlit·일반 straight alpha 블렌드 방식입니다. URP Forward·Universal2D와 Built-in용 패스를 제공합니다. SpriteMask는 아래 URP 2D 경로를 사용합니다. 타일링·Sprite 애니메이터·2D 조명·스킨 변형·HDRP 전용 패스는 구현하지 않았습니다. Android 실기기와 iOS의 검증 범위는 [플랫폼 표](#플랫폼과-검증-상태)를 따릅니다.

### 9-slice

Inspector의 Draw Mode를 Sliced로 바꾸거나 다음 API를 사용합니다. `Border`의 순서는 **왼쪽·아래·오른쪽·위**입니다. 단위는 패킹한 항목의 유효 픽셀입니다. `Size`는 로컬 Unity 단위이며 0 이상입니다.

```csharp
view.DrawMode = GpuAtlasDrawMode.Sliced;
view.Border = new Vector4(12, 8, 12, 8);
view.PixelsPerUnit = 100;
view.Size = new Vector2(4, 2);
// 왼쪽·오른쪽 모서리의 너비는 각 0.12, 위·아래 높이는 각 0.08입니다.
// 가운데와 네 가장자리만 늘어납니다. 중심 영역도 채웁니다.
```

크기를 바꿀 때는 `Size`를 바꿉니다. Transform 스케일을 바꾸면 테두리도 함께 확대됩니다. 목표 크기가 두 테두리의 합보다 작으면 해당 축의 테두리를 같은 비율로 줄입니다. 음수 중심 영역이나 뒤집힌 격자를 만들지 않습니다. 지정한 테두리 합이 원본 폭·높이를 넘으면 원본 크기까지 같은 비율로 제한합니다. 테두리가 0이면 일반적인 늘이기와 같습니다. `UseNativeSize = true`이면 원본 크기÷PixelsPerUnit으로 표시합니다. `DrawMode = GpuAtlasDrawMode.Simple`은 Border를 무시하고 전체 이미지를 늘립니다. Tiled 방식은 제공하지 않습니다.

Texture2D에는 Sprite Editor의 Border 정보가 없습니다. Inspector에서 직접 지정하거나, **해당 텍스처 전체를 나타내는 Single Sprite**의 메타데이터를 별도로 전달합니다. 다음 읽기는 CPU 픽셀 접근이 아니므로 Read/Write가 필요 없습니다.

```csharp
view.Border = sourceSprite.border;
view.PixelsPerUnit = sourceSprite.pixelsPerUnit;
view.Pivot = sourceSprite.pivot / sourceSprite.rect.size;
```

원본을 낮은 밉·축소 크기로 패킹했다면 Border도 실제 패킹 크기에 맞춰 줄입니다. Sprite Mode Multiple이나 다른 SpriteAtlas에 들어간 Sprite의 border·pivot을 전체 Texture2D 항목에 그대로 적용하지 마십시오. 9-slice는 반전·피벗·색상·SpriteMask와 함께 쓸 수 있습니다. Add·Repack 뒤에도 설정과 ID를 유지합니다.

### SpriteMask

Unity의 **SpriteMask 컴포넌트**를 사용합니다. Unity 6.7 URP의 MeshRenderer 마스크 API에 연결합니다. 지원 조건은 [Unity의 MeshRenderer 마스크 문서](https://docs.unity.com/en-us/engine/6000.7/manual/unity2d/2d-urp/2d-renderer-urp-features/sprite-mask-interaction)와 같습니다. Built-in·URP Forward 카메라의 SpriteMask 연동은 제공하지 않습니다.

1. 카메라 Inspector의 Rendering → Renderer에서 `GpuAtlas2D_Renderer`를 선택합니다. PC_RPAsset과 Mobile_RPAsset에 목록 번호 1로 등록했습니다. 기본 렌더러 번호 0은 기존 3D 렌더러입니다. 코드에서는 `camera.GetUniversalAdditionalCameraData().SetRenderer(1)`을 호출합니다. 이 확장 메서드에는 `using UnityEngine.Rendering.Universal;`이 필요합니다.
2. `GpuAtlas2D_Renderer`의 Depth/Stencil Buffer를 켭니다. 이 프로젝트의 제공 에셋에는 켜져 있습니다. 다른 URP 에셋을 만들면 이 렌더러를 목록에 추가하고 실제 목록 번호를 사용합니다.
3. GameObject → 2D Object → Sprite Mask를 만듭니다. Sprite에 마스크 모양을 지정하고 Alpha Cutoff를 설정합니다. 알파가 기준 이상인 부분이 마스크 안쪽입니다. 마스크 Sprite는 원본 또는 별도 텍스처로 유지합니다. 런타임 아틀라스의 ID를 SpriteMask.sprite에 직접 넣는 API는 없습니다.
4. 대상 GpuAtlasRenderer의 Mask Interaction을 설정합니다. Custom Range를 쓰면 대상의 Sorting Layer·Order가 Back~Front 범위에 들어가게 설정합니다. SortingGroup을 사용하면 Unity의 마스크 범위 규칙을 따릅니다.

```csharp
view.MaskInteraction = SpriteMaskInteraction.VisibleInsideMask;
// 마스크 바깥에 표시하려면 VisibleOutsideMask, 마스크를 무시하려면 None입니다.
view.SortingLayerId = SortingLayer.NameToID("Default");
view.SortingOrder = 10;
mask.isCustomRangeActive = true;
mask.backSortingLayerID = mask.frontSortingLayerID = view.SortingLayerId;
mask.backSortingOrder = 0;
mask.frontSortingOrder = 20;
```

마스크가 없거나 범위 밖이면 VisibleInsideMask는 숨기고 VisibleOutsideMask는 표시합니다. 마스크 이동·알파 경계·범위 변경은 Unity가 처리합니다. Add·Remove·Repack 뒤에 마스크 연결을 다시 설정할 필요가 없습니다. 같은 아틀라스의 view마다 서로 다른 MaskInteraction을 지정할 수 있습니다. `Mask`·`RectMask2D`를 쓰는 Canvas UI 연동은 별도 기능입니다.

Unity 6000.7.0b5의 검산에서 자동 GPU 인스턴싱 경로가 MeshRenderer의 SpriteMask 스텐실을 무시하는 것을 확인했습니다. 이 때문에 같은 아틀라스·셰이더의 활성 view 중 MaskInteraction이 None이 아닌 view가 하나라도 있으면 해당 공유 재질의 인스턴싱을 끕니다. 마지막 마스크 view가 해제·비활성화되거나 None으로 바뀌면 다시 켭니다. 메시·GPU 조회 표·압축 텍스처는 계속 공유합니다. 마스크 장면의 draw call과 GPU 시간은 목표 기기에서 측정하십시오.

### Addressables와 AssetBundle

다음은 AssetBundle 로드부터 렌더 연결·해제까지의 컴포넌트 예제입니다. 파일명을 `AtlasBundleView.cs`로 저장합니다. GameObject에 추가한 뒤 Bundle Path, View, 인코더 두 개와 Conversion을 Inspector에서 지정합니다. Bundle Path에는 해당 플랫폼에서 접근 가능한 실제 파일 경로를 넣습니다. Android의 StreamingAssets가 APK 내부에 있으면 먼저 파일로 복사하거나 별도 번들 다운로드 로더를 사용합니다.

```csharp
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityCompute.TextureCompression;

public sealed class AtlasBundleView : MonoBehaviour
{
    [SerializeField] string bundlePath;
    [SerializeField] GpuAtlasRenderer view;
    [SerializeField] ComputeShader bc7Encoder, astcEncoder, conversion;
    GpuTextureAtlas atlas;

    async void Start()
    {
        GpuTextureAtlas loaded = null;
        try
        {
            if (view == null) throw new InvalidOperationException("Assign View.");
            bool mobile = Application.platform == RuntimePlatform.Android
                || Application.platform == RuntimePlatform.IPhonePlayer;
            TextureFormat format = mobile ? TextureFormat.ASTC_4x4 : TextureFormat.BC7;
            loaded = await LoadAsync(bundlePath, format,
                mobile ? astcEncoder : bc7Encoder, conversion);
            // 로드 중 파괴된 컴포넌트에는 결과를 연결하지 않습니다.
            if (this == null || view == null) { loaded.Dispose(); return; }
            atlas = loaded;
            view.Bind(atlas, atlas.Entries[0].SourceIndex);
            view.PixelsPerUnit = 100;
        }
        catch (Exception error)
        {
            loaded?.Dispose(); atlas = null;
            Debug.LogException(error);
        }
    }

    static async Task<GpuTextureAtlas> LoadAsync(string path, TextureFormat format,
        ComputeShader encoder, ComputeShader conversion)
    {
        var open = AssetBundle.LoadFromFileAsync(path);
        while (!open.isDone) await Task.Yield();
        var bundle = open.assetBundle;
        if (bundle == null) throw new IOException("Cannot load bundle: " + path);
        try
        {
            var request = bundle.LoadAllAssetsAsync<Texture2D>();
            while (!request.isDone) await Task.Yield();
            var textures = request.allAssets.Cast<Texture2D>().ToArray();
            return await GpuTextureAtlas.PackTexturesAsync(textures, format, true,
                encoder, conversion, GpuAtlasQuality.Fast,
                maxSize: 4096, paddingBlocks: 1, filterMode: FilterMode.Bilinear);
        }
        finally { bundle.Unload(true); }
    }

    void OnDestroy()
    {
        if (view != null) view.Unbind();
        atlas?.Dispose(); atlas = null;
    }
}
```

이 예제는 자신이 로드한 번들의 원본을 해제합니다. 다른 사용자가 같은 번들 자원을 공유한다면 중앙 로더의 참조 수 규칙에 맞춰 해제합니다. 컴포넌트를 로드 도중 파괴해도 로컬 번들 참조는 패킹이 끝날 때까지 유지합니다. 완료한 출력은 파괴된 컴포넌트에 연결하지 않고 Dispose합니다. 빈 텍스처 목록·지원하지 않는 형식·용량 초과 등은 예외로 처리합니다. `Start`의 await 중에도 Update는 실행되므로 소비자는 atlas가 준비된 뒤 동작하게 하십시오.

Addressables 패키지가 있는 프로젝트에서는 위 로드 함수를 다음 방식으로 바꿀 수 있습니다. 이 프로젝트에는 Addressables 의존성을 새로 설치하지 않았습니다. 패킹 코드는 Addressables 패키지에 의존하지 않습니다.

```csharp
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityCompute.TextureCompression;

async Task<GpuTextureAtlas> LoadAtlasAsync(string label,
    ComputeShader encoderShader, ComputeShader conversionShader)
{
    var handle = Addressables.LoadAssetsAsync<Texture2D>(label, null);
    try
    {
        Texture2D[] textures = (await handle.Task).ToArray();
        return await GpuTextureAtlas.PackTexturesAsync(textures,
            TextureFormat.BC7, true, encoderShader, conversionShader);
    }
    finally { Addressables.Release(handle); }
}
```

이 예제의 목표는 Windows BC7 sRGB입니다. 모바일에서는 목표를 `TextureFormat.ASTC_6x6` 등으로 바꾸고 ASTC 셰이더를 전달합니다. AssetBundle의 `LoadAllAssets<Texture2D>()` 또는 `LoadAssetAsync<Texture2D>()` 결과도 같은 API에 전달합니다. 핸들과 원본 텍스처는 비동기 패킹이 완료될 때까지 유지합니다. 반환된 아틀라스는 원본과 독립된 객체입니다. 원본의 다른 사용이 끝나면 로더의 수명 규칙에 따라 해제할 수 있습니다.

추가 로드도 같은 수명 규칙을 씁니다. 추가 원본 핸들을 유지한 상태에서 `int first = atlas.NextSourceIndex`를 저장하고 `await atlas.AddAsync(textures, encoder, conversion)`을 호출합니다. 성공하면 입력 i의 ID는 `first + i`입니다. Task가 정상 또는 예외로 끝난 뒤 추가 원본 핸들을 해제합니다. 소비자에는 `Bind(atlas, id)`로 ID만 전달합니다. 이후 `atlas.Remove(id)`로 숨기고 `await atlas.RepackAsync()`로 남은 항목을 재배치합니다. 앞의 [추가](#기존-gpu-아틀라스에-이미지-추가)·[제거와 재배치](#제거와-재배치)에 실패·빈 아틀라스의 동작이 있습니다.

GPU API는 Unity 메인 스레드에서 호출합니다. `Task.Run` 안에서 Pack·Add·Repack·Bind·Dispose나 Unity 객체 함수를 호출하지 마십시오. CPU는 크기·형식·배치 정보를 처리하며 원본 픽셀을 읽지 않습니다. CPU readback이나 전체 CPU RGBA 아틀라스는 없습니다. 출력 Texture2D를 만들 때 초기 CPU 저장소를 해제하며, 불일치 입력의 기존 GPU 업로더도 최초 투명 블록 초기화를 수행합니다. 초기화까지 CPU 작업이 전혀 없는 API는 아닙니다. 로딩 중 패킹용이며 반복 호출의 0GC나 프레임 시간 한도를 보장하지 않습니다.

명령 제출과 완료 확인을 나누려면 `PackTextures`를 호출한 뒤 `IsReady`를 폴링하거나 `WaitForCompletionAsync`를 기다립니다. 대기는 fence를 폴링하며 GPU를 동기로 기다리지 않습니다. 이 비동기는 완료 대기이며, 변환 dispatch를 여러 프레임으로 나누지는 않습니다. 중간 제출 오류까지 포함해 로더 수명을 관리하려면 위 예제의 `PackTexturesAsync`를 사용합니다. 정상 반환과 예외 반환 전에 제출한 GPU 작업이 끝날 때까지 기다립니다. `Dispose`는 마지막 소비자의 그리기 명령을 제출한 뒤 호출합니다. 네이티브 패킷·임시 버퍼·출력은 fence 뒤에 해제합니다.

### 문제 확인

| 증상 | 확인할 항목 |
|---|---|
| 이미지가 보이지 않음 | 초기 패킹 await, 올바른 활성 ID, 카메라 Culling Mask, 오브젝트 활성화, Size·PixelsPerUnit·카메라 위치 |
| SpriteMask가 적용되지 않음 | 카메라가 URP 2D Renderer 사용, Depth/Stencil Buffer 활성화, MaskInteraction, 마스크 Sprite·Alpha Cutoff·정렬 범위 |
| 9-slice 모서리까지 확대됨 | Transform 스케일 대신 Size 변경, Border 순서·픽셀 단위와 PixelsPerUnit 확인 |
| 삭제 뒤 다른 이미지가 표시됨 | `Entries[id]` 대신 `GetEntry(id)` 사용. 목록 위치와 고정 ID를 구별 |
| 재배치 뒤 수동 메시가 잘못 표시됨 | 새 Texture와 UV·UvClamp 갱신. GpuAtlasRenderer는 자동 처리 |
| 추가할 공간이 부족함 | 이전 갱신 await, Repack 후 재시도, maxSize·paddingBlocks·목표 기기 텍스처 한도 확인 |
| 형식·플러그인 오류 | 실제 graphicsFormat, 플랫폼별 번들, 그래픽 API와 네이티브 플러그인, 변환 셰이더의 빌드 포함 여부 확인 |
| alpha 주변 색이 섞임 | straight alpha 입력, 항목별 UvClamp 사용, 압축 블록 크기·품질 확인. GpuAtlasRenderer는 UvClamp 적용 |

### 출력과 샘플링

`maxSize`는 각 변의 최대 픽셀 수이며 1~16384입니다. 기기의 실제 한도는 `CreateTexture`에서 확인합니다. 배치는 MaxRects 휴리스틱을 사용합니다. 최소 면적을 보장하지 않습니다. 한 장에 들어가지 않으면 오류를 반환합니다. 입력 목록을 나누거나 최대 크기를 늘리십시오. 출력의 각 변은 압축 블록 크기의 배수입니다. 항상 2의 거듭제곱은 아닙니다.

`paddingBlocks`는 각 이미지 사방의 예약 블록 수이며 기본값은 0입니다. GPU 경로는 예약 영역과 빈 공간을 기존 유효 압축 블록의 복사로 채웁니다. 투명 여백이나 테두리 복제를 보장하지 않습니다. 그대로 복사하는 입력의 마지막 블록 안에 남는 픽셀도 바꾸지 않습니다. 출력은 선택한 밉 하나뿐이며 밉 체인을 자동 생성하지 않습니다.

GPU 출력은 Read/Write가 꺼진 Point·Clamp 텍스처입니다. `filterMode: FilterMode.Bilinear`로 변경할 수 있습니다. 복사가 끝난 뒤 `Apply`를 호출하지 마십시오. 이미지별 필터·랩 설정은 하나의 아틀라스에 개별 적용할 수 없습니다. Bilinear 사용 시 UV를 유효 픽셀 중심까지 제한해야 다른 이미지나 패딩 색이 섞이지 않습니다. `entry.MapUv`는 이 제한을 적용합니다. 셰이더에서는 `CompressedAtlasSampling.hlsl`의 `CompressedAtlasUv(localUv, entry.UvScaleOffset, entry.UvClamp)`을 사용합니다. 전역 TextureWrapMode.Clamp만으로는 이미지별 경계를 제한할 수 없습니다.

### CPU 바이트 경로 — 선택 기능

기존 `CompressedAtlas.PackTextures`는 Read/Write를 켠 같은 형식·색 공간의 입력에 사용합니다. `GetPixelData<byte>(mipLevel)`로 선택한 밉의 압축 바이트를 읽습니다. 이 NativeArray는 Dispose하지 않습니다. `FromTexture`는 압축 바이트를 관리 배열로 복사합니다. 이후 `CompressedAtlas.Pack(sources)`는 작업 스레드에서 실행할 수 있습니다. `CreateTexture`는 메인 스레드에서 호출합니다. CPU 경로의 빈 영역은 유효한 투명 검정 블록입니다. 같은 아틀라스에서 형식·색 공간을 섞지 않습니다.

이미 확보한 헤더 없는 블록 바이트는 `new CompressedAtlasSource(name, width, height, format, srgb, blocks)`로 입력합니다. 외부의 위쪽 행부터 저장된 데이터는 `CompressedRowOrder.TopToBottom`을 지정합니다. 이미지별 UV의 세로 부호로 방향을 처리하므로 블록 내부 비트를 수정하지 않습니다. `atlas.Data`는 헤더 없는 출력입니다. `atlas.ToFile()`은 ASTC 또는 BC7 DX10 DDS를 만듭니다. 배치 정보는 파일에 포함되지 않으므로 `Entries`를 별도로 보관합니다.

검산은 런타임과 분리합니다. GPU 검산은 Windows BC7의 Read/Write를 끈 TextureImporter→혼합 AssetBundle→로드→GPU 복사·변환→샘플링을 확인합니다. Player 검산은 첫 검산이 만든 번들을 사용합니다. Editor를 닫은 뒤 다음 명령을 실행합니다. 혼합 입력의 변환 검산에는 아래 네이티브 빌드 절차의 인코더 출력 플러그인이 필요합니다.

```powershell
pwsh -File .\Tools\Verify-GpuAtlas.ps1 -BatchMode -DebugGpu
pwsh -File .\Tools\Verify-GpuAtlasPlayer.ps1
```

`CopyTexture`만 따로 검산하려면 다음 명령을 사용합니다. 셰이더 인코더나 네이티브 출력 브리지를 호출하지 않습니다. 알려진 압축 블록으로 만든 Read/Write Off 입력을 사용합니다. 같은 블록 수를 가진 전체 밉 복사, 패딩 영역까지의 픽셀 보존, 추가·제거·확장·재배치와 빈 아틀라스 재사용을 대조합니다. 기기가 샘플링하지 못하는 압축 형식은 결과의 `skippedFormats`에 기록합니다.

```powershell
pwsh -File .\Tools\Verify-GpuAtlas.ps1 -BatchMode -CopyOnly -DebugGpu
pwsh -File .\Tools\Verify-GpuAtlas.ps1 -BatchMode -CopyOnly -GraphicsApi Vulkan
```

결과는 `.validation/gpu-atlas/copytexture/<GraphicsApi>/result.json`에 있습니다. 전체 D3D12 검산에도 이 검사를 포함합니다. Unity 6.7의 Windows D3D12와 Windows Vulkan에서 각각 BC7 선형·sRGB의 14개 사례, 부분 블록 입력 28개, 추가 42회와 재배치 28회를 통과했습니다. 각 API에서 원래 유효 영역 밖의 블록 texel을 포함한 363,488픽셀이 CPU 바이트로 만든 별도 참조 텍스처와 같았습니다. Android 13의 SM-A426N / Adreno 619 / Vulkan 1.1에서도 ASTC 4×4·5×5·6×6·8×8·10×10·12×12와 두 색 공간을 확인했습니다. 90개 사례, 부분 블록 입력 186개, 추가 270회, 재배치 180회를 통과했습니다. 패딩 영역을 포함한 9,351,774픽셀이 참조와 같았습니다. 이 기기에서 지원하지 않는 BC7 두 형식은 제외했습니다.

Android 실기기 검산에는 Unity의 Android Build Support·SDK·NDK·OpenJDK와 USB 디버깅을 승인한 ARM64 기기가 필요합니다. Editor를 닫고 다음 명령을 실행합니다. 첫 명령은 Vulkan·IL2CPP 검산 APK를 만듭니다. 둘째 명령은 전용 앱 `com.ittodo.gputextureatlas.validation`을 설치하고 실행합니다. 기기가 여러 대면 `-Serial`로 대상을 지정합니다.

```powershell
pwsh -File .\Tools\Build-GpuAtlasAndroidProbe.ps1
pwsh -File .\Tools\Verify-GpuAtlasAndroid.ps1
```

이 앱은 지원하는 ASTC 블록 크기와 두 색 공간의 복사·편집을 확인합니다. ASTC 4×4의 세 품질·두 색 공간 변환 결과를 별도 블록 업로드 참조와 대조합니다. Read/Write를 끈 Android AssetBundle 입력으로 9-slice·SpriteMask도 검사합니다. 비교용 CPU readback은 검산 코드에만 있습니다. 전체 실행 시간은 검산·프레임 대기를 포함하므로 압축 속도 지표로 사용하지 않습니다. APK와 빌드 로그는 `.validation/gpu-atlas/android/`에 저장합니다. 기기별 실행 보고서·PNG·앱 로그는 그 아래 `device-<실행 시각>/`에 저장합니다. 검사 뒤 전용 앱은 기기에 남습니다.

2026-10-23~24의 최신 Windows D3D12·Android Vulkan 검산에는 공유 프레임 예산도 포함했습니다. 각 플랫폼에서 두 색 공간과 세 품질의 6개 조합, 148,629픽셀 대조, 취소 24회, 작업 중 해제 6회를 통과했습니다. 동시 작업의 프레임별 최대 제출은 설정한 복사 32블록·압축 5블록·변환 136픽셀 이내였습니다. 별도 부분 블록 입력은 whole-mip 복사의 예산 초과 예외 1회도 확인했습니다. Add·Repack의 완료 전 게시 방지와 취소 후 이전 상태 유지도 대조했습니다.

2026-10-23에 SM-A426N / Android 13 / Adreno 619 / Vulkan 1.1에서 전체 검산을 통과했습니다. 렌더러는 URP Forward·2D와 두 색 공간에서 122프레임·562,176픽셀을 확인했습니다. 9-slice 86프레임과 SpriteMask 24프레임을 포함하며 최대 채널 차이는 약 1.56/255입니다. 기준 오차는 Windows와 같은 2/255입니다. 최종 실행 로그에는 Unity 오류·경고가 없었습니다. 이 결과는 해당 기기와 드라이버에 한정합니다.

Android 네이티브 출력은 SM-A426N에서 ASTC 4×4의 세 품질·두 색 공간을 통과했습니다. 총 7,680픽셀이 CPU로 같은 블록을 업로드한 별도 참조와 같았습니다. 이 비교는 출력 전달의 정확성을 확인하며 압축 품질·속도 측정을 대신하지 않습니다.

픽셀 검산은 URP Render Scale을 실행 중에만 1로 맞춘 뒤 원래 값으로 복원합니다. 모바일 기본값 0.8의 업스케일 필터가 CPU의 1:1 참조와 섞이지 않게 합니다. 프로젝트의 렌더 배율은 바꾸지 않습니다. 예제 프로젝트에서 AI Assistant·Inference 의존성을 제거했습니다. 이전 App UI 시작 오류를 피하기 위한 검산 빌드의 `APP_UI_EDITOR_ONLY` 설정도 유지합니다. 기본 PC 렌더러의 불필요한 SSAO 기능을 제거하여 검산 Player에서 해당 리소스 오류가 나지 않게 했습니다.

Unity 6.7 D3D12에서 읽기 불가 BC7·RGBA32·DXT5 입력 10개를 두 색 공간과 세 품질로 확인했습니다. 아틀라스 6개에서 입력 18개는 그대로 복사했고 42개는 변환했습니다. 부분 블록 검산까지 포함해 직접 복사 경로 71,793픽셀과 독립 변환 참조 110,052픽셀이 일치했습니다. half 반올림도 독립 CPU 참조와 일치했습니다. 작은 밉의 아틀라스 3개, 원본 번들 해제 후 출력 유지, 출력 9개의 해제를 확인했습니다. 변환 커널은 D3D12·Android Vulkan/GLES3·iOS Metal에서 진단 없이 코드 생성을 통과했습니다.

추가 검산은 ASTC·BC7 블록 크기에 해당하는 6개 크기와 두 여백 설정의 배치 12건을 확인했습니다. GPU에서는 두 색 공간·세 품질의 아틀라스 6개에 24회 추가했습니다. 확장 18회와 기존 공간 사용 6회를 통과했습니다. 직접 복사 82,170픽셀과 독립 변환 참조 48,780픽셀의 차이는 0/255였습니다. 최초 원본 번들 해제, 기존 위치·UV 갱신, 부분 밉 추가, 중복 이름, 잘못된 입력·동시 요청·작업 중 Dispose의 거절 55건과 이후 재사용을 확인했습니다. 결과는 `.validation/gpu-atlas/addition.json`에 있습니다.

제거·재배치는 두 색 공간·세 품질의 6개 사례에서 항목 제거 84개와 재배치 30회를 통과했습니다. 삭제한 자리의 재사용 6회와 실제 좌표 이동 30개를 확인했습니다. 부분 블록을 포함해 450,720픽셀과 Bilinear UV 경계 샘플 476,244개가 이전 결과와 같았습니다. 원본 번들 해제 후 재배치, 고정 ID·분류 유지, 빈 아틀라스 재충전, 반복 배치의 결정성, 동시 요청·작업 중 Dispose를 포함한 거절 43건을 확인했습니다. 결과는 `.validation/gpu-atlas/editing.json`에 있습니다.

전용 렌더러는 Editor와 Windows Player에서 URP Forward·2D 및 선형·sRGB의 4개 조합을 확인합니다. 실제 카메라 출력을 독립 CPU 샘플링·알파 합성 결과와 비교합니다. 반전·피벗·회전·색상·정렬 순서, 공유 자원의 비활성화·재활성화·Unbind, 원본 해제 뒤 추가·재배치, 큰 ID와 없는 ID의 GPU 숨김, 빈 아틀라스·Dispose를 확인합니다. 9-slice의 비대칭 테두리·축소·0 크기·초과 테두리·원본 크기·반전과 피벗을 확인합니다. 실제 SpriteMask의 안팎 표시·알파 기준·이동·활성화·정렬 범위, 서로 다른 MaskInteraction의 동시 사용과 재배치 후 9-slice·마스크 결합을 확인합니다. 마스크 view의 활성 상태에 따라 인스턴싱이 자동 전환되는 것도 확인합니다.

Editor와 Player 각각 최종 122프레임·562,176픽셀을 통과했습니다. 이 중 9-slice 설정은 86프레임, SpriteMask 대조는 24프레임입니다. 최대 채널 차이는 약 1.60/255이며 Bilinear·알파 블렌드·half 출력의 수치 차이를 위해 2/255 한도를 적용합니다. D3D12·Android Vulkan/GLES3·iOS Metal에서 인스턴싱 유무와 세 렌더 패스의 셰이더 단계·변형 36개가 컴파일을 통과했습니다. Editor 렌더러 결과와 Simple·Sliced·마스크 안팎·재배치 PNG는 `.validation/gpu-atlas/renderer/`에 있습니다. Player 렌더러 결과는 `.validation/gpu-atlas/player/renderer/`에 있습니다. README의 `AtlasBundleView` 예제도 현재 Unity 어셈블리를 참조해 C# 컴파일을 통과했습니다.

별도 Windows Player는 읽기 불가 입력 10개를 3개 복사·7개 변환으로 처리했습니다. 직접 복사 10,752픽셀과 원본 번들 해제·GPU 재패킹 후 65,536픽셀이 일치했습니다. 추가 6회는 확장 5회와 기존 공간 사용 1회입니다. 변환 입력과 원본 해제 후 GPU 아틀라스 추가를 포함한 84,118픽셀이 참조와 같았습니다. 항목 제거 8개와 재배치 3회를 실행했고 재추가·빈 아틀라스 재사용을 포함한 272,482픽셀과 UV 좌표가 일치했습니다. 프레임 예산을 포함한 최신 Player 검산에서도 대기 중인 자원 해제는 0개였습니다. GPU 결과는 `.validation/gpu-atlas/result.json`, Player 결과는 `.validation/gpu-atlas/player/result.json`에 있습니다. 이 Player 검산은 AssetBundle을 사용합니다. 실제 Addressables 빌드는 아래 별도 패키지 검산에서 확인합니다. Android는 위 실기기 검산으로 확인합니다. iOS 네이티브 소스는 macOS·Xcode 컴파일과 기기 검증이 남아 있습니다.

CPU 경로는 다음 명령으로 별도 검산합니다.

```powershell
pwsh -File .\Tools\Verify-CompressedAtlas.ps1
# Editor가 닫혀 있으면 D3D12 배치 검산을 사용할 수 있습니다.
pwsh -File .\Tools\Verify-CompressedAtlas.ps1 -BatchMode
```

현재 검산은 7개 형식·두 색 공간·두 여백 설정에서 28개 아틀라스와 560개 입력을 통과했습니다. 크기가 다른 입력과 1픽셀·부분 블록을 포함합니다. 3,678,432바이트가 원본과 같았고 배치 겹침·UV·빈 블록·반복 배치·잘못된 입력 12개를 확인했습니다. 실제 번들 검산은 기본 TextureImporter의 32×16·64×32·128×64 BC7 텍스처를 두 색 공간으로 빌드했습니다. 로드한 6개 텍스처에서 첫째·둘째 밉의 아틀라스 4개를 만들었습니다. 첫째 밉의 GPU 디코딩 21,504픽셀이 원본 디코딩과 같았습니다. 원본 번들 해제 뒤 출력 유지도 확인했습니다. 결과는 `.validation/compressed-atlas/`에 있습니다.

## ASTC 인코더의 지원 범위

GPU가 한 밉 레벨의 LDR RGBA를 ASTC로 압축합니다. 4×4·5×5·6×6·8×8·10×10·12×12 정사각 블록을 지원합니다. 블록은 16바이트입니다. 가장자리의 남은 픽셀은 마지막 유효 픽셀로 채웁니다.

BC7 GPU 인코더도 제공합니다. BC7은 PC용 4×4 형식이며 8bpp입니다. Android·iOS용 경로는 ASTC입니다. BC7 사용법과 제한은 아래 ‘BC7 GPU 압축’에 있습니다.

`Fast`는 자주 압축하는 용도입니다. `Balanced`는 더 많은 격자와 끝점 보정을 탐색합니다. `HighQuality`는 Balanced 후보에 16개 가중치 모드, 추가 초기 끝점, RGB 채널별 분리 평면을 더합니다. 끝점을 4회 보정하고 격자 노드가 영향을 주는 픽셀로 가중치를 보정합니다. 세 품질은 단일 분할, RGB 직접 끝점·RGB scale 끝점·RGBA 직접 끝점을 사용합니다. 끝점은 ASTC 비트·trit·quint 정수열로 저장합니다. 생성 표의 기준은 Arm astcenc 5.7.0입니다.

ASTC의 HDR, 다중 분할, 밉 생성, 노멀 맵 전용 최적화는 없습니다. 입력은 0~1로 제한됩니다. astcenc의 전체 탐색과 같은 품질을 제공하지 않습니다. 알파 UI는 4×4부터 검토하십시오. 6×6에서는 가는 경계와 독립적인 RGB·알파 변화의 손실이 큽니다. HighQuality는 처리 비용이 큽니다. 매 프레임 사용 여부는 목표 기기의 시간·발열 측정으로 판단합니다.

Windows에서 실제 GPU 인코딩과 공식 디코더 대조를 완료했습니다. Android ASTC 4×4의 시간과 반복 편집 결과는 [모바일 성능](#android-모바일-성능)을 따릅니다. **iOS 실기기 실행은 미검증이며 네이티브 텍스처 업로드는 실험 기능입니다.** 모바일 셰이더 코드 생성과 기기 실행은 서로 다른 검증입니다.

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

iOS에는 .mm와 Unity 인터페이스 헤더를 복사합니다. `-SkipAndroidBuild`로 iOS 소스만 설치할 수 있습니다. macOS·Xcode에서 컴파일해야 합니다. 현재 환경에는 iOS SDK가 없습니다. Android ARM64 Vulkan은 위의 실기기 검산을 따릅니다. iOS의 네이티브 복사·알파 표시·방향·색 공간·반복 실행·자원 해제는 미검증입니다.

## 색 공간과 알파

`srgb`는 출력 RGB의 색 공간입니다. 알파는 항상 선형 값입니다. 입력은 straight RGBA로 취급합니다. premultiplied 입력을 자동 변환하지 않습니다.

ASTC·BC7의 `EncodeAsync`는 Edit Mode에서 읽기가 완료될 때까지 Editor 갱신과 Scene View 재그리기를 요청합니다. 6.7에서 유휴 화면의 GPU 제출이 늦어지는 경우를 처리합니다. GPU 완료를 동기로 기다리지 않습니다. Player의 비동기 읽기 경로와 `EncodeFrame`에는 이 Editor 처리가 없습니다.

`sampledInputIsSrgb`는 **GPU가 읽은 뒤의 RGB 값**이 sRGB인지 지정합니다. 기본값은 false입니다. Linear 프로젝트에서 sRGB 텍스처를 읽으면 GPU가 이미 선형 값으로 바꾸므로 기본값을 사용합니다. Gamma 프로젝트에서 sRGB 코드 값을 그대로 읽으면 true를 전달합니다. 선형 데이터와 선형 RenderTexture에는 false를 사용합니다.

데스크톱에서도 컴퓨트 지원이 있으면 압축과 내보내기는 가능합니다. 결과 표시에는 해당 ASTC 형식의 샘플링 지원이 필요합니다.

## 여러 프레임에 나누는 압축

`AstcGpuTexture`와 `Bc7GpuTexture`에 `BeginEncode`·`EncodeNextFrame`을 제공합니다. 행보다 작은 예산에는 `EncodeNextFrameBlocks(maxBlocks)`를 사용합니다. 두 Step API는 같은 작업 상태를 사용합니다. 출력은 마지막 블록을 처리한 뒤 게시합니다. 시작할 때 품질과 색 공간 설정을 고정합니다. 이후 Update마다 일부 블록 행을 제출합니다. 앞선 GPU fence가 남아 있으면 이번 호출은 제출을 미룹니다. CPU에서 GPU 완료를 기다리지 않습니다. Player에서는 같은 프레임에 여러 번 호출해도 압축 묶음은 최대 한 번 제출합니다.

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

블록 행 수는 엄격한 ms 제한이 아닙니다. 품질·입력 내용·기기에 따라 한 묶음의 비용이 달라집니다. 스냅샷과 마지막 텍스처 복사도 GPU 작업입니다. 단독 인코더의 예산은 출력마다 적용됩니다. 아틀라스 API에서는 같은 `GpuAtlasFrameBudget`을 전달하여 전체 제출량을 합산할 수 있습니다. [프레임 분산 비교](Benchmark/Results/frame-sliced/report.html)는 알파 UI·비알파 사진 각 1장의 1024 입력에서 모든 행 묶음을 측정합니다. 일괄 압축과 4행·32행 묶음을 비교합니다. CPU 완료 읽기를 일부 포함한 처리량입니다. GPU timestamp·전체 프레임 지연·모바일 기기 시간은 아닙니다.

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

플러그인 빌드에는 MSVC x64와 Windows SDK가 필요합니다. 기본 Editor 버전은 프로젝트의 `ProjectVersion.txt`에서 읽습니다. 설치 경로는 Unity CLI의 Editor 목록에서 찾습니다. 자동 탐색이 안 되면 `-UnityEditor`에 `Unity.exe`가 있는 폴더를 지정합니다. MSVC와 Windows SDK의 경로는 `-VisualStudio`, `-WindowsSdk`로 지정합니다. DLL은 `Packages/com.ittodo.gpu-texture-atlas/Plugins/Bc7/Windows/x86_64/`에 설치합니다.

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

패키지 설치와 실제 Addressables 수명 검산:

```powershell
pwsh -File Tools/Verify-GpuAtlasPackage.ps1
```

이 도구는 `.validation/gpu-atlas/package-consumer-*`에 별도 프로젝트를 만듭니다. 로컬 UPM 패키지와 [Addressables 4.1.1](https://docs.unity3d.com/Packages/com.unity.addressables@4.1/manual/index.html)을 설치하고 콘텐츠와 Windows D3D12 Player를 빌드합니다. 예제 프로젝트의 AI·URP 의존성을 가져오지 않습니다. 2026-10-23 검산은 8회 반복, 입력 16개 해제, 430,080픽셀 대조, 씬 종료 중 작업 중단 1회, 샘플 렌더러 4개 생성을 통과했습니다. 이 검산은 로컬 Addressables 콘텐츠를 사용하며 원격 CDN 동작은 포함하지 않습니다.

모바일 성능·반복 편집 측정 도구:

```powershell
pwsh -File Tools/Build-GpuAtlasMobileBenchmark.ps1
pwsh -File Tools/Run-GpuAtlasMobileBenchmark.ps1 -Repetitions 1 -StressSeconds 600 -HighQualityEncodeBlocks 32
node Tools/Report-GpuAtlasMobileBenchmark.mjs <result.json 경로>
```

세 해상도의 알파 10장·불투명 10장, 세 압축 품질, Copy·Add·Repack을 측정합니다. 기본 측정의 예열과 입력 로딩은 기록 시간에서 제외합니다. CPU 호출 시간, 완료 지연, 진행 중 프레임 분포를 따로 기록합니다. FrameTimingManager의 GPU 수치는 뒤의 8프레임까지 포함한 앱 전체 값입니다. 압축 커널 시간으로 해석하지 않습니다. 그래픽 메모리가 계속 0이면 조회 불가입니다. 호출별 관리 힙 할당은 알려진 배열을 한 번 할당해 카운터를 확인한 뒤 기록합니다. Unity 6000.7.0b5의 IL2CPP 카운터는 0을 고정 반환하므로 N/A로 표시합니다. 이를 0GC로 해석하지 않습니다. 배터리 온도는 GPU 온도가 아닙니다. 검산과 측정 파일 쓰기는 패키지 런타임에 포함하지 않습니다.

`-OnlyQuality`, `-OnlyGroup`, `-OnlyScheduling`, `-Resolution`, `-InputLimit`는 실패 재현용 필터입니다. `-EncodeBlocks`와 `-HighQualityEncodeBlocks`로 압축 예산을 정합니다. 측정 도구의 기본값은 각각 128과 16블록입니다. 현재 기기에서 HighQuality 전체 제출 중 GPU 종료가 발생했으므로 기본 측정에서 해당 경로는 제외하고 사유를 기록합니다. `-IncludeHighQualityImmediate`는 그 경로의 명시적 재현용 옵션입니다.

`-MaxImmediateResolution`의 기본값은 256입니다. 그보다 큰 입력은 전체 이미지 변환 제출을 측정하지 않습니다. 이 크기 제한은 측정 범위이며, 제외한 모든 경로에서 오류를 관찰했다는 뜻은 아닙니다. 분산 변환과 압축 블록 Copy·Add·Repack은 세 해상도를 모두 측정합니다. 입력별 1회 실행이면 580개 측정과 140개 제외 사유를 기록합니다. 필터 실행은 전체 측정으로 집계하지 않습니다. 앱이 종료되면 측정 도구가 실패를 보고하고 남은 기록과 로그를 보존합니다.

측정 중에는 앱을 전면에 유지합니다. PC의 대기 시간이 끝나도 기기 프로세스와 결과 파일은 남습니다. `-ResumeRun <출력된 device 폴더> -TimeoutSeconds 10800`으로 같은 실행의 수집을 이어갈 수 있습니다. 이 옵션은 저장된 실행 ID와 프로세스를 확인하며 APK를 재설치하지 않습니다. 측정 도중 백그라운드로 전환되면 해당 완료 시간에 중단 시간이 포함됩니다. 그 표본은 다시 측정해야 합니다. 반복 편집 단계의 의도적인 백그라운드 검사는 별도로 수행합니다.

보고서 생성기는 APK에 포함된 입력 manifest와 저장소의 PNG 60개 해시를 대조합니다. 계획한 결과의 누락·중복과 각 그룹의 표본 수도 확인합니다. APK·빌드 소스·원본 결과의 해시를 보고서에 보존합니다. 이 검산은 보고서 도구에서만 실행합니다.

중단된 표본을 재측정한 경우 보고서 명령의 세 번째 인자로 교체 명세 JSON을 전달할 수 있습니다. 명세는 `originalSha256`과 `replacements` 목록을 사용합니다. 각 항목은 `resultFile`, `reason`, `id`를 지정합니다. `id`에는 `resolution`, `group`, `texture`, `operation`, `quality`, `scheduling`, `repetition`을 넣습니다. `resultFile`은 명세 파일 기준 경로입니다. 생성기는 같은 APK·기기·입력·예산인지 확인하고, 일시 중단이 없는 재측정의 지정 행만 사용합니다. 보고서에는 교체 사유와 교체 전 원본·재측정 JSON을 함께 보존합니다.

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

## 개발 환경과 실행

이 저장소는 Unity 프로젝트 형태로 제공합니다. Unity Hub에서 `compute/`를 열고 패키지 임포트가 끝날 때까지 기다립니다. Windows의 GPU 텍스처 경로는 Direct3D12를 사용합니다. Unity CLI를 설치한 환경에서는 다음 명령을 사용할 수 있습니다. 실행 파일 경로는 설치 위치에 맞춰 바꿉니다.

```powershell
& 'C:\Program Files\Unity Hub\resources\unity.exe' open .\compute --editor-version 6000.7.0b5 --args '-force-d3d12'
```

| 항목 | 프로젝트 설정 |
|---|---|
| Unity Editor | 6000.7.0b5 |
| URP | 17.7.0 |
| UGUI | 2.7.0 |
| Input System | 6.7.0 |
| Unity Pipeline | 0.7.0-exp.1 |
| Windows 스크립팅 백엔드 | Mono |
| Android 스크립팅 백엔드 | IL2CPP |

CoreCLR로 전환하지 않았습니다. Pipeline 0.8.0-exp.1은 CLI 속성 어셈블리 분리와 이 프로젝트의 SRP 참조가 맞지 않아 사용하지 않습니다. 이 버전 조건은 `compute/Packages/manifest.json`에 반영돼 있습니다.

검산 도구의 기본 Editor 경로는 `.tooling/Unity/6000.7.0b5/Editor/Unity.exe`입니다. 이 폴더는 Git에 포함하지 않습니다. 다른 위치에 설치한 Editor를 사용하면 도구에 `-UnityEditor`를 전달합니다. GPU 아틀라스 검산 도구에는 `Unity.exe` 파일 경로를 지정합니다. CLI 기반 도구의 실행 파일은 `-UnityCli`로 지정합니다. 네이티브 빌드 도구의 인자는 해당 절차를 따릅니다.

마이그레이션 복구본과 검산 기록의 로컬 저장 위치는 `.validation/migration-6000.7.0b5/`입니다. 이 폴더도 Git에 포함하지 않습니다.

## 파일 안내

| 경로 | 역할 |
|---|---|
| `Packages/com.ittodo.gpu-texture-atlas/Shaders/Astc/AstcEncoder.compute` | Fast·Balanced·HighQuality 인코딩 |
| `Packages/com.ittodo.gpu-texture-atlas/Shaders/Astc/AstcTables.hlsl` | GPU 표 접근 |
| `Packages/com.ittodo.gpu-texture-atlas/Runtime/Astc/AstcLookup.cs` | 생성된 GPU 표 데이터 |
| `Packages/com.ittodo.gpu-texture-atlas/Runtime/Astc/AstcEncoder.cs` | 동기·비동기 읽기, 재사용 GPU 버퍼와 행 범위 |
| `Packages/com.ittodo.gpu-texture-atlas/Runtime/Astc/AstcImage.cs` | 데이터·텍스처·파일 포장 |
| `Packages/com.ittodo.gpu-texture-atlas/Runtime/Astc/AstcGpuTexture.cs` | 실험용 모바일 GPU 텍스처 갱신 |
| `Packages/com.ittodo.gpu-texture-atlas/Runtime/Astc/AstcGpuStream.cs` | 매 프레임 사용 예제 |
| `Packages/com.ittodo.gpu-texture-atlas/Runtime/Astc/AstcGpuRetirement.cs` | GPU 완료 후 자원 해제 |
| `compute/Assets/Astc/Editor/AstcEncoderWindow.cs` | 내보내기와 기본 예제 씬 |
| `compute/Assets/Astc/Editor/AstcValidation.cs` | 런타임과 분리된 검산 |
| `compute/Assets/Astc/Editor/Benchmark/` | 원래 구현·스칼라 디코더·비교 측정 |
| `compute/Assets/Astc/Editor/Benchmark/AstcPeakMetrics.cs` | RGB·알파·합성의 피크 오차와 최악 블록 계산 |
| `Packages/com.ittodo.gpu-texture-atlas/Shaders/Bc7/Bc7Encoder.compute` | BC7 세 품질의 진입 커널 |
| `Packages/com.ittodo.gpu-texture-atlas/Shaders/Bc7/Bc7HighQuality.hlsl` | 2분할 후보 선택·모드 1·3·7 탐색 |
| `Packages/com.ittodo.gpu-texture-atlas/Shaders/Bc7/Bc7Partitions.hlsl` | Microsoft 참조의 64개 분할·anchor 표 |
| `compute/Assets/Astc/Editor/Benchmark/HighQualityBenchmark.cs` | 6.7에서 세 품질을 같은 조건으로 비교 |
| `Packages/com.ittodo.gpu-texture-atlas/Runtime/Bc7/` | BC7 버퍼·동기·비동기 API, 텍스처와 DDS 포장 |
| `Packages/com.ittodo.gpu-texture-atlas/Runtime/Bc7/Bc7GpuTexture.cs` | Windows D3D12의 GPU 버퍼→BC7 텍스처 갱신 |
| `Packages/com.ittodo.gpu-texture-atlas/Runtime/Bc7/Bc7GpuStream.cs` | 매 프레임 압축·표시 컴포넌트 |
| `compute/Assets/Bc7/Editor/` | 내보내기, 별도 디코더, 하드웨어 대조와 비교 측정 |
| `Tools/Run-Bc7Benchmark.ps1` | 같은 60개 입력의 GPU·Unity BC7 비교 |
| `Tools/Run-HighQualityBenchmark.ps1` | 세 해상도의 540개 GPU 비교 |
| `Packages/com.ittodo.gpu-texture-atlas/Runtime/Atlas/FrameSlicedGpuJob.cs` | GPU 스냅샷·프레임별 제출·fence 진행 관리 |
| `Packages/com.ittodo.gpu-texture-atlas/Runtime/Atlas/GpuAtlasFrameBudget.cs` | 동시 아틀라스 작업의 프레임별 복사·변환·압축 예산 |
| `Packages/com.ittodo.gpu-texture-atlas/Runtime/Atlas/GpuTextureAtlas.Scheduling.cs` | 분할 제출·입력 정규화·취소 확인 |
| `Packages/com.ittodo.gpu-texture-atlas/Runtime/Atlas/CompressedAtlas.cs` | 압축 Texture2D·블록 바이트의 아틀라스 패킹과 텍스처 생성 |
| `Packages/com.ittodo.gpu-texture-atlas/Runtime/Atlas/CompressedAtlasSource.cs` | GetPixelData로 선택한 밉의 압축 바이트 읽기 |
| `Packages/com.ittodo.gpu-texture-atlas/Runtime/Atlas/CompressedAtlasSampling.hlsl` | 이미지별 UV 변환과 유효 픽셀 경계 제한 |
| `Packages/com.ittodo.gpu-texture-atlas/Runtime/Atlas/BlockAtlasLayout.cs` | CPU·GPU 경로가 공유하는 블록 배치 계산 |
| `Packages/com.ittodo.gpu-texture-atlas/Runtime/Atlas/GpuTextureAtlas.cs` | Read/Write 없는 직접 복사와 다른 입력만 GPU 변환 |
| `Packages/com.ittodo.gpu-texture-atlas/Runtime/Atlas/GpuTextureAtlas.Addition.cs` | 기존 위치를 유지하는 추가·확장과 공통 GPU 입력 처리 |
| `Packages/com.ittodo.gpu-texture-atlas/Runtime/Atlas/GpuTextureAtlas.Editing.cs` | ID 조회·제거·남은 압축 블록의 GPU 재배치 |
| `Packages/com.ittodo.gpu-texture-atlas/Runtime/Atlas/GpuAtlasRenderer.cs` | 고정 ID 렌더러·9-slice·SpriteMask·크기·색상·반전·정렬 |
| `compute/Assets/Settings/GpuAtlas2D_Renderer.asset` | SpriteMask용 URP 2D Renderer. PC·Mobile 파이프라인의 Renderer 목록 1번 |
| `Packages/com.ittodo.gpu-texture-atlas/Runtime/Atlas/GpuAtlasRenderState.cs` | 아틀라스별 공유 재질·격자 메시·GPU ID 조회 표와 자원 수명 |
| `Packages/com.ittodo.gpu-texture-atlas/Resources/GpuAtlasUnlit.shader` | Built-in·URP Forward·Universal2D의 Unlit 렌더 패스 |
| `Packages/com.ittodo.gpu-texture-atlas/Resources/GpuAtlasRender.hlsl` | 정수 ID 해시 조회·유효 여부·UV 경계 제한 |
| `Packages/com.ittodo.gpu-texture-atlas/Shaders/Atlas/GpuAtlasConvert.compute` | 선형 RGB로 읽기·목표 블록 크기까지 가장자리 채우기 |
| `compute/Assets/TextureCompression/Editor/GpuAtlasCopyValidation.cs` | CopyTexture의 부분 블록·밉·추가·재배치 검산 |
| `Tools/Build-GpuAtlasAndroidProbe.ps1`, `Tools/Verify-GpuAtlasAndroid.ps1` | Android Vulkan 검산 앱 빌드·설치·실행·보고서 수집 |
| `Verification/GpuAtlasAndroidProbe.cs` | ASTC 실기기 복사·변환·렌더러 검산 앱 |
| `Tools/Verify-GpuAtlas.ps1` | 읽기 불가 혼합 번들·색 공간·GPU 복사·변환·해제 검산 |
| `compute/Assets/TextureCompression/Editor/GpuAtlasAdditionValidation.cs` | 추가·빈 공간 재사용·확장·실패·작업 중 해제 검산 |
| `compute/Assets/TextureCompression/Editor/GpuAtlasEditingValidation.cs` | 제거·자리 재사용·재배치·빈 아틀라스·고정 ID 검산 |
| `compute/Assets/TextureCompression/Editor/GpuAtlasRendererValidation.cs` | 실제 카메라 출력·독립 CPU 참조·플랫폼 셰이더·Player 공통 검산 |
| `Tools/Verify-GpuAtlasPlayer.ps1` | 별도 Windows Player의 GPU 패킹·재패킹·로더 수명 검산 |
| `Verification/GpuAtlasPlayerProbe.cs` | 일반 런타임과 분리된 Player 검산 소스 |
| `Verification/GpuAtlasSchedulingValidation.cs` | 동시 예산·부분 블록·취소·작업 중 해제 검산 |
| `Tools/Verify-GpuAtlasPackage.ps1` | 별도 프로젝트에서 UPM·Addressables·샘플 검산 |
| `Tools/Build-GpuAtlasMobileBenchmark.ps1`, `Tools/Run-GpuAtlasMobileBenchmark.ps1` | Android 성능·반복 편집 측정 앱 빌드와 실행 |
| `Tools/Report-GpuAtlasMobileBenchmark.mjs` | 전체 표본·제외 사유 확인과 모바일 보고서 생성 |
| `Packages/com.ittodo.gpu-texture-atlas/Samples~/DynamicAtlas/` | Package Manager에서 설치하는 조작 가능한 샘플 |
| `Tools/Verify-CompressedAtlas.ps1` | 블록 보존·배치·UV·TextureImporter·AssetBundle 검산 |
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
| `Packages/com.ittodo.gpu-texture-atlas/Plugins/Astc/` | Unity에 설치한 네이티브 산출물 |
| `Benchmark/Input/manifest.json` | 입력 60개의 크기·알파 통계·SHA-256 |
| `Benchmark/Sources/sources.json` | 원본 출처·라이선스·SHA-256 |
| `Benchmark/Results/report.html` | 현재 비교 보고서 |

## Git 기록 범위

소스·Unity 프로젝트 설정·입력 원본과 라이선스·측정 JSON/CSV·HTML 보고서를 기록합니다. Unity 캐시(`compute/.utmp/` 포함), 설치 도구, 임시 검산, Native/build와 벤치마크의 생성 PNG·ASTC·BC7 블록은 제외합니다. 파일은 로컬에 유지합니다. 새 체크아웃에서 복원 이미지 패널을 보려면 벤치마크를 다시 실행합니다. 측정 manifest의 SHA-256을 보존하도록 Git의 줄바꿈 자동 변환을 끕니다.

## 근거와 라이선스

이 프로젝트에서 직접 작성한 코드와 문서는 [MIT License](LICENSE)로 배포합니다. 상업적 사용·수정·재배포를 허용합니다. 해당 코드를 배포할 때는 저작권 고지와 라이선스 본문을 함께 유지해야 합니다.

포함된 외부 코드·생성 표·SDK 헤더·이미지에는 각 자료의 원래 라이선스가 적용됩니다. 아래 라이선스 안내와 각 파일의 SPDX·저작권 고지를 따릅니다.

ASTC 형식은 [Arm 설명](https://github.com/ARM-software/astc-encoder/blob/5.7.0/Docs/FormatOverview.md)과 [5.7.0 참조 구현](https://github.com/ARM-software/astc-encoder/tree/5.7.0)을 확인했습니다. BC7은 Microsoft의 [형식 설명](https://learn.microsoft.com/en-us/windows/win32/direct3d11/bc7-format)과 [모드별 비트 배치](https://learn.microsoft.com/en-us/windows/win32/direct3d11/bc7-format-mode-reference)를 기준으로 구현했습니다. 생성 표와 Arm 이미지는 [Apache 2.0](ThirdParty/astcenc/LICENSE.txt), Khronos 이미지는 [MIT](Benchmark/Sources/Khronos-LICENSE.txt)입니다. 사진 출처는 [sources.json](Benchmark/Sources/sources.json)에 있습니다.

Unity 헤더는 [Unity Companion License](Native/Unity/LICENSE.md)입니다. Vulkan 헤더는 v1.3.275이며 [라이선스 안내](Native/Vulkan/LICENSE.md)와 원문을 포함했습니다. 플랫폼 조건은 [Unity 컴퓨트 셰이더](https://docs.unity3d.com/6000.0/Documentation/Manual/class-ComputeShader-introduction.html)와 [GPU 텍스처 형식](https://docs.unity3d.com/6000.0/Documentation/Manual/texture-formats-reference.html)을 참조합니다.

BC7 2분할 패턴·anchor 표는 Microsoft DirectXTex의 [고정 리비전 기록](ThirdParty/DirectXTex/source.json)을 사용합니다. [MIT 라이선스](ThirdParty/DirectXTex/LICENSE.txt)를 포함했습니다. 표를 다시 만들려면 해당 `BC6HBC7.cpp`와 리비전을 `.validation/reference/DirectXTex/`에 두고 `Tools/Generate-Bc7Tables.ps1`을 실행합니다. 생성 후 192개 하드웨어 대조를 실행합니다.
