#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Android;
using Android.BLE;
using Android.BLE.Commands;

namespace Lirisxr.BleBridge
{
    /// <summary>
    /// Android BLE backend for BleBridge, backed by Velorexe's
    /// Unity-Android-Bluetooth-Low-Energy library (com.velorexe.androidbluetoothlowenergy).
    ///
    /// Unlike the Windows WinRT backend (a background polling thread over BleWinrtDll), the
    /// Velorexe library is main-thread / event driven: a BleManager MonoBehaviour drains a
    /// command queue in Update() and Java calls back into Unity via UnitySendMessage (main
    /// thread). So this backend owns NO polling thread. BleConnectionManager delegates
    /// RequestLink/Write/Release to it; the shared BleLink is reused and its events fire on the
    /// main thread here (a safe subset of the IBleLink contract, which says consumers must
    /// marshal to main thread themselves either way).
    ///
    /// UUIDs are normalized to full 128-bit lowercase and every characteristic command uses
    /// customGatt=true. This matches the only confirmed-working Android path (StimulHeat's
    /// Nordic UART service, full lowercase UUIDs) and also works for 16-bit UUIDs (e.g. Nebula's
    /// 0x180C / 0x2A56) once expanded to the Bluetooth base UUID
    /// 0000xxxx-0000-1000-8000-00805f9b34fb, since a 16-bit GATT UUID *is* that 128-bit UUID.
    /// </summary>
    internal sealed class BleAndroidBackend : MonoBehaviour
    {
        public static BleAndroidBackend Instance
        {
            get
            {
                if (_instance != null) return _instance;
                var go = new GameObject(nameof(BleAndroidBackend));
                _instance = go.AddComponent<BleAndroidBackend>();
                return _instance;
            }
        }
        private static BleAndroidBackend _instance;

        private const float LinkTimeoutSeconds = 15f;
        private const int DiscoverTimeMs = 10000;

        // The vendored Velorexe jar never calls BluetoothGatt.requestMtu(), so the connection
        // stays at the BLE default ATT MTU of 23 bytes = only 20 bytes usable per GATT write.
        // An oversize setValue() + writeCharacteristic() is silently truncated by Android to
        // the MTU, so a 39-byte COBS frame never delivers its trailing 0x00 delimiter and the
        // firmware never decodes it. The firmware reassembles COBS by accumulating bytes across
        // writes until the 0x00 delimiter, so we transparently split each outgoing frame into
        // chunks of at most this many bytes and queue each as a serialized AckedWrite. Android
        // only allows one outstanding GATT write at a time, and the queue serializes them, so
        // the chunks arrive in order with one outstanding write at a time.
        private const int AndroidMtuPayload = 20;

        // A pending link waiting to be discovered + connected.
        private class Pending
        {
            public BleLink Link;
            public float RequestedAt;
        }

        private readonly List<Pending> _pending = new List<Pending>();
        private readonly Dictionary<string, BleLink> _connected = new Dictionary<string, BleLink>();
        // deviceId -> the ConnectToDevice command (so we can disconnect on release).
        private readonly Dictionary<string, ConnectToDevice> _connectCommands = new Dictionary<string, ConnectToDevice>();
        // deviceId -> active subscribe commands (so we can unsubscribe on release).
        private readonly Dictionary<string, List<SubscribeToCharacteristic>> _subscribeCommands = new Dictionary<string, List<SubscribeToCharacteristic>>();
        // deviceIds already claimed during a scan, so two links can't grab the same peripheral.
        private readonly HashSet<string> _claimed = new HashSet<string>();

        // Per-device incoming accumulator. The vendored jar never negotiates MTU, so a notification
        // larger than the default 23-byte MTU (20 usable bytes) arrives truncated and the device's
        // COBS frame (terminated by 0x00) is split across multiple CharacteristicValueChanged
        // callbacks. Consumers (StimulHeat's UpdateMeasurementResults) expect the whole frame in
        // one packet and search for the 0x00 delimiter, so we reassemble here: append each chunk to
        // the device's buffer and Deliver the accumulated bytes only once the 0x00 delimiter lands.
        private readonly Dictionary<string, List<byte>> _rxAccumulators = new Dictionary<string, List<byte>>();

        private readonly ConcurrentQueue<WriteRequest> _writeQueue = new ConcurrentQueue<WriteRequest>();
        private readonly object _lock = new object();

        private BleManager _ble;
        private bool _booted;
        private bool _permissionsGranted;
        private bool _permissionCheckStarted;
        private DiscoverDevices _scanCommand;
        private bool _scanRunning;
        private bool _restartScanNextFrame;

        // Android runtime permission names. BLUETOOTH_SCAN/BLUETOOTH_CONNECT are the scan
        // permissions on API 31+; ACCESS_FINE_LOCATION is needed for scan on API 29-30 and is
        // harmless to request on 31+.
        private const string BleScanPermission = "android.permission.BLUETOOTH_SCAN";
        private const string BleConnectPermission = "android.permission.BLUETOOTH_CONNECT";
        private const string LocationPermission = "android.permission.ACCESS_FINE_LOCATION";

        private struct WriteRequest
        {
            public BleLink Link;
            public byte[] Data;
            public Action<bool> OnWriteResult; // null = fire-and-forget
        }

        // The vendored Velorexe jar's onDescriptorWrite does not forward to Unity, so a
        // "DescriptorWrite" event never reaches C#. A queued SubscribeToCharacteristic can never be
        // detected as complete by CommandReceived; it stays as the _activeCommand, blocks the whole
        // command queue until its timeout, and EndOnTimeout -> End() then UNSUBSCRIBES, tearing down
        // the notification subscription the device needs. So we run subscribe as a parallel
        // fire-and-forget command: Start() sends the Java subscribe call synchronously and we never
        // block the queue or unsubscribe on a timeout.
        private class FireAndForgetSubscribe : SubscribeToCharacteristic
        {
            public FireAndForgetSubscribe(string deviceAddress, string service, string characteristic,
                CharacteristicChanged onDataFound, bool customGatt)
                : base(deviceAddress, service, characteristic, onDataFound, customGatt)
            {
                RunParallel = true;
                _timeout = float.MaxValue;
            }
            // Never unsubscribe on timeout: the original End() calls unsubscribeFromGatt*, which
            // would tear down the subscription. We only want notifications to keep flowing.
            public override void EndOnTimeout() { }
        }

        // Unlike subscribe, the jar DOES report a "CharacteristicWrite" event when the Android
        // onCharacteristicWrite callback arrives, so we serialize writes through the BleManager
        // queue (RunParallel=false) and wait for that ack. This is required on Android: only one
        // GATT write may be outstanding at a time, so firing two writes back-to-back without
        // waiting for the ack makes Android drop/coalesce the second, which corrupts the device's
        // COBS framing. Each write waits for CharacteristicWrite; if it times out we report failure
        // (StimulHeat's SendAcked retries).
        private class AckedWrite : WriteToCharacteristic
        {
            private readonly Action<bool> _onResult;
            private int _done;
            public AckedWrite(string deviceAddress, string serviceAddress, string characteristicAddress, byte[] data, Action<bool> onResult)
                : base(deviceAddress, serviceAddress, characteristicAddress, data, true)
            {
                _onResult = onResult;
                _timeout = 5f;
            }
            public override bool CommandReceived(BleObject obj)
            {
                bool consumed = base.CommandReceived(obj);
                if (consumed) Finish(true);
                return consumed;
            }
            public override void EndOnTimeout()
            {
                Finish(false);
                base.EndOnTimeout();
            }
            private void Finish(bool ok)
            {
                if (System.Threading.Interlocked.Exchange(ref _done, 1) == 0)
                    _onResult?.Invoke(ok);
            }
        }



        // =========================================================
        // Lifecycle
        // =========================================================
        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            try { _ble?.DeInitialize(); } catch (Exception e) { Debug.LogWarning("[BleBridge][Android] DeInitialize: " + e.Message); }
            if (_instance == this) _instance = null;
        }

        private void OnApplicationQuit() => OnDestroy();

        /// <summary>Boots Velorexe's BleManager and requests Android BLE permissions.</summary>
        internal void EnsureBoot()
        {
            if (_booted) return;
            _booted = true;
            _ble = BleManager.Instance; // accessing the singleton creates + initializes it
            try { _ble?.Initialize(); }
            catch (Exception e) { Debug.LogError($"[BleBridge][Android] BleManager init failed: {e.Message}"); }
            // The Java library hook is required for every SendCommand; surface a loud error if it
            // is null so a silent no-op (every command swallowed by _bleLibrary?.Call) is visible.
            // Velorexe's _bleLibrary is internal to its own assembly, so probe the Java singleton
            // through the public AndroidJavaClass API instead of reaching into BleManager.
            if (!IsJavaLibraryBound())
                Debug.LogError("[BleBridge][Android] Java BLE library not bound; all commands will be no-ops.");

            EnsurePermissions();
        }

        private static bool IsJavaLibraryBound()
        {
            try
            {
                using (var cls = new AndroidJavaClass("com.velorexe.unityandroidble.UnityAndroidBLE"))
                using (var inst = cls.CallStatic<AndroidJavaObject>("getInstance"))
                    return inst != null;
            }
            catch (Exception e)
            {
                Debug.LogError($"[BleBridge][Android] Java singleton probe failed: {e.Message}");
                return false;
            }
        }

        // BLE scan on Android 12+ requires the BLUETOOTH_SCAN / BLUETOOTH_CONNECT runtime
        // permissions (and ACCESS_FINE_LOCATION on API 29-30). Scanning before they are granted
        // silently fails: Android's BluetoothLeScanner returns no results and no completion, so
        // the link times out. We request the permissions ourselves with explicit callbacks (NOT
        // the AppPermissions component, which has exitOnUserDenied=true and would Application.Quit
        // the app if the user denies) and gate the first scan on all of them being granted. On a
        // second run they are already authorized and we scan immediately.
        private void EnsurePermissions()
        {
            if (_permissionCheckStarted) return;
            _permissionCheckStarted = true;

            if (PermissionsAlreadyGranted())
            {
                OnPermissionsGranted();
                return;
            }

            Debug.Log("[BleBridge][Android] Requesting BLE permissions before scanning...");
            var needed = new List<string>(3);
            if (!Permission.HasUserAuthorizedPermission(BleScanPermission)) needed.Add(BleScanPermission);
            if (!Permission.HasUserAuthorizedPermission(BleConnectPermission)) needed.Add(BleConnectPermission);
            if (!Permission.HasUserAuthorizedPermission(LocationPermission)) needed.Add(LocationPermission);

            if (needed.Count == 0) { OnPermissionsGranted(); return; }

            // Track how many of the requested permissions have been resolved; once all are, decide.
            var resolved = new int[1];
            var granted = new bool[needed.Count];
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += name => ResolvePermission(name, needed, granted, resolved, true);
            callbacks.PermissionDenied += name => ResolvePermission(name, needed, granted, resolved, false);
            callbacks.PermissionDeniedAndDontAskAgain += name => ResolvePermission(name, needed, granted, resolved, false);
            Permission.RequestUserPermissions(needed.ToArray(), callbacks);
        }

        private void ResolvePermission(string name, List<string> needed, bool[] granted, int[] resolved, bool isGranted)
        {
            int idx = needed.IndexOf(name);
            if (idx >= 0 && !granted[idx])
            {
                granted[idx] = isGranted;
                resolved[0]++;
                if (!isGranted)
                    Debug.LogWarning($"[BleBridge][Android] Permission denied: {name}");
                if (resolved[0] >= needed.Count)
                {
                    // Proceed regardless of grant state: if the user denied, the scan will simply
                    // find nothing and the link times out (with a clear log) rather than the app
                    // quitting. We never call Application.Quit.
                    bool allOk = true;
                    for (int i = 0; i < granted.Length; i++) if (!granted[i]) allOk = false;
                    if (allOk) OnPermissionsGranted();
                    else
                    {
                        Debug.LogError("[BleBridge][Android] Some BLE permissions denied; scanning may find no devices.");
                        OnPermissionsGranted();
                    }
                }
            }
        }

        private bool PermissionsAlreadyGranted()
        {
            try
            {
                if (Permission.HasUserAuthorizedPermission(BleScanPermission)
                    && Permission.HasUserAuthorizedPermission(BleConnectPermission))
                    return true;
            }
            catch { }
            return false;
        }

        private void OnPermissionsGranted()
        {
            _permissionsGranted = true;
            Debug.Log("[BleBridge][Android] BLE permissions granted; starting scan if pending.");
            // A RequestLink may have added pending links before permissions were granted; now we
            // can finally scan for them.
            StartScanIfNeeded();
        }

        // =========================================================
        // Link registration (called by BleConnectionManager on the main thread)
        // =========================================================
        internal void RequestLink(BleLink link)
        {
            EnsureBoot();
            lock (_lock)
            {
                _pending.Add(new Pending { Link = link, RequestedAt = Time.realtimeSinceStartup });
            }
            StartScanIfNeeded();
        }

        // =========================================================
        // Scan
        // =========================================================
        private void StartScanIfNeeded()
        {
            // Never start a scan before the runtime BLE permissions are granted: on Android 12+
            // an un-permitted scan silently returns nothing and the link times out.
            if (!_permissionsGranted) return;
            if (_scanRunning) return;
            bool any;
            lock (_lock) any = _pending.Count > 0;
            if (!any) return;

            _scanRunning = true;
            _claimed.Clear();
            var scan = new DiscoverDevices(
                onDeviceDiscovered: (peripheralId, peripheralName) =>
                {
                    if (string.IsNullOrEmpty(peripheralName)) return;
                    OnDeviceDiscovered(peripheralId, peripheralName);
                },
                onFinishedDiscovering: () =>
                {
                    _scanRunning = false;
                    _scanCommand = null;
                    OnScanFinished();
                },
                DiscoverTimeMs);

            _scanCommand = scan;
            _ble.QueueCommand(scan);
        }

        private void OnDeviceDiscovered(string peripheralId, string peripheralName)
        {
            if (_claimed.Contains(peripheralId)) return;
            lock (_lock)
            {
                for (int i = 0; i < _pending.Count; i++)
                {
                    var p = _pending[i];
                    if (p.Link.DeviceId != null) continue;
                    var name = p.Link.Profile.deviceName;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (peripheralName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        p.Link.SetDeviceId(peripheralId);
                        _claimed.Add(peripheralId);
                        return;
                    }
                }
            }
        }

        private void OnScanFinished()
        {
            // Connect every pending link that now has a discovered id.
            List<BleLink> toConnect = null;
            List<Pending> stragglers = null;
            lock (_lock)
            {
                for (int i = _pending.Count - 1; i >= 0; i--)
                {
                    var p = _pending[i];
                    if (p.Link.DeviceId != null)
                    {
                        (toConnect ??= new List<BleLink>()).Add(p.Link);
                        _pending.RemoveAt(i);
                    }
                    else
                    {
                        (stragglers ??= new List<Pending>()).Add(p);
                    }
                }
            }

            if (toConnect != null)
            {
                foreach (var link in toConnect) ConnectLink(link);
            }

            // Restart the scan for still-undiscovered links on the next frame, NOT synchronously
            // here: DiscoverDevices.End() calls stopScanBleDevices() AFTER OnFinishedDiscovering
            // returns, so a synchronous StartScanIfNeeded() would start a new scan that End()
            // then immediately stops, yielding a ~10ms scan that finds nothing.
            //
            // Reset each straggler's RequestedAt so the pending-link timeout (LinkTimeoutSeconds)
            // measures from the START of the upcoming scan cycle, not from the original RequestLink
            // call. Without this, a straggler that wasn't found in the first 10s scan would time out
            // partway through the restart scan (10s + 10s > 15s), so the second device would never
            // connect - the "connect one but not the other" symptom.
            if (stragglers != null)
            {
                float now = Time.realtimeSinceStartup;
                lock (_lock)
                {
                    foreach (var p in stragglers)
                        p.RequestedAt = now;
                }
                _restartScanNextFrame = true;
            }
        }

        // =========================================================
        // Connect + subscribe
        // =========================================================
        private void ConnectLink(BleLink link)
        {
            string id = link.DeviceId;
            var profile = link.Profile;

            var connect = new ConnectToDevice(
                id,
                onConnected: deviceAddress => OnDeviceConnected(link),
                onDisconnected: deviceAddress => OnDeviceDisconnected(link));
            lock (_lock) _connectCommands[id] = connect;
            _ble.QueueCommand(connect);
        }

        private void OnDeviceConnected(BleLink link)
        {
            string id = link.DeviceId;
            var profile = link.Profile;
            var subscribes = new List<SubscribeToCharacteristic>();

            if (profile.notifyCharacteristicUuids != null)
            {
                string service = NormalizeUuid(profile.serviceUuid);
                foreach (var rawChar in profile.notifyCharacteristicUuids)
                {
                    if (string.IsNullOrEmpty(rawChar)) continue;
                    string ch = NormalizeUuid(rawChar);
                    var capturedChar = ch;
                    var sub = new FireAndForgetSubscribe(
                        id,
                        service,
                        ch,
                        value => OnCharacteristicChanged(link, capturedChar, value),
                        true); // customGatt=true -> full 128-bit UUID path (base64 payloads)
                    subscribes.Add(sub);
                    _ble.QueueCommand(sub);
                }
            }

            lock (_lock)
            {
                _subscribeCommands[id] = subscribes;
                _connected[id] = link;
            }

            // Mirror the user's confirmed-working Android script: treat GATT connect as "ready"
            // and start writing immediately; notification data flows via the subscribe callback.
            link.SignalConnected();
        }

        private void OnDeviceDisconnected(BleLink link)
        {
            link.SignalConnectionLost("device disconnected");
            lock (_lock)
            {
                if (link.DeviceId != null)
                {
                    _connected.Remove(link.DeviceId);
                    _connectCommands.Remove(link.DeviceId);
                    _subscribeCommands.Remove(link.DeviceId);
                    _rxAccumulators.Remove(link.DeviceId);
                }
            }
        }

        private void OnCharacteristicChanged(BleLink link, string characteristicUuid, byte[] value)
        {
            if (value == null || value.Length == 0) return;
            string service = NormalizeUuid(link.Profile.serviceUuid);
            // Reassemble COBS: the vendored jar never negotiates MTU, so a notification larger
            // than the default 23-byte MTU (20 usable bytes) is split across multiple callbacks.
            // Append each chunk to the device's buffer and Deliver the accumulated bytes only once
            // the COBS frame delimiter (0x00) lands, so consumers see the whole frame in one packet.
            List<byte> acc;
            lock (_lock)
            {
                if (!_rxAccumulators.TryGetValue(link.DeviceId, out acc))
                {
                    acc = new List<byte>(64);
                    _rxAccumulators[link.DeviceId] = acc;
                }
            }
            lock (acc)
            {
                acc.AddRange(value);
                int zero = acc.IndexOf((byte)0);
                if (zero < 0) return; // incomplete frame; keep accumulating
                // Deliver the complete frame INCLUDING the 0x00 delimiter: StimulHeat's
                // UpdateMeasurementResults searches for byte.MinValue and slices before it.
                byte[] frame = new byte[zero + 1];
                acc.CopyTo(0, frame, 0, zero + 1);
                acc.RemoveRange(0, zero + 1);
                link.Deliver(frame, (short)frame.Length, link.DeviceId, service, characteristicUuid);
            }
        }

        // =========================================================
        // Write (drained on the main thread -> thread-safe for Velorexe's command queue)
        // =========================================================
        internal void EnqueueWrite(BleLink link, byte[] data, Action<bool> onWriteResult)
        {
            if (link == null || data == null || data.Length == 0)
            {
                onWriteResult?.Invoke(false);
                return;
            }
            _writeQueue.Enqueue(new WriteRequest { Link = link, Data = data, OnWriteResult = onWriteResult });
        }

        private void Update()
        {
            while (_writeQueue.TryDequeue(out var w))
            {
                TryWrite(w.Link, w.Data, w.OnWriteResult);
            }
            if (_restartScanNextFrame)
            {
                _restartScanNextFrame = false;
                StartScanIfNeeded();
            }
            ExpireTimedOutPending();
        }

        private void TryWrite(BleLink link, byte[] data, Action<bool> onWriteResult)
        {
            if (!link.IsConnected || string.IsNullOrEmpty(link.DeviceId))
            {
                onWriteResult?.Invoke(false);
                return;
            }
            string service = NormalizeUuid(link.Profile.serviceUuid);
            string ch = NormalizeUuid(link.Profile.writeCharacteristicUuid);
            if (string.IsNullOrEmpty(service) || string.IsNullOrEmpty(ch))
            {
                onWriteResult?.Invoke(false);
                return;
            }
            // Serialize writes through the queue (RunParallel=false) and wait for the jar's
            // CharacteristicWrite ack. Android only allows one outstanding GATT write at a time;
            // fire-and-forget back-to-back writes get dropped/coalesced by Android and corrupt the
            // device's COBS framing, so StimulHeat never sees a clean frame. For a fire-and-forget
            // request (no ack callback, e.g. Nebula commands) we still queue an AckedWrite but with a
            // null callback so it serializes the same way without stalling the caller.
            //
            // The vendored jar never negotiates MTU, so the connection stays at the BLE default
            // 23-byte MTU = 20 bytes usable per write. An oversize write is silently truncated, so
            // a 39-byte COBS frame never delivers its 0x00 delimiter and the firmware never
            // decodes it. The firmware reassembles COBS by accumulating bytes across writes, so we
            // split each outgoing frame into AndroidMtuPayload-byte chunks and queue each as a
            // serialized AckedWrite. The caller's onWriteResult fires once, after the last chunk
            // acks (or on the first chunk failure), so StimulHeat's SendAcked retry logic works.
            if (data.Length <= AndroidMtuPayload)
            {
                _ble.QueueCommand(new AckedWrite(link.DeviceId, service, ch, data, onWriteResult));
                return;
            }
            QueueChunkedWrite(link.DeviceId, service, ch, data, onWriteResult);
        }

        // Splits an oversize frame into AndroidMtuPayload-byte chunks, queuing each as a
        // serialized AckedWrite. onWriteResult fires exactly once: true when the final chunk
        // acks, false on the first chunk failure/timeout. Chunks serialize through the queue so
        // Android gets one outstanding write at a time and the firmware reassembles the COBS
        // frame in order.
        private void QueueChunkedWrite(string deviceId, string service, string ch, byte[] data, Action<bool> onWriteResult)
        {
            int chunkCount = (data.Length + AndroidMtuPayload - 1) / AndroidMtuPayload;
            var coord = new ChunkedWriteCoordinator(chunkCount, onWriteResult);
            for (int offset = 0, i = 0; offset < data.Length; offset += AndroidMtuPayload, i++)
            {
                int len = System.Math.Min(AndroidMtuPayload, data.Length - offset);
                var chunk = new byte[len];
                System.Array.Copy(data, offset, chunk, 0, len);
                bool isLast = i == chunkCount - 1;
                _ble.QueueCommand(new AckedWrite(deviceId, service, ch, chunk, isLast ? coord.LastAck : coord.IntermediateAck));
            }
        }

        // Aggregates acks across the chunks of a single logical write. onWriteResult fires once.
        private sealed class ChunkedWriteCoordinator
        {
            private readonly Action<bool> _onResult;
            private int _remaining;
            private int _failed;
            public ChunkedWriteCoordinator(int chunkCount, Action<bool> onResult)
            {
                _remaining = chunkCount;
                _onResult = onResult;
            }
            // Any chunk failure fails the whole logical write and short-circuits further acks.
            public void IntermediateAck(bool ok)
            {
                if (!ok) { Fail(); return; }
                System.Threading.Interlocked.Decrement(ref _remaining);
            }
            // The last chunk completing successfully completes the whole logical write.
            public void LastAck(bool ok)
            {
                if (!ok) { Fail(); return; }
                if (System.Threading.Interlocked.Decrement(ref _remaining) == 0)
                    _onResult?.Invoke(true);
            }
            private void Fail()
            {
                if (System.Threading.Interlocked.Exchange(ref _failed, 1) == 0)
                    _onResult?.Invoke(false);
            }
        }

        private void ExpireTimedOutPending()
        {
            float now = Time.realtimeSinceStartup;
            List<BleLink> timedOut = null;
            lock (_lock)
            {
                for (int i = _pending.Count - 1; i >= 0; i--)
                {
                    var p = _pending[i];
                    if (p.Link.DeviceId != null) continue;
                    if (now - p.RequestedAt >= LinkTimeoutSeconds)
                    {
                        timedOut ??= new List<BleLink>();
                        timedOut.Add(p.Link);
                        _pending.RemoveAt(i);
                    }
                }
            }
            if (timedOut != null)
            {
                foreach (var l in timedOut) l.SignalConnectionLost("device not found within " + LinkTimeoutSeconds + "s");
            }
        }

        // =========================================================
        // Release / teardown
        // =========================================================
        internal void ReleaseLink(BleLink link)
        {
            string id = link.DeviceId;
            if (id == null) return;
            lock (_lock)
            {
                _pending.RemoveAll(p => p.Link == link);
                _connected.Remove(id);
            }
            // Disconnect the peripheral and unsubscribe, best-effort.
            if (_connectCommands.TryGetValue(id, out var connect))
            {
                try { connect.Disconnect(); } catch { }
                _connectCommands.Remove(id);
            }
            if (_subscribeCommands.TryGetValue(id, out var subs))
            {
                foreach (var s in subs) { try { s.Unsubscribe(); } catch { } }
                _subscribeCommands.Remove(id);
            }
        }

        internal void StopAll()
        {
            // Called by BleConnectionManager.StopNative when the last link is released.
            List<ConnectToDevice> connects;
            List<List<SubscribeToCharacteristic>> subs;
            lock (_lock)
            {
                connects = new List<ConnectToDevice>(_connectCommands.Values);
                subs = new List<List<SubscribeToCharacteristic>>(_subscribeCommands.Values);
                _connectCommands.Clear();
                _subscribeCommands.Clear();
                _connected.Clear();
                _pending.Clear();
            }
            foreach (var s in subs) foreach (var c in s) { try { c.Unsubscribe(); } catch { } }
            foreach (var c in connects) { try { c.Disconnect(); } catch { } }
            // Stop an in-flight scan: DiscoverDevices.End() sends the Java "stopScanBleDevices".
            if (_scanRunning && _scanCommand != null) { try { _scanCommand.End(); } catch { } }
            _scanRunning = false;
            _scanCommand = null;
        }

        // =========================================================
        // UUID normalization: everything -> full 128-bit lowercase, no braces.
        // 16-bit short UUIDs (e.g. "180c", "2a56") expand to the Bluetooth base UUID
        // 0000xxxx-0000-1000-8000-00805f9b34fb so the customGatt=true path matches.
        // =========================================================
        private static string NormalizeUuid(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string s = raw.Trim().Trim('{', '}').Trim().ToLowerInvariant();
            if (s.Length == 4)
            {
                return "0000" + s + "-0000-1000-8000-00805f9b34fb";
            }
            if (s.Length == 8)
            {
                // e.g. "00002a56" -> treat as 16-bit embedded in first 32 bits
                return s.Substring(0, 8) + "-0000-1000-8000-00805f9b34fb";
            }
            return s;
        }
    }
}
#endif
