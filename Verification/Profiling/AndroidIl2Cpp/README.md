# Android IL2CPP 클래스별 할당 추적

Unity **6000.7.0b5**, Android ARM64 IL2CPP, Vulkan에서 동작하는 진단용 수집기다.
2026-10-24에 **SM-A426N / Adreno 619 / Android 13**에서 검산했다.
클래스별 생성 개수와 **GC 이후 약한 참조가 소실된 개수**를 기록한다.
물리적인 객체 메모리 반환 개수는 제공하지 않는다.

## 결과

2026-10-24 결과: [집계 JSON](Results/summary.json) · [전체 클래스 CSV](Results/class-counts.csv)

원본: [최종 수집](Results/profile.json) · [알려진 객체 검산](Results/calibration.json) · [게임 검산](Results/result.json)

| 검산 대상 | 생성 | 약한 참조 소실 | 확인 내용 |
|---|---:|---:|---|
| 일반 객체 `CalTransient` | 10,000 | 10,000 | 참조를 해제한 객체가 모두 검출됨 |
| 유지 객체 `CalRetained` | 2,000 | 0 | 유지한 참조가 GC로 사라지지 않음 |
| 소멸자 객체 `CalFinalizable` | 100 | 100 | 소멸자 100회 실행도 별도 확인 |
| 부활 객체 `CalResurrecting` | 1 | 1 | 소멸자에서 부활했고 실제로 살아 있음 |

변경하지 않은 패키지 샘플도 실행했다.

| 샘플의 클래스 | 생성 | 약한 참조 소실 |
|---|---:|---:|
| `GpuTextureAtlas` | 1 | 1 |
| `GpuAtlasRenderer` | 4 | 4 |
| `AstcEncoder` | 4 | 4 |
| `CompressedAtlasEntry` | 4 | 4 |

샘플은 ASTC 아틀라스를 생성하고 렌더러 4개를 연결했다. 검산 앱이 샘플을 제거한 뒤 GC를 실행했다.
수집기의 핸들 생성 실패, 용량 초과, 재진입으로 생략한 할당은 모두 0이었다.
각 타입에서 `allocated = weakCleared + remainingTracked`를 확인했다.
전체 `remainingTracked` 합계와 수집기가 유지한 약한 핸들 수가 일치했다.

패키지의 코드·셰이더·네이티브 파일 42개는 실행 전후 해시가 같았다.
복사한 `DynamicAtlasDemo.cs`도 원본과 같다.

## 회수 지표의 정확한 의미

`allocated`는 수집기를 시작한 뒤 관측한 관리 힙 객체 할당 수다. 배열과 박싱된 값도 포함한다.
`weakCleared`는 IL2CPP의 짧은 약한 참조가 GC에 의해 비워진 수다.
`remainingTracked`는 아직 비워지지 않은 추적 개수다.

부활 검산에서는 `weakCleared=1`인데도 객체가 살아 있었다.
짧은 약한 참조는 소멸자가 객체를 부활시키기 전에 비워질 수 있기 때문이다.
따라서 `weakCleared`를 모든 클래스의 정확한 메모리 해제 수로 표시하면 안 된다.
`remainingTracked`도 현재 생존 객체 수로 해석하면 안 된다.
JSON에 `physicalFreeCountAvailable: false`를 명시했다.

소멸자 유무를 `hasFinalizer`로 제공한다. 이 값만으로 부활과 참조 관계의 모든 경우를 판정하지는 않는다.
설치된 IL2CPP는 `track_resurrection=true`인 약한 참조를 지원하지 않는다.
객체별 물리적 회수 이벤트까지 수집하려면 다른 GC 계측 경로가 필요하다. 이 도구는 Unity 런타임을 수정하지 않는다.

`Texture2D`, `GameObject`, `MonoBehaviour` 계열은 C# 관리 객체를 센다.
Unity 네이티브 객체의 `Destroy`나 GPU 자원 반환은 별도 지표다.
Unity의 UI·런타임과 검산 앱이 생성한 객체도 목록에 포함된다.

## 게임에 붙이는 부분

게임 클래스에 생성자 카운터, 소멸자, 추적 호출을 넣지 않는다.
개발용 APK에 다음 두 파일을 추가한다.

1. `Assets/Plugins/Android/arm64-v8a/libIl2CppAllocationProbe.so`
2. `Integration/Il2CppProbeBootstrap.cs`

부트스트랩은 `BeforeSceneLoad`에 시작한다. `LateUpdate`에서 GC 후 약한 참조를 확인한다.
`Il2CppProbeBootstrap.Save("checkpoint")`로 중간 결과를 저장할 수 있다.
`Save("profile", true)`는 최종 결과를 저장하고 할당 추적을 멈춘다.
정상 종료 때도 최종 저장을 시도한다. 강제 종료나 크래시에서는 최종 저장이 보장되지 않는다.
한 프로세스에서 한 번의 수집을 지원한다.

결과 위치는 `Application.persistentDataPath/il2cpp-profile`이다.
부트스트랩을 추가하기 전의 초기 런타임 할당은 포함하지 않는다.
APK 재빌드가 필요하며 휴대폰 루팅은 사용하지 않는다.

`Calibration/AndroidAllocationCalibration.cs`는 개수를 미리 정한 별도 검산 앱이다.
일반 게임에 이 파일을 추가할 필요는 없다.
강제 GC, 소멸자 검산, 샘플 생성·제거는 이 검산 앱에만 있다.

## 수집 방식과 보완 사항

IL2CPP가 내보내는 다음 네이티브 API를 `dlsym`으로 찾는다.

- `il2cpp_profiler_install_allocation`: 객체와 클래스의 할당 콜백.
- `il2cpp_profiler_install_gc`: GC 이벤트 콜백.
- `il2cpp_gchandle_new_weakref`, `get_target`, `free`: 약한 핸들 추적.
- 클래스·타입 조회 API: 타입 이름과 소멸자 유무 확인.

GC 콜백에서는 원자 카운터만 갱신한다. GC가 잠금을 잡은 상태에서 핸들을 조회하지 않는다.
메인 스레드에서 핸들을 확인하고, 소실된 핸들은 즉시 해제한다.
객체 주소를 강한 참조로 보관하지 않는다.
검사 중 다른 GC가 완료되면 다음 프레임에 다시 확인한다.

Unity 6000.7.0b5의 `Profiler::GCHeapResize`는 GC 이벤트 콜백이 있으면 힙 변경 콜백도 호출한다.
첫 기기 실행에서 빈 힙 변경 콜백으로 크래시가 발생했다. 두 콜백을 모두 등록해 수정했다.
최종 APK는 이 수정을 포함하며 기기 검산을 통과했다.

BDWGC의 증분 수집은 `GC_EVENT_END`를 항상 내보내지 않는다.
수집기는 `GC_EVENT_RECLAIM_END`를 후속 검사 신호로 사용한다.
`gcReclaimPhases`와 `fullGcEndEvents`를 따로 기록한다.
검산 앱의 `GC.CollectionCount(0)` 측정 구간은 부트스트랩의 관측 구간보다 짧다.
이 카운터들은 경계와 의미가 달라서 같은 값이라고 가정하지 않는다.

## 검증 범위와 비용

실기 검산에서는 증분 GC 설정을 켰다. 알려진 객체의 회수를 확인하는 단계에는 명시적 GC도 사용했다.
특정 Unity 버전의 IL2CPP 네이티브 API에 의존한다. 다른 버전과 기기에서는 다시 검증해야 한다.
모든 할당을 추적하므로 콜백, 약한 핸들, 네이티브 저장 공간과 GC 후 검사 비용이 발생한다.
이 도구의 실행 결과로 일반 게임의 성능을 평가하지 않는다.
추적 중인 핸들이 2,000,000개를 넘으면 누락 수를 기록한다. 검산 도구는 누락이 있으면 실패로 처리한다.
Java/Kotlin 객체와 Unity 네이티브 메모리 전체를 추적하는 도구는 아니다.

## 빌드와 재실행

Windows의 PowerShell 7에서 저장소 루트를 작업 폴더로 사용한다.
Unity 6000.7.0b5의 Android Build Support, SDK와 NDK가 필요하다.
설치 경로가 다르면 `-EditorData`로 해당 Editor의 `Data` 폴더를 지정한다.

```powershell
pwsh -File Verification/Profiling/AndroidIl2Cpp/build-native.ps1 `
  -EditorData 'C:/Program Files/Unity/Hub/Editor/6000.7.0b5/Editor/Data'
```

기본 출력은 `.validation/android-il2cpp-profiling/native/libIl2CppAllocationProbe.so`다.
`-OutputDirectory`로 출력 폴더를 바꿀 수 있다.
생성한 SO를 개발용 Unity 프로젝트의 `Assets/Plugins/Android/arm64-v8a`에 복사한다.
Plugin Inspector에서 Android ARM64만 활성화한다.
`Integration/Il2CppProbeBootstrap.cs`도 프로젝트에 복사하고 IL2CPP ARM64 Development APK를 빌드한다.
부트스트랩은 Android 전용이다. Editor에서 이 컴포넌트를 실행하지 않는다.

알려진 객체 검산을 재현할 때는 별도 Built-in 렌더 파이프라인 프로젝트를 사용한다.
이 저장소의 UPM 패키지를 설치하고 Dynamic Atlas 샘플을 Import한다.
`Calibration/AndroidAllocationCalibration.cs`와 `Editor/AndroidAllocationBuild.cs`를 각각 `Assets/Diagnostics`와 `Assets/Editor`에 복사한다.
위 SO와 부트스트랩도 추가한다. Image Conversion, IMGUI, JSON Serialize, Sprite Mask 기본 모듈이 필요하다.
Unity를 `-batchmode -buildTarget Android -projectPath <프로젝트 경로> -executeMethod AndroidAllocationBuild.Run -logFile <로그 경로>`로 실행한다.
Editor 스크립트가 검산 씬을 생성하고 `Build/AllocationProbe.apk`를 만든다.

검산 앱 ID는 `com.ittodo.il2cppallocationprobe`다.
ADB에서 연결된 기기 ID를 확인한 뒤 APK를 설치하고 실행한다.
결과는 `/sdcard/Android/data/com.ittodo.il2cppallocationprobe/files/il2cpp-profile`에서 가져온다.
`result.json`의 `passed`와 `profile.json`의 `stopped`가 모두 참인지 확인한다.
`handleFailures`, `capacityDropped`, `reentrantSkipped`는 모두 0이어야 한다.
검산 표의 생성·약한 참조 소실 개수도 원본 JSON과 대조한다.

`Results/`에는 2026-10-24의 검산 결과와 빌드 해시를 보관한다.
생성한 프로젝트, APK, SO, 기기 로그는 저장소에 포함하지 않는다.
진단 소스와 검산 앱은 UPM 패키지의 런타임 의존성이 아니다.

참고: [Microsoft의 짧은 약한 참조와 소멸자 설명](https://learn.microsoft.com/en-us/archive/msdn-magazine/2000/december/garbage-collection-part-2-automatic-memory-management-in-the-microsoft-net-framework)
