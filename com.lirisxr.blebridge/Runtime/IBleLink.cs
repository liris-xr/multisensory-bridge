using System;

namespace Lirisxr.BleBridge
{
    /// <summary>
    /// One inbound BLE packet, routed to a link by its native deviceId.
    /// Mirrors the native BleWinrtDll BLEData but as a plain managed struct
    /// so device packages never touch the native binding.
    /// </summary>
    public struct BleDataPacket
    {
        public byte[] buf;
        public short size;
        public string deviceId;
        public string serviceUuid;
        public string characteristicUuid;
    }

    /// <summary>
    /// What a device package needs to connect its hardware. Handed to
    /// BleConnectionManager.RequestLink(). The advertised name is used to
    /// pick the device out of the shared scan; the UUIDs drive discovery.
    /// </summary>
    public struct BleDeviceProfile
    {
        /// <summary>Advertised device name to match during scan (case-insensitive, substring).</summary>
        public string deviceName;
        /// <summary>Full 128-bit service UUID, e.g. "{6E400001-B5A3-F393-E0A9-E50E24DCCA9E}".</summary>
        public string serviceUuid;
        /// <summary>Characteristic UUIDs to subscribe to for notifications/incoming data.</summary>
        public string[] notifyCharacteristicUuids;
        /// <summary>Characteristic UUID to write outgoing data to.</summary>
        public string writeCharacteristicUuid;
    }

    /// <summary>
    /// One live BLE link to a single device, obtained from BleConnectionManager.
    /// Raised events fire from the manager's polling thread; consumers marshal
    /// to the main thread themselves (see NebulaManager / StimulHeatDevicesManager).
    /// </summary>
    public interface IBleLink : IDisposable
    {
        /// <summary>Native device id (BLE address) once the device has been discovered.</summary>
        string DeviceId { get; }
        /// <summary>True once scan + discovery + subscribe finished and the link is usable.</summary>
        bool IsConnected { get; }

        event Action<IBleLink> OnConnected;
        event Action<IBleLink> OnDisconnected;
        event Action<IBleLink, BleDataPacket> OnDataReceived;

        /// <summary>Writes a payload to the write characteristic. No-op if not connected.</summary>
        void Write(byte[] data);

        /// <summary>
        /// Writes a payload to the write characteristic and reports whether the native
        /// stack acknowledged the write. <paramref name="onWriteResult"/> is invoked once
        /// with <see langword="true"/> on success or <see langword="false"/> on failure/timeout.
        /// The callback fires on a background thread on Windows and on the main thread on
        /// Android; consumers marshal to the main thread themselves.
        /// No-op (callback invoked with <see langword="false"/>) if not connected.
        /// </summary>
        void Write(byte[] data, Action<bool> onWriteResult);
    }
}
