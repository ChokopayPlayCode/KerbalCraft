using System;
using System.Collections.Generic;
using UnityEngine;

namespace KerbalCraft
{
	/// <summary>
	/// A building site: a spot on a celestial body whose tangent plane becomes a flat patch of
	/// Minecraft world. Everything Steve builds there is stored in Minecraft's world under the
	/// site's slot, so it is still there next time (and on other planets there are other sites).
	/// </summary>
	public class Site
	{
		public uint Id;
		public int SlotIndex;
		public string BodyName;
		public double Lat, Lon, Alt;   // anchor on the surface; Alt is metres above sea level
	}

	public static class Sites
	{
		public const double SlotSpacing = 2048.0;   // Minecraft blocks between two site slots
		public const int SlotGrid = 32;             // slots per axis; positions stay below +-32768 so floats keep millimetres
		public static readonly List<Site> All = new List<Site>();

		public static void SlotOrigin(int index, out double x, out double z)
		{
			x = ((index % SlotGrid) - SlotGrid / 2) * SlotSpacing;
			z = (((index / SlotGrid) % SlotGrid) - SlotGrid / 2) * SlotSpacing;
		}

		public static Site Find(CelestialBody body, Vector3d worldPos, double maxDistance)
		{
			Site best = null; double bestD = maxDistance;
			for (int i = 0; i < All.Count; i++)
			{
				Site s = All[i];
				if (s.BodyName != body.bodyName) continue;
				double d = (body.GetWorldSurfacePosition(s.Lat, s.Lon, s.Alt) - worldPos).magnitude;
				if (d < bestD) { bestD = d; best = s; }
			}
			return best;
		}

		public static Site Create(CelestialBody body, Vector3d worldPos)
		{
			double lat = body.GetLatitude(worldPos), lon = body.GetLongitude(worldPos);
			Site s = new Site();
			s.SlotIndex = All.Count;                  // sites are never deleted, so the index stays unique
			s.Id = (uint)(s.SlotIndex + 1);
			s.BodyName = body.bodyName;
			s.Lat = lat; s.Lon = lon;
			s.Alt = body.TerrainAltitude(lat, lon, true);
			All.Add(s);
			Log.Info("new site {0} on {1} ({2:F4}, {3:F4}), slot {4}", s.Id, s.BodyName, lat, lon, s.SlotIndex);
			return s;
		}

		public static void Save(ConfigNode node)
		{
			for (int i = 0; i < All.Count; i++)
			{
				Site s = All[i];
				ConfigNode n = node.AddNode("SITE");
				n.AddValue("id", s.Id); n.AddValue("slot", s.SlotIndex); n.AddValue("body", s.BodyName);
				n.AddValue("lat", s.Lat.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
				n.AddValue("lon", s.Lon.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
				n.AddValue("alt", s.Alt.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
			}
		}

		public static void Load(ConfigNode node)
		{
			All.Clear();
			var ci = System.Globalization.CultureInfo.InvariantCulture;
			foreach (ConfigNode n in node.GetNodes("SITE"))
			{
				try
				{
					Site s = new Site();
					s.Id = uint.Parse(n.GetValue("id")); s.SlotIndex = int.Parse(n.GetValue("slot")); s.BodyName = n.GetValue("body");
					s.Lat = double.Parse(n.GetValue("lat"), ci); s.Lon = double.Parse(n.GetValue("lon"), ci); s.Alt = double.Parse(n.GetValue("alt"), ci);
					All.Add(s);
				}
				catch (Exception e) { Log.Warn("skipped a broken site: {0}", e.Message); }
			}
			Log.Info("{0} building site(s) in this save", All.Count);
		}

		/// <summary>The Minecraft world for this save game: each save keeps its own builds.</summary>
		public static string WorldName
		{
			get
			{
				string save = HighLogic.SaveFolder;
				if (string.IsNullOrEmpty(save)) save = "default";
				var sb = new System.Text.StringBuilder("KerbalCraft-");
				foreach (char c in save) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
				return sb.ToString();
			}
		}
	}

	[KSPScenario(ScenarioCreationOptions.AddToAllGames, new GameScenes[] { GameScenes.FLIGHT, GameScenes.SPACECENTER, GameScenes.TRACKSTATION })]
	public class KerbalCraftScenario : ScenarioModule
	{
		public override void OnLoad(ConfigNode node) { base.OnLoad(node); Sites.Load(node); }
		public override void OnSave(ConfigNode node) { base.OnSave(node); Sites.Save(node); }
	}
}
