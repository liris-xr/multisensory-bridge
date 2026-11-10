using System;
using System.Threading;

namespace Lirisxr.BleBridge
{
    /// <summary>
    /// Concrete IBleLink. Holds one device profile and its discovered ids; routes
    /// events back to the consumer. All native calls happen through the owning
    /// BleConnectionManager, never directly.
    /// </summary>
    internal sealed class BleLink : IBleLink
    {
        public string DeviceId => _deviceId;
        public bool IsConnected => _connected;

        public event Action<IBleLink> OnConnected;
        public event Action<IBleLink> OnDisconnected;
        public event Action<IBleLink, BleDataPacket> OnDataReceived;

        internal BleDeviceProfile Profile => _profile;
        internal string ServiceUuid => _serviceUuid;
        internal string WriteUuid => _writeUuid;

        private readonly BleConnectionManager _manager;
        private readonly BleDeviceProfile _profile;
        private string _deviceId;
        private string _serviceUuid;
        private string _writeUuid;
        private volatile bool _connected;
        private int _subscribedCount;
        private volatile bool _disposed;

        internal BleLink(BleConnectionManager manager, BleDeviceProfile profile)
        {
            _manager = manager;
            _profile = profile;
        }

        public void Write(byte[] data)
        {
            Write(data, null);
        }

        public void Write(byte[] data, Action<bool> onWriteResult)
        {
            if (_disposed || !_connected || data == null)
            {
                onWriteResult?.Invoke(false);
                return;
            }
            _manager.NativeWrite(this, data, onWriteResult);
        }

        internal void SetDeviceId(string id) { _deviceId = id; }
        internal void SetServiceUuid(string uuid) { _serviceUuid = uuid; }
        internal void SetWriteUuid(string uuid) { _writeUuid = uuid; }
        internal void RegisterSubscribed(string uuid) { Interlocked.Increment(ref _subscribedCount); }

        internal void SignalConnected()
        {
            _connected = true;
            try { OnConnected?.Invoke(this); } catch { }
        }

        internal void SignalConnectionLost(string reason)
        {
            bool was = _connected;
            _connected = false;
            if (was)
            {
                try { OnDisconnected?.Invoke(this); } catch { }
            }
        }

        internal void Deliver(byte[] buf, short size, string deviceId, string serviceUuid, string characteristicUuid)
        {
            var pkt = new BleDataPacket
            {
                buf = buf,
                size = size,
                deviceId = deviceId,
                serviceUuid = serviceUuid,
                characteristicUuid = characteristicUuid
            };
            try { OnDataReceived?.Invoke(this, pkt); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            bool was = _connected;
            _connected = false;
            _manager.ReleaseLink(this);
            if (was)
            {
                try { OnDisconnected?.Invoke(this); } catch { }
            }
        }
    }
}
