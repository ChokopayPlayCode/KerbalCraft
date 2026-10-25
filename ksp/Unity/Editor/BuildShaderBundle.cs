// Put this folder's Shaders/ and Editor/ into a Unity 2019.4.18f1 project (the version KSP 1.12 uses),
// then use the menu KerbalCraft > Build shader bundle. Copy the result to
// GameData/KerbalCraft/Shaders/kerbalcraft.shaders.
#if UNITY_EDITOR
using System.IO;
using UnityEditor;

public static class BuildShaderBundle
{
	[MenuItem("KerbalCraft/Build shader bundle")]
	public static void Build()
	{
		string[] shaders = Directory.GetFiles("Assets/Shaders", "*.shader");
		var build = new AssetBundleBuild { assetBundleName = "kerbalcraft.shaders", assetNames = shaders };
		Directory.CreateDirectory("Bundles");
		// KSP runs on Direct3D 11 (Windows); the bundle must be built for the standalone Windows player.
		BuildPipeline.BuildAssetBundles("Bundles", new[] { build }, BuildAssetBundleOptions.ForceRebuildAssetBundle, BuildTarget.StandaloneWindows64);
		UnityEngine.Debug.Log("Built Bundles/kerbalcraft.shaders");
	}
}
#endif
