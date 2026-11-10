#if (UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN) && !UNITY_ANDROID
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Lirisxr.BleBridge
{
    /// <summary>
    /// Windows BLE backend for BleBridge, backed by BleWinrtDll (adabru).
    ///
    /// Threading model: a single dedicated background thread ("BleWinrtPump") owns ALL BleApi
    /// calls, using the blocking poll variants (block=true), exactly like the upstream threaded
    /// reference (Joelx/BleWinrtDll-Unity-Demo). The BleWinrtDll author explicitly recommends
    /// threads over the non-blocking main-thread Demo scene; the previous version of this backend
    /// drove every non-blocking call from Update() on the Unity main thread, which stalled the
    /// GATT discovery state machine and froze the app whenever a BLE link was requested
    /// (reproduced for both Nebula and StimulHeat).
    ///
    /// Quit() race that motivated the old main-thread design is avoided by ownership rules rather
    /// than by avoiding threads: exactly ONE thread (the pump) ever touches the DLL, and Quit() is
    /// called only from StopAll() AFTER _running is cleared and the pump thread is joined. So the
    /// pump is never inside the DLL when Quit() runs. ReleaseLink() moves a link out of the
    /// in-progress sets under the lock; the pump notices on its next iteration and stops servicing
    /// it, so a single disconnect never tears the whole stack (only the last link going away does,
    /// via StopAll()).
    ///
    /// BleLink events (SignalConnected / Deliver / SignalConnectionLost) are raised on the pump
    /// thread; consumers marshal to the main thread themselves (NebulaManager via
    /// EnqueueNotification, StimulHeatDevicesManager mutates device fields directly) per the
    /// IBleLink contract.
    /// </summary>
    internal sealed class BleWinrtBackend : MonoBehaviour
    {
        public static BleWinrtBackend Instance
        {
            get
            {
                if (_instance != null) return _instance;
                var go = new GameObject(nameof(BleWinrtBackend));
                _instance = go.AddComponent<BleWinrtBackend>();
                return _instance;
            }
        }
        private static BleWinrtBackend _instance;

        private enum LinkPhase { Pending, Ready, Failed }

        private class LinkState
        {
            public BleLink Link;
            public LinkPhase Phase;
            public string ServiceUuid;
            public string WriteUuid;
            public readonly HashSet<string> Subscribed = new HashSet<string>();
        }

        private readonly List<LinkState> _pending = new List<LinkState>();
        private readonly List<LinkState> _active = new List<LinkState>();
        private readonly Dictionary<string, ScannedEntry> _seen = new Dictionary<string, ScannedEntry>();
        private readonly Dictionary<string, LinkState> _byDeviceId = new Dictionary<string, LinkState>();
        private readonly ConcurrentQueue<WriteRequest> _writes = new ConcurrentQueue<WriteRequest>();
        private readonly object _lock = new object();

        private Thread _thread;
        private volatile bool _running;
        private volatile bool _scanUp;
        private string _lastError;

        private struct ScannedEntry { public string name; public bool connectable; }
        private struct WriteRequest { public LinkState State; public byte[] Data; public Action<bool> OnResult; }

        // Settle delay between finishing service enumeration and starting characteristic
        // enumeration, matching the upstream Joelx Thread.Sleep(200). BleWinrtDll's shared
        // queue needs this pause to flush the service scan before the characteristic scan begins.
        private const int ProfileSettleMs = 200;

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
            EnsurePumpStarted();
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                StopAll();
                _instance = null;
            }
        }

        private void OnApplicationQuit() => StopAll();

        // The main thread no longer touches the DLL. Update() is a no-op kept only so Unity
        // recognises us as a ticking MonoBehaviour and so this component stays alive. All work
        // is done by the background pump thread.
        private void Update() { }

        private void EnsurePumpStarted()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(PumpLoop) { IsBackground = true, Name = "BleWinrtPump" };
            _thread.Start();
        }

        // =========================================================
        // Link registration (called by BleConnectionManager on the main thread)
        // =========================================================
        internal void RequestLink(BleLink link)
        {
            lock (_lock) _pending.Add(new LinkState { Link = link, Phase = LinkPhase.Pending });
            EnsurePumpStarted();
        }

        internal void EnqueueWrite(BleLink link, byte[] data, Action<bool> onResult)
        {
            LinkState ls = null;
            lock (_lock) _byDeviceId.TryGetValue(link.DeviceId, out ls);
            if (ls == null || ls.Phase != LinkPhase.Ready)
            {
                onResult?.Invoke(false);
                return;
            }
            _writes.Enqueue(new WriteRequest { State = ls, Data = data, OnResult = onResult });
        }

        internal void ReleaseLink(BleLink link)
        {
            lock (_lock)
            {
                _pending.RemoveAll(ls => ls.Link == link);
                _active.RemoveAll(ls => ls.Link == link);
                if (link.DeviceId != null) _byDeviceId.Remove(link.DeviceId);
            }
        }

        // =========================================================
        // Background pump (the ONLY thread touching BleApi)
        // =========================================================
        private void PumpLoop()
        {
            try
            {
                while (_running)
                {
                    PumpScan();
                    PumpIncoming();
                    DrainErrors();
                    DrainWrites();

                    // While there is at least one Ready link we must keep polling data with short
                    // sleeps; otherwise a light idle so the thread isn't a busy loop.
                    bool anyReady;
                    lock (_lock) anyReady = _active.Count > 0;
                    Thread.Sleep(anyReady ? 10 : 50);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[BleBridge][WinRT] pump thread crashed: {e}");
            }
            finally
            {
                // StopDeviceScan is safe here: the pump owns the scan and is about to exit.
                try { if (_scanUp) BleApi.StopDeviceScan(); } catch { }
                _scanUp = false;
            }
        }

        // BleWinrtDll uses a single shared internal queue for device scan, service, and
        // characteristic enumeration. If the device scan keeps producing events while
        // ScanServices/PollService run, PollService can stall and never reach FINISHED, so the
        // link never becomes Ready and the consumer's connect timeout fires. The proven upstream
        // reference (Joelx) stops the device scan BEFORE enumerating services/characteristics and
        // inserts a settle delay between service and characteristic enumeration. We do the same:
        // on a match, stop the scan, run the full profile enumeration synchronously here, then
        // re-arm the scan if other links are still pending.
        private void PumpScan()
        {
            bool needScan;
            lock (_lock) needScan = _pending.Count > 0;
            if (needScan && !_scanUp)
            {
                BleApi.StartDeviceScan();
                _scanUp = true;
                _seen.Clear();
            }
            if (!needScan && _scanUp)
            {
                BleApi.StopDeviceScan();
                _scanUp = false;
                return;
            }
            if (!_scanUp) return;

            // Blocking poll: returns AVAILABLE per device, FINISHED when the scan completes.
            BleApi.ScanStatus status;
            var res = new BleApi.DeviceUpdate();
            do
            {
                status = BleApi.PollDevice(out res, true);
                if (status == BleApi.ScanStatus.AVAILABLE)
                {
                    if (!_seen.TryGetValue(res.id, out var entry)) entry = new ScannedEntry();
                    if (res.nameUpdated) entry.name = res.name;
                    if (res.isConnectableUpdated) entry.connectable = res.isConnectable;
                    _seen[res.id] = entry;
                    if (entry.connectable && !string.IsNullOrEmpty(entry.name))
                    {
                        LinkState match = null;
                        lock (_lock)
                        {
                            for (int i = 0; i < _pending.Count; i++)
                            {
                                var ls = _pending[i];
                                if (ls.Phase != LinkPhase.Pending) continue;
                                var name = ls.Link.Profile.deviceName;
                                if (name != null &&
                                    entry.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    match = ls;
                                    _pending.RemoveAt(i);
                                    break;
                                }
                            }
                        }
                        if (match != null)
                        {
                            match.Link.SetDeviceId(res.id);
                            lock (_lock) _active.Add(match);
                            // STOP the scan before enumerating the GATT profile (shared-queue rule).
                            try { BleApi.StopDeviceScan(); } catch { }
                            _scanUp = false;
                            EnumerateProfile(match);
                            // Re-arm the scan if other links are still pending; profile enumeration
                            // is done synchronously in EnumerateProfile(), not via phased states.
                            bool morePending;
                            lock (_lock) morePending = _pending.Count > 0;
                            if (morePending && _running)
                            {
                                BleApi.StartDeviceScan();
                                _scanUp = true;
                                _seen.Clear();
                                // Keep draining this scan batch for remaining pending links.
                            }
                            else
                            {
                                // No more pending links: the scan is down. Break out of the poll loop
                                // so we never block in PollDevice(true) with no scan running.
                                break;
                            }
                        }
                    }
                }
                else if (status == BleApi.ScanStatus.FINISHED)
                {
                    BleApi.StopDeviceScan();
                    _scanUp = false;
                    break;
                }
            }
            while (status == BleApi.ScanStatus.AVAILABLE && _running);
        }

        // Full Joelx-style profile enumeration for one link on the pump thread: ScanServices ->
        // blocking PollService until the target service is seen or FINISHED -> settle delay ->
        // ScanCharacteristics -> blocking PollCharacteristic, subscribing to notify chars and
        // caching the write char -> Ready. The device scan is already stopped by the caller.
        private void EnumerateProfile(LinkState ls)
        {
            if (!FindService(ls)) return;                 // fails the link on FINISHED/abort
            Thread.Sleep(ProfileSettleMs);                // settle between service and char scan
            if (!_running) { FailLink(ls, "shutdown during profile enumeration"); return; }
            EnumerateCharacteristics(ls);                 // fails the link or sets Ready
        }

        private bool FindService(LinkState ls)
        {
            BleApi.ScanServices(ls.Link.DeviceId);
            BleApi.ScanStatus status;
            var svc = new BleApi.Service();
            do
            {
                status = BleApi.PollService(out svc, true);
                if (status == BleApi.ScanStatus.AVAILABLE)
                {
                    if (UuidContains(svc.uuid, ls.Link.Profile.serviceUuid))
                    {
                        ls.ServiceUuid = svc.uuid;
                        ls.Link.SetServiceUuid(svc.uuid);
                        return true;
                    }
                }
                else if (status == BleApi.ScanStatus.FINISHED)
                {
                    FailLink(ls, $"service {ls.Link.Profile.serviceUuid} not found on device {ls.Link.DeviceId}");
                    return false;
                }
            }
            while (status == BleApi.ScanStatus.AVAILABLE && _running);
            if (!_running) { FailLink(ls, "shutdown during service enumeration"); return false; }
            return false;
        }

        private void EnumerateCharacteristics(LinkState ls)
        {
            BleApi.ScanCharacteristics(ls.Link.DeviceId, ls.ServiceUuid);
            BleApi.ScanStatus status;
            var ch = new BleApi.Characteristic();
            do
            {
                status = BleApi.PollCharacteristic(out ch, true);
                if (status == BleApi.ScanStatus.AVAILABLE)
                {
                    if (ls.Link.Profile.notifyCharacteristicUuids != null)
                    {
                        foreach (string want in ls.Link.Profile.notifyCharacteristicUuids)
                        {
                            if (UuidContains(ch.uuid, want) && !ls.Subscribed.Contains(ch.uuid))
                            {
                                BleApi.SubscribeCharacteristic(ls.Link.DeviceId, ls.ServiceUuid, ch.uuid);
                                ls.Subscribed.Add(ch.uuid);
                                ls.Link.RegisterSubscribed(ch.uuid);
                            }
                        }
                    }
                    if (ls.Link.Profile.writeCharacteristicUuid != null && UuidContains(ch.uuid, ls.Link.Profile.writeCharacteristicUuid))
                        ls.WriteUuid = ch.uuid;
                }
                else if (status == BleApi.ScanStatus.FINISHED)
                {
                    if (ls.WriteUuid != null) ls.Link.SetWriteUuid(ls.WriteUuid);
                    lock (_lock) _byDeviceId[ls.Link.DeviceId] = ls;
                    ls.Phase = LinkPhase.Ready;
                    ls.Link.SignalConnected();
                    return;
                }
            }
            while (status == BleApi.ScanStatus.AVAILABLE && _running);
            if (!_running) FailLink(ls, "shutdown during characteristic enumeration");
        }

        // Discovery no longer has a phased state machine: a matched link's whole GATT profile is
        // enumerated synchronously in EnumerateProfile() while the device scan is stopped (see
        // PumpScan). Once a link is Ready it is only pumped for incoming data here.

        private void PumpIncoming()
        {
            var data = new BleApi.BLEData();
            while (BleApi.PollData(out data, false))
            {
                if (data.size <= 0) continue;
                LinkState target = null;
                lock (_lock) _byDeviceId.TryGetValue(data.deviceId, out target);
                target?.Link.Deliver(data.buf, data.size, data.deviceId, data.serviceUuid, data.characteristicUuid);
            }
        }

        private void DrainErrors()
        {
            var err = new BleApi.ErrorMessage();
            BleApi.GetError(out err);
            if (!string.IsNullOrEmpty(err.msg) && err.msg != _lastError)
            {
                _lastError = err.msg;
                Debug.LogWarning($"[BleBridge][DLL] {err.msg}");
            }
        }

        // Writes are split by ack policy:
        //  - Fire-and-forget (OnResult == null): non-blocking SendData(false) on the pump thread.
        //    Safe (returns immediately, touches no shared queues) and cheap, so Nebula commands
        //    stay low-latency and never spawn a thread.
        //  - Acknowledged (OnResult != null): SendData(true) on a dedicated ThreadPool thread, NOT
        //    on the pump. SendData(true) blocks until the GATT write completes; running it on the
        //    pump would stall scan/data polling for every link and serialize all acked writes
        //    behind discovery (observed as large StimulHeat latency once a second link is active).
        //
        // Thread safety: SendData(true) uses its own local mutex/condition_variable (see the
        // BleWinrtDll C++ source) and retrieveCharacteristic only reads the permissive `cache`
        // (BluetoothCacheMode::Cached); it does NOT touch the shared deviceQueue/serviceQueue/
        // characteristicQueue that the pump's Poll* calls consume. So an off-thread acked write
        // never races the pump's scan/discovery state machine. The only shared state it touches
        // is quitLock/quitFlag (quittable wait) and saveError, both safe.
        private void DrainWrites()
        {
            while (_writes.TryDequeue(out var w))
            {
                if (w.State.Phase != LinkPhase.Ready) { w.OnResult?.Invoke(false); continue; }
                var pkt = new BleApi.BLEData
                {
                    buf = new byte[512],
                    size = (short)Math.Min(w.Data.Length, 512),
                    deviceId = w.State.Link.DeviceId,
                    serviceUuid = w.State.ServiceUuid,
                    characteristicUuid = w.State.WriteUuid
                };
                Array.Copy(w.Data, pkt.buf, pkt.size);
                if (w.OnResult == null)
                {
                    // Fire-and-forget: non-blocking on the pump thread. SendData(false) returns
                    // immediately and does not touch the shared scan/data queues.
                    try { BleApi.SendData(in pkt, false); } catch { }
                }
                else
                {
                    // Acknowledged: block on a worker thread so the pump keeps polling data and
                    // serving other links while this GATT write is in flight.
                    var onResult = w.OnResult;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        bool ok = false;
                        try { ok = BleApi.SendData(in pkt, true); } catch { ok = false; }
                        try { onResult(ok); } catch { }
                    });
                }
            }
        }

        // =========================================================
        // Release / teardown (main thread -> stops pump, then Quit() with no race)
        // =========================================================
        internal void StopAll()
        {
            _running = false;
            // Unblock the pump first: it is blocked in PollDevice(block=true). StopDeviceScan()
            // is safe to call from this (main) thread while the pump is inside PollDevice -- it is
            // exactly how the upstream Joelx demo cancels a scan (BLEScan.Cancel -> StopDeviceScan).
            // Without this the Join below would wait for the scan to finish on its own.
            try { BleApi.StopDeviceScan(); } catch { }
            _scanUp = false;
            try { _thread?.Join(2000); } catch { }
            _thread = null;

            lock (_lock)
            {
                _pending.Clear();
                _active.Clear();
                _byDeviceId.Clear();
            }
            try { BleApi.Quit(); } catch { } // pump is stopped -> no DLL race
            while (_writes.TryDequeue(out _)) { }
        }

        private void FailLink(LinkState ls, string reason)
        {
            ls.Phase = LinkPhase.Failed;
            lock (_lock) _active.Remove(ls);
            ls.Link.SignalConnectionLost(reason);
        }

        private static bool UuidContains(string uuid, string want)
            => !string.IsNullOrEmpty(uuid) && !string.IsNullOrEmpty(want)
               && uuid.ToLowerInvariant().Contains(want.ToLowerInvariant());
    }
}
#endif
