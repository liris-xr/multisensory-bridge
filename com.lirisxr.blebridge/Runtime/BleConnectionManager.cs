using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
namespace Lirisxr.BleBridge
{
    /// <summary>
    /// The single shared BLE connection multiplexer.
    ///
    /// Root cause this fixes: BleWinrtDll is a global singleton with one scan stream and one
    /// Quit() that releases ALL links. When Nebula and StimulHeat each imported the DLL and
    /// called BleApi directly, the DLL loaded twice (build-time conflict) and either one
    /// calling Quit() tore the other's link down.
    ///
    /// BleConnectionManager is the ONLY code that imports BleWinrtDll (Windows) and the ONLY
    /// code that calls Quit(). It runs one scan, fans discovered devices out to the right
    /// IBleLink by name + service UUID, and ref-counts the native stack lifetime across all
    /// active links. Device packages only ever RequestLink()/Dispose() an IBleLink.
    ///
    /// Platform switching is automatic via Unity defines:
    ///   - Windows (Editor/Standalone): BleWinrtDll, background polling thread.
    ///   - Android: Velorexe Unity-Android-Bluetooth-Low-Energy, event-driven via the
    ///     BleAndroidBackend MonoBehaviour (no polling thread). Delegated to here.
    /// </summary>
    public class BleConnectionManager : IDisposable
    {
        public static BleConnectionManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        if (_instance == null)
                            _instance = new BleConnectionManager();
                    }
                }
                return _instance;
            }
        }
        private static BleConnectionManager _instance;
        private static readonly object _instanceLock = new object();

        private readonly List<BleLink> _links = new List<BleLink>();
        private readonly List<BleLink> _pending = new List<BleLink>();
        private readonly Dictionary<string, BleLink> _connectedByDeviceId = new Dictionary<string, BleLink>();
        private readonly object _lock = new object();
        private Thread _thread;
        private volatile bool _running;

        private BleConnectionManager() { }

        /// <summary>
        /// Requests a BLE link for one device. Returns a link whose events will fire once
        /// the device is discovered and subscribed. The link is connected asynchronously;
        /// poll IsConnected or subscribe to OnConnected.
        /// </summary>
        public IBleLink RequestLink(BleDeviceProfile profile)
        {
            var link = new BleLink(this, profile);
            lock (_lock)
            {
                _pending.Add(link);
                _links.Add(link);
            }
            EnsureStarted();
            StartLinkPlatform(link);
            return link;
        }

        internal void ReleaseLink(BleLink link)
        {
            bool anyLeft;
            lock (_lock)
            {
                _pending.Remove(link);
                _links.Remove(link);
                if (link.DeviceId != null)
                    _connectedByDeviceId.Remove(link.DeviceId);
                anyLeft = _links.Count > 0;
            }
            ReleaseLinkPlatform(link);
            // Ref-count the native stack: tear it down only when the last link goes away.
            if (!anyLeft) StopNative();
        }

        // =========================================================
        // Stack lifecycle
        // =========================================================
        private void EnsureStarted()
        {
#if (UNITY_ANDROID && !UNITY_EDITOR) || ((UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN) && !UNITY_ANDROID)
            // Both backends are event-driven MonoBehaviours (BleAndroidBackend / BleWinrtBackend)
            // that poll on the main thread; no background polling thread is started here.
#else
            if (_running) return;
            _running = true;
            _thread = new Thread(Run) { IsBackground = true, Name = "BleConnectionManager" };
            _thread.Start();
#endif
        }

        private void Run()
        {
            try
            {
                while (_running)
                {
                    Pump();
                    Thread.Sleep(10);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[BleBridge] Manager thread crashed: {e}");
            }
            finally
            {
                StopNative();
            }
        }

#if (UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN) && !UNITY_ANDROID
        // =========================================================
        // Windows WinRT backend (BleWinrtDll).
        // All native interaction is delegated to BleWinrtBackend, a MonoBehaviour that runs a
        // single dedicated background pump thread driving BleWinrtDll with blocking polls. The
        // manager itself starts no thread here. Quit() ownership stays in BleWinrtBackend, which
        // stops its pump before calling Quit() so there is no DLL race.
        // =========================================================
        private void StartLinkPlatform(BleLink link)
        {
            BleWinrtBackend.Instance.RequestLink(link);
        }

        private void ReleaseLinkPlatform(BleLink link)
        {
            BleWinrtBackend.Instance.ReleaseLink(link);
        }

        // Pump()/Run() are never called on Windows (EnsureStarted starts no thread).
        private void Pump() { }

        internal void NativeWrite(BleLink link, byte[] data, Action<bool> onWriteResult)
        {
            if (!link.IsConnected)
            {
                onWriteResult?.Invoke(false);
                return;
            }
            BleWinrtBackend.Instance.EnqueueWrite(link, data, onWriteResult);
        }

#elif UNITY_ANDROID && !UNITY_EDITOR
        // =========================================================
        // Android backend (Velorexe) — event-driven, no polling thread.
        // All native interaction is delegated to BleAndroidBackend,
        // a MonoBehaviour that drives Velorexe's command queue on the
        // main thread and marshals Java callbacks back into BleLink.
        // =========================================================
        private void StartLinkPlatform(BleLink link)
        {
            BleAndroidBackend.Instance.RequestLink(link);
        }

        private void ReleaseLinkPlatform(BleLink link)
        {
            BleAndroidBackend.Instance.ReleaseLink(link);
        }

        // Pump()/Run() are never called on Android (EnsureStarted starts no thread).
        private void Pump() { }

        internal void NativeWrite(BleLink link, byte[] data, Action<bool> onWriteResult)
        {
            if (!link.IsConnected)
            {
                onWriteResult?.Invoke(false);
                return;
            }
            BleAndroidBackend.Instance.EnqueueWrite(link, data, onWriteResult);
        }

#else
        // =========================================================
        // No native backend available on this platform.
        // =========================================================
        private void StartLinkPlatform(BleLink link) { }
        private void ReleaseLinkPlatform(BleLink link) { }
        private void Pump() { Thread.Sleep(50); }
        internal void NativeWrite(BleLink link, byte[] data, Action<bool> onWriteResult) { onWriteResult?.Invoke(false); }
#endif

        private void StopNative()
        {
#if (UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN) && !UNITY_ANDROID
            try { BleWinrtBackend.Instance.StopAll(); } catch { } // main-thread only -> no DLL race
            _connectedByDeviceId.Clear();
#elif UNITY_ANDROID && !UNITY_EDITOR
            try { BleAndroidBackend.Instance.StopAll(); } catch { }
            _connectedByDeviceId.Clear();
#else
            // No native backend on this platform.
#endif
        }

        public void Dispose()
        {
            _running = false;
            try { _thread?.Join(1000); } catch { }
            lock (_instanceLock) { if (_instance == this) _instance = null; }
        }
    }
}
