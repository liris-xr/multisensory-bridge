using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Lirisxr.BleBridge.Editor
{
    public class BleBridgeManifestWizard : EditorWindow
    {
        const string PackageName = "com.lirisxr.blebridge";

        static string AssetsManifestPath => Path.Combine(Directory.GetCurrentDirectory(), "Assets/Plugins/Android/AndroidManifest.xml");

        static string PackageManifestPath => Path.GetFullPath($"Packages/{PackageName}/Plugins/Android/AndroidManifest.xml");

        static bool _forcePreSdk31;
        static string _existingManifest;
        static bool _hasExisting;

        static readonly PermissionEntry[] Permissions =
        {
            new PermissionEntry(
                new Regex(@"uses-permission[^>]+android:name=""android\.permission\.BLUETOOTH"""),
                "Legacy Bluetooth access (pre API 31)",
                "<uses-permission android:name=\"android.permission.BLUETOOTH\" android:maxSdkVersion=\"30\" />"),
            new PermissionEntry(
                new Regex(@"uses-permission[^>]+android:name=""android\.permission\.BLUETOOTH_ADMIN"""),
                "Legacy Bluetooth admin access (pre API 31)",
                "<uses-permission android:name=\"android.permission.BLUETOOTH_ADMIN\" android:maxSdkVersion=\"30\" />"),
            new PermissionEntry(
                new Regex(@"uses-permission[^>]+android:name=""android\.permission\.BLUETOOTH_SCAN"""),
                "Bluetooth scan (API 31+)",
                "<uses-permission android:name=\"android.permission.BLUETOOTH_SCAN\" android:usesPermissionFlags=\"neverForLocation\" />",
                31),
            new PermissionEntry(
                new Regex(@"uses-permission[^>]+android:name=""android\.permission\.BLUETOOTH_CONNECT"""),
                "Bluetooth connect (API 31+)",
                "<uses-permission android:name=\"android.permission.BLUETOOTH_CONNECT\" />",
                31),
            new PermissionEntry(
                new Regex(@"uses-permission[^>]+android:name=""android\.permission\.ACCESS_FINE_LOCATION"""),
                "Fine location (BLE scan on API 29-30)",
                "<uses-permission android:name=\"android.permission.ACCESS_FINE_LOCATION\" />"),
        };

        readonly struct PermissionEntry
        {
            public readonly Regex Regex;
            public readonly string Description;
            public readonly string Line;
            public readonly int MinSdkVersion;

            public PermissionEntry(Regex regex, string description, string line, int minSdkVersion = 0)
            {
                Regex = regex;
                Description = description;
                Line = line;
                MinSdkVersion = minSdkVersion;
            }

            public bool Exists(string manifest) => Regex.IsMatch(manifest);
        }

        [MenuItem("Window/BLE Bridge/Android Manifest")]
        public static void Open()
        {
            var window = GetWindow<BleBridgeManifestWizard>(false, "BLE Bridge Manifest", true);
            window.minSize = new Vector2(420, 220);
            Refresh();
        }

        static BleBridgeManifestWizard s_Instance;

        public static bool IsOpen => s_Instance != null;

        public void OnEnable()
        {
            s_Instance = this;
            Refresh();
        }

        public void OnDestroy() => s_Instance = null;

        static void Refresh()
        {
            _hasExisting = File.Exists(AssetsManifestPath);
            _existingManifest = _hasExisting ? File.ReadAllText(AssetsManifestPath) : null;
        }

        public static void RefreshStatic()
        {
            Refresh();
            if (s_Instance != null) s_Instance.Repaint();
        }

#if UNITY_2022_2_OR_NEWER
        public static void WarnIfGameActivityOnly()
        {
            var entry = PlayerSettings.Android.applicationEntry;
            if (entry == AndroidApplicationEntry.GameActivity)
            {
                Debug.LogWarning(
                    "[BleBridge] Android application entry point is GameActivity. BLE Bridge uses a " +
                    "pure-Java plugin (Velorexe) that requires the Activity entry point " +
                    "(UnityPlayerActivity). Open Window/BLE Bridge/Android Manifest to switch it, or " +
                    "the APK will build but fail to launch.");
            }
        }
#endif

        public void OnGUI()
        {
            var bold = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };

            EditorGUILayout.LabelField("BLE Bridge - Android Manifest", bold);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(
                "On Android 12+ (API 31), Android auto-denies BLUETOOTH_SCAN / BLUETOOTH_CONNECT with " +
                "no dialog if they are missing from the final merged manifest. A project-level " +
                "Assets/Plugins/Android/AndroidManifest.xml is always merged by Unity, so the runtime " +
                "permission request can actually show a dialog.", EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space();

            if (_hasExisting)
            {
                EditorGUILayout.LabelField("AndroidManifest.xml found:", bold);
                EditorGUILayout.LabelField(AssetsManifestPath, EditorStyles.wordWrappedLabel);
                EditorGUILayout.Space();

                DrawPermissionStatus();
                EditorGUILayout.Space();
                if (GUILayout.Button("Add missing permissions"))
                {
                    MergeMissingPermissions();
                    Refresh();
                    GUIUtility.ExitGUI();
                }
            }
            else
            {
                EditorGUILayout.LabelField("No Assets/Plugins/Android/AndroidManifest.xml found.", bold);
                EditorGUILayout.LabelField(
                    "Create one now with the BLE runtime permissions and the Unity launcher activity. " +
                    "If a manifest already exists later, use Add missing permissions to merge only the " +
                    "<uses-permission> lines.",
                    EditorStyles.wordWrappedLabel);
                EditorGUILayout.Space();
                if (GUILayout.Button("Create permissions manifest"))
                {
                    CreatePermissionsManifest();
                    Refresh();
                    GUIUtility.ExitGUI();
                }
            }

            EditorGUILayout.Space();
            DrawEntryPoint();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(
                "Menu: Window/BLE Bridge/Android Manifest reopens this window later.",
                EditorStyles.miniLabel);
        }

#if UNITY_2022_2_OR_NEWER
        static readonly string[] EntryPointNames = { "Activity (UnityPlayerActivity)", "GameActivity", "Activity + GameActivity" };
        static readonly AndroidApplicationEntry[] EntryPointValues =
        {
            AndroidApplicationEntry.Activity,
            AndroidApplicationEntry.GameActivity,
            AndroidApplicationEntry.Activity | AndroidApplicationEntry.GameActivity,
        };

        void DrawEntryPoint()
        {
            var bold = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            EditorGUILayout.LabelField("Application entry point", bold);
            EditorGUILayout.LabelField(
                "BLE Bridge uses a pure-Java Android plugin (Velorexe jar). The GameActivity entry " +
                "point is unsuitable for pure-Java plugins; the classic Activity entry point " +
                "(com.unity3d.player.UnityPlayerActivity) must be selected, or the APK builds but " +
                "cannot launch.",
                EditorStyles.wordWrappedLabel);

            var current = PlayerSettings.Android.applicationEntry;
            int idx = System.Array.IndexOf(EntryPointValues, current);
            if (idx < 0) idx = 0;
            var sel = GUILayout.SelectionGrid(idx, EntryPointNames, 1, EditorStyles.radioButton);
            if (sel != idx)
            {
                PlayerSettings.Android.applicationEntry = EntryPointValues[sel];
                Debug.Log($"[BleBridge] Set Android application entry point to {EntryPointValues[sel]}.");
            }
        }
#else
        void DrawEntryPoint()
        {
            EditorGUILayout.HelpBox(
                "This Unity version predates PlayerSettings.Android.applicationEntry (added 2022.2). " +
                "If you are on 2022.2+, set the Android Application Entry Point to Activity " +
                "(not GameActivity) for the pure-Java BLE plugin to work.",
                MessageType.Info);
        }
#endif

        void DrawPermissionStatus()
        {
            var targetSdk = (int)PlayerSettings.Android.targetSdkVersion;
            if (targetSdk == 0)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Android API Level");
                if (GUILayout.Button("API 31+ (suggested)")) _forcePreSdk31 = false;
                if (GUILayout.Button("Pre-31")) _forcePreSdk31 = true;
                EditorGUILayout.EndHorizontal();
                targetSdk = _forcePreSdk31 ? 0 : 31;
            }

            var ok = EditorGUIUtility.IconContent("TestPassed").image;
            var fail = EditorGUIUtility.IconContent("console.erroricon").image;
            var h = EditorGUIUtility.singleLineHeight;

            foreach (var perm in Permissions)
            {
                if (targetSdk < perm.MinSdkVersion) continue;
                var present = perm.Exists(_existingManifest);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(present ? ok : fail, GUILayout.Width(h), GUILayout.Height(h));
                EditorGUILayout.LabelField(perm.Description);
                EditorGUILayout.EndHorizontal();
            }
        }

        static void CreatePermissionsManifest()
        {
            var dir = Path.GetDirectoryName(AssetsManifestPath);
            Directory.CreateDirectory(dir);
            File.WriteAllText(AssetsManifestPath, TemplateManifest());
            AssetDatabase.ImportAsset("Assets/Plugins/Android/AndroidManifest.xml", ImportAssetOptions.ForceUpdate);
            Debug.Log($"[BleBridge] Created {AssetsManifestPath}");
        }

        static void MergeMissingPermissions()
        {
            var content = _existingManifest;
            var insert = "";
            foreach (var perm in Permissions)
            {
                if (perm.Exists(content)) continue;
                insert += "    " + perm.Line + "\n";
            }
            if (string.IsNullOrEmpty(insert))
            {
                Debug.Log("[BleBridge] All BLE permissions already present in manifest.");
                return;
            }

            var closeTag = content.LastIndexOf("</manifest>");
            if (closeTag < 0)
            {
                Debug.LogError("[BleBridge] Could not find </manifest> to insert permissions before.");
                return;
            }

            content = content.Insert(closeTag, insert);
            File.WriteAllText(AssetsManifestPath, content);
            AssetDatabase.ImportAsset("Assets/Plugins/Android/AndroidManifest.xml", ImportAssetOptions.ForceUpdate);
            Debug.Log($"[BleBridge] Merged missing BLE permissions into {AssetsManifestPath}");
        }

        static string TemplateManifest() =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<manifest\n" +
            "    xmlns:android=\"http://schemas.android.com/apk/res/android\"\n" +
            "    package=\"com.unity3d.player\"\n" +
            "    xmlns:tools=\"http://schemas.android.com/tools\">\n" +
            "    <application>\n" +
            "        <activity android:name=\"com.unity3d.player.UnityPlayerActivity\"\n" +
            "                  android:theme=\"@style/UnityThemeSelector\"\n" +
            "                  android:exported=\"true\">\n" +
            "            <intent-filter>\n" +
            "                <action android:name=\"android.intent.action.MAIN\" />\n" +
            "                <category android:name=\"android.intent.category.LAUNCHER\" />\n" +
            "            </intent-filter>\n" +
            "            <meta-data android:name=\"unityplayer.UnityActivity\" android:value=\"true\" />\n" +
            "        </activity>\n" +
            "    </application>\n" +
            "    <!-- Legacy Bluetooth permissions (API <= 30, Android 11 and below) -->\n" +
            "    <uses-permission android:name=\"android.permission.BLUETOOTH\"\n" +
            "                     android:maxSdkVersion=\"30\" />\n" +
            "    <uses-permission android:name=\"android.permission.BLUETOOTH_ADMIN\"\n" +
            "                     android:maxSdkVersion=\"30\" />\n" +
            "    <!-- Runtime Bluetooth permissions (API 31+, Android 12+). Android auto-denies these\n" +
            "         with no dialog if they are missing from the final merged manifest. -->\n" +
            "    <uses-permission android:name=\"android.permission.BLUETOOTH_SCAN\" android:usesPermissionFlags=\"neverForLocation\" />\n" +
            "    <uses-permission android:name=\"android.permission.BLUETOOTH_CONNECT\" />\n" +
            "    <!-- Location needed for BLE scan on API 29-30. -->\n" +
            "    <uses-permission android:name=\"android.permission.ACCESS_FINE_LOCATION\" />\n" +
            "</manifest>\n";
    }
}
