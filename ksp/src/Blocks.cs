using System;
using System.Collections.Generic;
using UnityEngine;

namespace KerbalCraft
{
	/// <summary>
	/// Minecraft's light-emitting blocks (torches, lava, glowstone...) light KSP's world: the
	/// nearest few become real Unity point lights. Each section's emitters arrive as RenLight records.
	/// </summary>
	public unsafe class BlockLights
	{
		class Set_ { public int Sx, Sy, Sz; public byte[] Data; }
		readonly Dictionary<long, Set_> sets = new Dictionary<long, Set_>();
		readonly List<Light> pool = new List<Light>();
		readonly List<KeyValuePair<float, Vector4>> candidates = new List<KeyValuePair<float, Vector4>>();
		readonly List<Color> candidateColors = new List<Color>();
		float timer;
		const int MaxLights = 6;
		GameObject holder;

		public void Set(long key, int sx, int sy, int sz, byte* p, int n)
		{
			byte[] d = new byte[n * 8];
			for (int i = 0; i < d.Length; i++) d[i] = p[i];
			sets[key] = new Set_ { Sx = sx, Sy = sy, Sz = sz, Data = d };
		}

		public void Remove(long key) { sets.Remove(key); }

		public void Clear()
		{
			sets.Clear();
			for (int i = 0; i < pool.Count; i++) pool[i].enabled = false;
		}

		public void Update(WorldRenderer owner, Vector3 cameraLocal)
		{
			timer -= Time.unscaledDeltaTime;
			// flicker needs per-frame intensity, selection only a few times a second
			if (timer <= 0f)
			{
				timer = 0.25f;
				Select(owner, cameraLocal);
			}
			float t = Time.unscaledTime;
			for (int i = 0; i < pool.Count; i++)
			{
				Light l = pool[i];
				if (!l.enabled) continue;
				if (kinds.Count > i && kinds[i] == Proto.LIGHT_FLAME)
					l.intensity = bases[i] * (0.9f + 0.1f * Mathf.Sin(t * 13f + i * 7f) + 0.05f * Mathf.Sin(t * 29f + i));
				else if (kinds.Count > i && kinds[i] == Proto.LIGHT_LAVA)
					l.intensity = bases[i] * (0.92f + 0.08f * Mathf.Sin(t * 1.7f + i));
			}
		}

		readonly List<int> kinds = new List<int>();
		readonly List<float> bases = new List<float>();

		void Select(WorldRenderer owner, Vector3 cam)
		{
			candidates.Clear(); candidateColors.Clear();
			var kindList = new List<int>();
			foreach (Set_ s in sets.Values)
			{
				for (int i = 0; i < s.Data.Length; i += 8)
				{
					Vector3 local = owner.ToLocal(s.Sx * 16 + s.Data[i] + 0.5, s.Sy * 16 + s.Data[i + 1] + 0.5, s.Sz * 16 + s.Data[i + 2] + 0.5);
					float d2 = (local - cam).sqrMagnitude;
					if (d2 > 48f * 48f) continue;
					int level = s.Data[i + 3];
					uint color = BitConverter.ToUInt32(s.Data, i + 4);
					float score = d2 / Mathf.Max(level, 1);   // near and bright first
					candidates.Add(new KeyValuePair<float, Vector4>(score, new Vector4(local.x, local.y, local.z, level)));
					candidateColors.Add(new Color((color & 0xFF) / 255f, ((color >> 8) & 0xFF) / 255f, ((color >> 16) & 0xFF) / 255f, (color >> 24) & 0xF));
				}
			}
			// pick the MaxLights best scores
			int[] order = new int[candidates.Count];
			for (int i = 0; i < order.Length; i++) order[i] = i;
			Array.Sort(order, (a, b) => candidates[a].Key.CompareTo(candidates[b].Key));
			if (holder == null) holder = new GameObject("KerbalCraft lights");
			kinds.Clear(); bases.Clear();
			for (int i = 0; i < MaxLights; i++)
			{
				while (pool.Count <= i)
				{
					GameObject go = new GameObject("light");
					go.transform.SetParent(holder.transform, false);
					Light l = go.AddComponent<Light>();
					l.type = LightType.Point; l.shadows = LightShadows.None; l.enabled = false;
					pool.Add(l);
				}
				Light light = pool[i];
				if (i >= order.Length) { light.enabled = false; kinds.Add(0); bases.Add(0f); continue; }
				Vector4 v = candidates[order[i]].Value;
				Color c = candidateColors[order[i]];
				light.transform.position = owner.RootT.TransformPoint(new Vector3(v.x, v.y, v.z));
				light.color = new Color(c.r, c.g, c.b);
				light.range = Mathf.Clamp(v.w * 1.1f, 2f, 18f);
				float b = Mathf.Clamp01(v.w / 15f) * 1.4f;
				light.intensity = b;
				light.enabled = true;
				kinds.Add((int)c.a & 0xF); bases.Add(b);
			}
		}
	}

	/// <summary>Marks a placed-block collider: Minecraft already knows these blocks, so terrain sampling ignores them.</summary>
	public class SolidBlockTag : MonoBehaviour { }

	/// <summary>
	/// Placed blocks are real obstacles for KSP's physics too: rovers and kicked rockets run into
	/// them. Each section's solid blocks (a 16x16x16 bitset) become a few merged BoxColliders
	/// under a kinematic body that follows the site.
	/// </summary>
	public unsafe class SolidBlocks
	{
		Transform parent;
		public double SlotX, SlotZ;
		class Entry { public GameObject Go; public Vector3 Base; }
		readonly Dictionary<long, Entry> sections = new Dictionary<long, Entry>();

		public void Init(Transform renderRoot) { parent = renderRoot; }

		public void Clear()
		{
			foreach (Entry e in sections.Values) UnityEngine.Object.Destroy(e.Go);
			sections.Clear();
		}

		/// <summary>The site's slot changed: every section moves with it.</summary>
		public void Relocate()
		{
			foreach (Entry e in sections.Values) e.Go.transform.localPosition = new Vector3((float)(e.Base.x - SlotX), e.Base.y, (float)(e.Base.z - SlotZ));
		}

		public void OnSolids(byte* p, int bytes)
		{
			if (!Settings.BlockColliders) return;
			int sx = *(int*)p, sy = *(int*)(p + 4), sz = *(int*)(p + 8), count = *(int*)(p + 12);
			long key = ((long)(sx & 0x1FFFFF) << 42) | ((long)(sy & 0x1FFFFF) << 21) | (long)(sz & 0x1FFFFF);
			Entry old;
			if (sections.TryGetValue(key, out old)) { UnityEngine.Object.Destroy(old.Go); sections.Remove(key); }
			if (count <= 0 || bytes < 16 + 512) return;
			byte* bits = p + 16;
			bool[] solid = new bool[4096];
			for (int i = 0; i < 4096; i++) solid[i] = (bits[i >> 3] & (1 << (i & 7))) != 0;   // bit = x + 16 z + 256 y
			GameObject go = new GameObject("solid " + sx + "," + sy + "," + sz);
			go.transform.SetParent(parent, false);
			// KSP's ground (and so its EVA kerbals' footing) lives on "Local Scenery": blocks must be there
			// too, or kerbals don't see them as ground and fall through. Tagged, so the terrain sampler skips them.
			int scenery = LayerMask.NameToLayer("Local Scenery");
			go.layer = scenery >= 0 ? scenery : 0;
			go.AddComponent<SolidBlockTag>();
			// greedy merge: runs along x, grown in z, then in y
			bool[] used = new bool[4096];
			for (int y = 0; y < 16; y++)
			for (int z = 0; z < 16; z++)
			for (int x = 0; x < 16; x++)
			{
				int i = x + 16 * z + 256 * y;
				if (!solid[i] || used[i]) continue;
				int dx = 1; while (x + dx < 16 && solid[i + dx] && !used[i + dx]) dx++;
				int dz = 1;
				while (z + dz < 16 && RowFree(solid, used, x, y, z + dz, dx)) dz++;
				int dy = 1;
				while (y + dy < 16 && LayerFree(solid, used, x, y + dy, z, dx, dz)) dy++;
				for (int yy = y; yy < y + dy; yy++) for (int zz = z; zz < z + dz; zz++) for (int xx = x; xx < x + dx; xx++) used[xx + 16 * zz + 256 * yy] = true;
				BoxCollider bc = go.AddComponent<BoxCollider>();
				bc.center = new Vector3(x + dx * 0.5f, y + dy * 0.5f, z + dz * 0.5f);
				bc.size = new Vector3(dx, dy, dz);
			}
			Entry en = new Entry { Go = go, Base = new Vector3(sx * 16, sy * 16, sz * 16) };
			go.transform.localPosition = new Vector3((float)(en.Base.x - SlotX), en.Base.y, (float)(en.Base.z - SlotZ));
			sections[key] = en;
		}

		static bool RowFree(bool[] solid, bool[] used, int x, int y, int z, int dx)
		{
			for (int k = 0; k < dx; k++) { int i = x + k + 16 * z + 256 * y; if (!solid[i] || used[i]) return false; }
			return true;
		}

		static bool LayerFree(bool[] solid, bool[] used, int x, int y, int z, int dx, int dz)
		{
			for (int k = 0; k < dz; k++) if (!RowFree(solid, used, x, y, z + k, dx)) return false;
			return true;
		}
	}
}
