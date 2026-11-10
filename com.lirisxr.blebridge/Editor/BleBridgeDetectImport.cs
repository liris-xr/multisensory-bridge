using System.Linq;
using UnityEditor;

namespace Lirisxr.BleBridge.Editor
{
    public class BleBridgeDetectImport : AssetPostprocessor
    {
        const string PluginManifest = "Packages/com.lirisxr.blebridge/Plugins/Android/AndroidManifest.xml";
        const string AssetsManifest = "Assets/Plugins/Android/AndroidManifest.xml";

        static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths,
            bool didDomainReload)
        {
            if (didDomainReload && BleBridgeManifestWizard.IsOpen)
            {
                BleBridgeManifestWizard.RefreshStatic();
            }

            if (importedAssets.Contains(PluginManifest))
            {
                BleBridgeManifestWizard.Open();
                BleBridgeManifestWizard.WarnIfGameActivityOnly();
            }

            if (importedAssets.Contains(AssetsManifest))
            {
                BleBridgeManifestWizard.RefreshStatic();
            }
        }
    }
}
