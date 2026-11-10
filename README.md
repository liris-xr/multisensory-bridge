# BLE Bridge (`com.lirisxr.blebridge`)

The **single shared owner** of the native BLE libraries used to talk to BLE hardware from Unity.
Device packages (Nebula, StimulHeat, ...) depend on this one and ask it for links; they never
import the native DLLs themselves.

This is the bridge half of a small ecosystem of device packages:

| Package | Role |
|---|---|
| `com.lirisxr.blebridge` (this repo) | Shared owner of the native BLE backends. Multiplexes device links so several packages share one connection stack without DLL or `Quit()` conflicts. |
| `com.lirisxr.nebula` | Nebula olfactory device setup. Uses this bridge for BLE. |
| `fr.renforce.stimulheat` | StimulHeat thermal device setup. Uses this bridge for BLE. |

## Why this exists

Nebula and StimulHeat both used the same third-party library — [`BleWinrtDll`](https://github.com/adabru/BleWinrtDll)
(adabru) — on Windows. Two problems:

1. **Duplicate native binding.** Each package shipped its own `BleWinrtDll.dll` and its own
   P/Invoke. Putting both in one Unity project is a build-time DLL conflict.
2. **Global `Quit()`.** `BleWinrtDll.Quit()` releases *all* BLE links at once. When each package
   called it independently (on disconnect, on `OnApplicationQuit`), one tore the other's
   connection down.

`BleBridge` fixes both:

- It is the **only** package that imports the native libraries and the **only** code that calls `Quit()`.
- `BleConnectionManager` runs **one** shared scan and fans discovered devices out to the right
  link by advertised name + service UUID. Incoming packets are routed by native device id.
- The native stack lifetime is **ref-counted** across all active links: it is torn down only when
  the last link is disposed, so coexisting device packages never kill each other.

## How device packages use it

```csharp
using Lirisxr.BleBridge;

var profile = new BleDeviceProfile
{
    deviceName = "Nebula",                       // advertised name, matched as a substring (case-insensitive)
    serviceUuid = "180c",                        // can be short or full 128-bit UUID
    notifyCharacteristicUuids = new[] { "2a56" },
    writeCharacteristicUuid    = "2a56"
};

IBleLink link = BleConnectionManager.Instance.RequestLink(profile);
link.OnConnected     += l => { /* link ready */ };
link.OnDisconnected  += l => { /* link lost  */ };
link.OnDataReceived   += (l, p) => { /* p.buf / p.size hold one packet */ };

link.Write(Encoding.ASCII.GetBytes("S"));        // outgoing data
link.Dispose();                                  // release; stack torn down when last link goes
```

Events fire from the main thread on both Windows and Android (the backends poll/handle on the
main thread). Consumers may marshal to the main thread themselves regardless (Nebula uses an
`EnqueueNotification` queue drained in `Tick()`; StimulHeat mutates device fields directly).

## Backends & automatic platform switching

The bridge selects its native backend automatically from the build target — **no manual
scripting defines are required**:

| Build target | Backend | Native lib | Threading model |
|---|---|---|---|
| Windows Editor / Standalone | WinRT | `BleWinrtDll.dll` (x64, Windows 10+) | Main-thread non-blocking polling (MonoBehaviour) |
| Android | Android | Velorexe [Unity-Android-Bluetooth-Low-Energy](https://github.com/Velorexe/Unity-Android-Bluetooth-Low-Energy) | Main-thread event-driven (command queue) |

Switching uses Unity's built-in platform defines (`UNITY_EDITOR_WIN`/`UNITY_STANDALONE_WIN`,
`UNITY_ANDROID`), so building for Android automatically uses the Android backend and building
for Windows uses the WinRT backend.

The Android backend (`BleAndroidBackend`) is a `MonoBehaviour` that drives Velorexe's
`BleManager` command queue on the main thread. Velorexe is vendored into this package
(`Runtime/AndroidBLE/` + `Plugins/Android/unityandroidble.jar`) and is public-domain licensed
(see `LICENSE.md`). Its runtime assembly is compiled **only** for the Android platform.

## Architecture

```
Device packages (Nebula, StimulHeat, ...)
        │  request IBleLink(BleDeviceProfile)
        ▼
BleConnectionManager  (singleton, the only Quit() owner)
        │
        ├── Windows: BleApi (P/Invoke BleWinrtDll) ── background thread scan/subscribe/poll
        └── Android: BleAndroidBackend ── Velorexe command queue (discover/connect/subscribe/write)
```

## Installation

Add the package to your Unity project (2022.3+). The native dependencies are vendored in-package
(the WinRT DLL and the Android jar), so no extra install steps are needed beyond importing it.

Device packages that use BLE (Nebula, StimulHeat) declare `com.lirisxr.blebridge` as a UPM
dependency, so installing one of them pulls this bridge in automatically.

## Platform setup

### Windows / Editor

Build for x64, Windows 10+ with Bluetooth on. The bridge uses `BleWinrtDll` automatically.

### Android

Switch the build target to Android. The bridge uses the Velorexe backend automatically and
requests the BLE runtime permissions (`BLUETOOTH_SCAN`, `BLUETOOTH_CONNECT`,
`ACCESS_FINE_LOCATION`) at runtime on first use (API 31+).

For the runtime request to actually show a dialog, the permissions **must** be present in the
final merged manifest. The package also ships a plugin-level manifest
(`Plugins/Android/AndroidManifest.xml`), but a plugin manifest is only merged when its plugin is
included for the build target ABI — so it can silently drop out and Android will then auto-deny
the permissions with **no dialog**. The reliable path is a project-level manifest at
`Assets/Plugins/Android/AndroidManifest.xml`, which Unity always merges.

This package ships an Editor wizard that creates/merges that project-level manifest and sets the
required player setting for you:

- It opens automatically when the package is first imported.
- Reopen it any time via **Window → BLE Bridge → Android Manifest**.
- If no project manifest exists, **Create permissions manifest** writes an
  `Assets/Plugins/Android/AndroidManifest.xml` containing the Unity launcher activity (with
  `android:exported="true"`, required on Android 12+) plus the BLE `<uses-permission>` lines.
  A custom project manifest replaces Unity's default library manifest as the main manifest, so
  it must include the launcher activity or the APK builds but cannot launch.
- If a manifest already exists, **Add missing permissions** inserts the missing
  `<uses-permission>` lines before `</manifest>` instead of overwriting it.
- **Application entry point** — the bridge uses a pure-Java Android plugin (the Velorexe jar).
  Unity 2022.2+ defaults the Android entry point to **GameActivity**, which is unsuitable for
  pure-Java plugins; the classic **Activity** entry point (`com.unity3d.player.UnityPlayerActivity`)
  must be selected, or the APK builds but fails to launch with
  `No activity in the manifest with action MAIN and category LAUNCHER`. The wizard exposes the
  setting and warns on import if it is left on GameActivity.

## Structure

```
Runtime/
├── com.lirisxr.blebridge.Runtime.asmdef
├── IBleLink.cs               # public API: IBleLink, BleDeviceProfile, BleDataPacket
├── BleConnectionManager.cs   # the multiplexer; sole Quit() owner
├── BleLink.cs                # IBleLink implementation
├── BleApi.cs                 # sole P/Invoke binding for BleWinrtDll (Windows)
├── BleWinrtBackend.cs        # Windows/Editor backend
├── BleAndroidBackend.cs      # Android backend (Velorexe command queue)
├── AndroidBLE/               # vendored Velorexe Unity-Android-Bluetooth-Low-Energy
└── Plugins/BleWinRT/         # the Windows native DLL lives here (once)
    ├── BleWinrtDll.dll
    └── LICENSE.txt           # WTFPL — free to redistribute
Editor/
├── BleBridgeDetectImport.cs  # import-time checks + wizard auto-open
└── BleBridgeManifestWizard.cs
```

## Licenses

- `BleWinrtDll` — WTFPL (see `Runtime/Plugins/BleWinRT/LICENSE.txt`)
- Velorexe Unity-Android-Bluetooth-Low-Energy — The Unlicense / public domain (see `LICENSE.md`)
