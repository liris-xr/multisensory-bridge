# BLE Bridge (`com.lirisxr.blebridge`)

The **single shared owner** of the native BLE libraries used to talk to BLE hardware from Unity.
Device packages (Nebula, StimulHeat, ...) depend on this and ask it for links; they never import
the native DLLs themselves.

## Why this exists

Nebula and StimulHeat both used the same third-party library — [`BleWinrtDll`](https://github.com/adabru/BleWinrtDll)
(adabru) — on Windows. Two problems:

1. **Duplicate native binding.** Each package shipped its own `BleWinrtDll.dll` and its own
   P/Invoke (`BleApi` / `BLE.Impl`). Putting both in one Unity project is a build-time DLL conflict.
2. **Global `Quit()`.** `BleWinrtDll.Quit()` releases *all* BLE links at once. When each package
   called it independently (on disconnect, on `OnApplicationQuit`), one tore the other's
   connection down.

`BleBridge` fixes both:

- It is the **only** package that imports `BleWinrtDll.dll` and the **only** code that calls `Quit()`.
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

Events fire from the manager's background polling thread. Consumers marshal them to the main
thread themselves (see `NebulaManager` / `StimulHeatDevicesManager`).

## Platform support

| Platform | Backend | Status |
|----------|---------|--------|
| Windows / Editor (x64) | `BleWinrtDll` (adabru) | Implemented |
| Android | native plugin | **Stubbed** — wiring is in place (`BLEBRIDGE_ANDROID` define), pending the Android plugin |

To enable the Windows backend, add the scripting define **`BLEBRIDGE_WINRT`** in
*Player Settings → Other Settings → Scripting Define Symbols* and build for x64, Windows 10+.

## Structure

```
Runtime/
├── com.lirisxr.blebridge.Runtime.asmdef
├── BleApi.cs                 # sole P/Invoke binding for BleWinrtDll (BLEBRIDGE_WINRT)
├── IBleLink.cs               # public API: IBleLink, BleDeviceProfile, BleDataPacket
├── BleConnectionManager.cs   # the multiplexer; sole Quit() owner
├── BleLink.cs                # IBleLink implementation
└── Plugins/BleWinRT/         # the native DLL lives here (once)
    ├── BleWinrtDll.dll
    └── LICENSE.txt            # WTFPL — free to redistribute
```
