using System;
using System.IO;
using UnityEngine;

namespace KerbalCraft
{
	/// <summary>Reads GameData/KerbalCraft/KerbalCraft.cfg (a KERBALCRAFT node). Every key is optional.</summary>
	public static class Settings
	{
		public static KeyCode ToggleKey = KeyCode.Insert;     // enter / leave Steve mode
		public static KeyCode BoardKey = KeyCode.G;           // board the vessel you are looking at
		public static float MouseScale = 10f;                 // raw mouse axis -> pixels (Unity's default axis gives 0.1 per pixel)
		public static float TetherMeters = 2000f;             // how far from the active vessel Steve may walk
		public static float SiteRadius = 1000f;               // a new building site starts this far from the nearest one
		public static float KickImpulse = 4f;                 // kN*s per point of Minecraft damage dealt to a vessel part
		public static float ExplosionImpulse = 60f;           // kN*s at the centre of a TNT-sized (radius 4) blast
		public static bool AutoStartMinecraft = true;
		public static string LauncherPath = "";               // empty: %LOCALAPPDATA%\KerbalCraft\Prism\prismlauncher.exe
		public static string LauncherArguments = "--launch KerbalCraft";
		public static float OverlayScale = 1f;               // render Minecraft's HUD at this fraction of the window size
		public static bool BlockColliders = true;             // vessels collide with placed blocks
		public static bool HideKspUi = true;                  // hide KSP's own HUD while in Steve mode
		public static float InterpDelayMs = 60f;              // how far in the past the camera follows Minecraft's 20 Hz ticks
		public static int CollisionRadiusRegions = 6;         // collision streamed this many 8-block regions around Steve
		public static float ActorRadius = 60f;                // vessel parts closer than this get a hittable stand-in
		public static bool FollowWithEva = false;             // experimental: move the active EVA kerbal along with Steve
		public static bool Diagnostics = false;

		public static string ConfigPath
		{
			get { return Path.Combine(KSPUtil.ApplicationRootPath, "GameData/KerbalCraft/KerbalCraft.cfg"); }
		}

		public static void Load()
		{
			try
			{
				if (!File.Exists(ConfigPath)) return;
				ConfigNode root = ConfigNode.Load(ConfigPath);
				if (root == null) return;
				ConfigNode n = root.HasNode("KERBALCRAFT") ? root.GetNode("KERBALCRAFT") : root;
				ToggleKey = Key(n, "toggleKey", ToggleKey);
				BoardKey = Key(n, "boardKey", BoardKey);
				MouseScale = F(n, "mouseScale", MouseScale);
				TetherMeters = F(n, "tetherMeters", TetherMeters);
				SiteRadius = F(n, "siteRadius", SiteRadius);
				KickImpulse = F(n, "kickImpulse", KickImpulse);
				ExplosionImpulse = F(n, "explosionImpulse", ExplosionImpulse);
				AutoStartMinecraft = B(n, "autoStartMinecraft", AutoStartMinecraft);
				if (n.HasValue("launcherPath")) LauncherPath = n.GetValue("launcherPath");
				if (n.HasValue("launcherArguments")) LauncherArguments = n.GetValue("launcherArguments");
				OverlayScale = Mathf.Clamp(F(n, "overlayScale", OverlayScale), 0.25f, 1f);
				BlockColliders = B(n, "blockColliders", BlockColliders);
				HideKspUi = B(n, "hideKspUi", HideKspUi);
				InterpDelayMs = F(n, "interpDelayMs", InterpDelayMs);
				CollisionRadiusRegions = Mathf.Clamp((int)F(n, "collisionRadiusRegions", CollisionRadiusRegions), 2, 10);
				ActorRadius = F(n, "actorRadius", ActorRadius);
				FollowWithEva = B(n, "followWithEva", FollowWithEva);
				Diagnostics = B(n, "diagnostics", Diagnostics);
			}
			catch (Exception e) { Log.Warn("couldn't read {0}: {1}", ConfigPath, e.Message); }
		}

		static float F(ConfigNode n, string k, float d) { float v; return n.HasValue(k) && float.TryParse(n.GetValue(k), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : d; }
		static bool B(ConfigNode n, string k, bool d) { bool v; return n.HasValue(k) && bool.TryParse(n.GetValue(k), out v) ? v : d; }
		static KeyCode Key(ConfigNode n, string k, KeyCode d)
		{
			if (!n.HasValue(k)) return d;
			try { return (KeyCode)Enum.Parse(typeof(KeyCode), n.GetValue(k), true); } catch { return d; }
		}
	}
}
