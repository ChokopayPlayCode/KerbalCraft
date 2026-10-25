using System.IO;
using UnityEngine;

namespace KerbalCraft
{
	/// <summary>
	/// Finds the shaders to draw Minecraft's meshes with. Best: the KerbalCraft shader bundle
	/// (GameData/KerbalCraft/Shaders/kerbalcraft.shaders, built from Unity/Shaders, vertex colours
	/// + Minecraft light levels, lit and shadowed by KSP's sun). Without it, KSP's own cutout and
	/// alpha shaders are used: everything still draws, without tint, ambient occlusion and the
	/// Minecraft light levels.
	/// </summary>
	public static class Shaders
	{
		public static Shader Cutout, Translucent, Overlay;   // Overlay: premultiplied alpha, ZTest always; may be null
		public static bool VertexColours;                     // true with the bundle
		static bool loaded;

		public static void Load()
		{
			if (loaded) return;
			loaded = true;
			string path = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/KerbalCraft/Shaders/kerbalcraft.shaders");
			try
			{
				if (File.Exists(path))
				{
					AssetBundle bundle = AssetBundle.LoadFromFile(path);
					if (bundle != null)
					{
						foreach (Shader s in bundle.LoadAllAssets<Shader>())
						{
							if (s.name == "KerbalCraft/Blocks") Cutout = s;
							else if (s.name == "KerbalCraft/BlocksTranslucent") Translucent = s;
							else if (s.name == "KerbalCraft/Overlay") Overlay = s;
						}
					}
				}
			}
			catch (System.Exception e) { Log.Warn("shader bundle failed: {0}", e.Message); }

			VertexColours = Cutout != null;
			if (Cutout == null) Cutout = Find("KSP/Alpha/Cutoff", "Legacy Shaders/Transparent/Cutout/Diffuse", "Unlit/Transparent Cutout", "Diffuse");
			if (Translucent == null) Translucent = Find("KSP/Alpha/Translucent", "Legacy Shaders/Transparent/Diffuse", "Sprites/Default", "Diffuse");
			Log.Info("shaders: blocks={0}{1}, translucent={2}, overlay={3}", Name(Cutout), VertexColours ? " (bundle)" : " (fallback: no tint/AO/light levels)", Name(Translucent), Name(Overlay));
		}

		static Shader Find(params string[] names)
		{
			foreach (string n in names) { Shader s = Shader.Find(n); if (s != null) return s; }
			return null;
		}

		static string Name(Shader s) { return s == null ? "none" : s.name; }

		public static Material MakeBlockMaterial(bool translucent, Texture tex)
		{
			Shader s = translucent ? Translucent : Cutout;
			Material m = new Material(s);
			m.mainTexture = tex;
			if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", 0.5f);
			if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
			if (translucent) m.renderQueue = 3000;
			return m;
		}
	}
}
