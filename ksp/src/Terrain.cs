using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace KerbalCraft
{
	/// <summary>Ground height under a Minecraft column, in the site frame.</summary>
	public class TerrainSampler
	{
		readonly Dictionary<long, float> cache = new Dictionary<long, float>();
		public SiteFrame Frame;
		int terrainMask;

		public TerrainSampler(SiteFrame f) { Frame = f; terrainMask = LayerMask.GetMask("Local Scenery"); }

		public void Clear() { cache.Clear(); }

		static long Key(int x, int z) { return ((long)x << 32) | (uint)z; }

		/// <summary>Surface height (Minecraft Y) at the corner of block column (mx, mz); cached.</summary>
		public float VertexY(int mx, int mz)
		{
			long k = Key(mx, mz);
			float y;
			if (cache.TryGetValue(k, out y)) return y;
			y = Compute(mx - Frame.SlotX, mz - Frame.SlotZ);
			if (cache.Count > 400000) cache.Clear();
			cache[k] = y;
			return y;
		}

		/// <summary>Surface height at local offsets (metres east/south of the anchor), uncached.</summary>
		public float Compute(double lx, double lz)
		{
			CelestialBody body = Frame.Body;
			Vector3d p = Frame.Origin + Frame.XDir * lx + Frame.South * lz;
			double lat = body.GetLatitude(p), lon = body.GetLongitude(p);
			double alt = body.TerrainAltitude(lat, lon, true);      // true: ocean floor, not sea level
			Vector3d radial = (p - body.position).normalized;
			Vector3d surface = body.position + radial * (body.Radius + alt);
			double y = Vector3d.Dot(surface - Frame.Origin, Frame.Up);
			// Where KSP has loaded terrain colliders (near the vessel) prefer the real mesh: it can differ from the formula by decimetres.
			Vector3 from = (Vector3)(surface + Frame.Up * 30.0);
			RaycastHit[] hits = Physics.RaycastAll(from, -(Vector3)Frame.Up, 60f, terrainMask, QueryTriggerInteraction.Ignore);
			float nearest = float.MaxValue;
			Vector3 point = Vector3.zero;
			bool found = false;
			for (int i = 0; i < hits.Length; i++)
			{
				if (hits[i].collider.GetComponent<SolidBlockTag>() != null) continue;   // placed blocks: Minecraft has them
				if (hits[i].distance < nearest) { nearest = hits[i].distance; point = hits[i].point; found = true; }
			}
			if (found) y = Vector3d.Dot((Vector3d)point - Frame.Origin, Frame.Up);
			return (float)y;
		}

		/// <summary>Height of the sea surface over local offsets, following the planet's curvature.</summary>
		public double SeaY(double lx, double lz)
		{
			double r = Frame.Body.Radius;
			double v = r * r - lx * lx - lz * lz;
			return (v > 0 ? Math.Sqrt(v) : 0) - Frame.R0;
		}

		public bool HasOcean { get { return Frame.Body.ocean; } }
	}

	/// <summary>
	/// Streams KSP's ground and the vessels standing on it to Minecraft as collision: exact
	/// triangles (what the player walks on) and 8x8x8 voxel blocks (for items, arrows, stand-ins),
	/// in 8-block regions around Steve. Terrain comes from the planet's height function or, where
	/// KSP has loaded colliders, a ray into them; vessels from their parts' collider meshes.
	/// </summary>
	public unsafe class CollisionExporter
	{
		const int RS = 8;           // Proto: REGION_SIZE
		const int TriFlagTerrain = Proto.TRI_TERRAIN;

		class Region
		{
			public byte[] Voxels;         // header + blocks, ready to send
			public List<float> TerrainTris = new List<float>();   // 9 floats per triangle, Minecraft coordinates
			public int BlockCount;
			public bool Dirty = true;     // needs (re)sending
			public bool DynamicSent;      // last send included vessel triangles
		}

		readonly Dictionary<long, Region> regions = new Dictionary<long, Region>();
		readonly HashSet<long> builtColumns = new HashSet<long>();
		readonly Dictionary<long, int[]> columnRange = new Dictionary<long, int[]>();
		readonly List<long> sendQueue = new List<long>();
		readonly VesselGeometry vessels = new VesselGeometry();
		TerrainSampler sampler;
		SiteFrame frame;
		public uint Epoch;
		float dynTimer;
		readonly Stopwatch sw = new Stopwatch();

		static long RKey(int rx, int ry, int rz) { return ((long)(rx & 0x1FFFFF) << 42) | ((long)(ry & 0x1FFFFF) << 21) | (long)(rz & 0x1FFFFF); }
		static long CKey(int rx, int rz) { return ((long)rx << 32) | (uint)rz; }

		public TerrainSampler Sampler { get { return sampler; } }

		/// <summary>New site (or reconnect): drop everything and tell Minecraft to forget its copy.</summary>
		public void Reset(SiteFrame f, uint newEpoch)
		{
			frame = f; Epoch = newEpoch;
			sampler = new TerrainSampler(f);
			regions.Clear(); builtColumns.Clear(); columnRange.Clear(); sendQueue.Clear(); vessels.Clear();
			byte[] b = BitConverter.GetBytes((int)newEpoch);
			fixed (byte* p = b) Link.WriteCollision(Proto.COL_CLEAR, p, 4);
		}

		public bool Ready { get { return sampler != null; } }

		public void Tick(double mcX, double mcZ, float dt, Vessel ignore)
		{
			if (sampler == null) return;
			sw.Reset(); sw.Start();
			int cx = (int)Math.Floor(mcX / RS), cz = (int)Math.Floor(mcZ / RS);
			int R = Settings.CollisionRadiusRegions;

			// ground, nearest columns first
			for (int ring = 0; ring <= R && sw.ElapsedMilliseconds < 4; ring++)
			{
				for (int dx = -ring; dx <= ring && sw.ElapsedMilliseconds < 4; dx++)
				for (int dz = -ring; dz <= ring; dz++)
				{
					if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;
					long ck = CKey(cx + dx, cz + dz);
					if (builtColumns.Contains(ck)) continue;
					BuildColumn(cx + dx, cz + dz);
					if (sw.ElapsedMilliseconds >= 4) break;
				}
			}

			// vessels (moving ones are re-exported a few times a second)
			dynTimer -= dt;
			if (dynTimer <= 0f)
			{
				dynTimer = 0.12f;
				List<long> touched = vessels.Update(frame, mcX, mcZ, ignore);
				for (int i = 0; i < touched.Count; i++)
				{
					Region r;
					if (regions.TryGetValue(touched[i], out r)) { r.Dirty = true; if (!sendQueue.Contains(touched[i])) sendQueue.Add(touched[i]); }
				}
			}

			Flush();
			Evict(cx, cz, R);
		}

		void Flush()
		{
			int sent = 0;
			while (sendQueue.Count > 0 && sent < 48)
			{
				long key = sendQueue[0];
				Region r;
				if (!regions.TryGetValue(key, out r)) { sendQueue.RemoveAt(0); continue; }
				int rx = (int)((key >> 42) & 0x1FFFFF), ry = (int)((key >> 21) & 0x1FFFFF), rz = (int)(key & 0x1FFFFF);
				rx = Sign21(rx); ry = Sign21(ry); rz = Sign21(rz);
				byte[] tris = MakeTris(rx, ry, rz, r);
				bool dyn;
				fixed (byte* tp = tris) { if (!Link.WriteCollision(Proto.COL_TRIS, tp, tris.Length)) return; }
				fixed (byte* vp = r.Voxels) { if (!Link.WriteCollision(Proto.COL_REGION, vp, r.Voxels.Length)) return; }
				dyn = vessels.RegionHasTriangles(key);
				r.DynamicSent = dyn;
				r.Dirty = false;
				sendQueue.RemoveAt(0);
				sent++;
			}
		}

		static int Sign21(int v) { return (v & 0x100000) != 0 ? v | ~0x1FFFFF : v; }

		byte[] MakeTris(int rx, int ry, int rz, Region r)
		{
			List<float> dyn = vessels.TrianglesFor(RKey(rx, ry, rz));
			int count = r.TerrainTris.Count / 9 + (dyn == null ? 0 : dyn.Count / 9);
			var ms = new MemoryStream(Proto.COL_REGION_HEADER_BYTES + count * Proto.COL_TRI_BYTES);
			var w = new BinaryWriter(ms);
			WriteHeader(w, rx, ry, rz, (uint)count);
			WriteTris(w, r.TerrainTris, TriFlagTerrain);
			if (dyn != null) WriteTris(w, dyn, 0);
			return ms.ToArray();
		}

		static void WriteTris(BinaryWriter w, List<float> t, int flags)
		{
			for (int i = 0; i + 8 < t.Count; i += 9)
			{
				for (int k = 0; k < 9; k++) w.Write(t[i + k]);
				w.Write(flags);
			}
		}

		void WriteHeader(BinaryWriter w, int rx, int ry, int rz, uint count)
		{
			w.Write(rx * RS); w.Write(ry * RS); w.Write(rz * RS);
			w.Write(rx * RS + RS - 1); w.Write(ry * RS + RS - 1); w.Write(rz * RS + RS - 1);
			w.Write((int)Epoch); w.Write((int)count);
		}

		// ---- building ground ----------------------------------------------------------------

		void BuildColumn(int rx, int rz)
		{
			builtColumns.Add(CKey(rx, rz));
			int x0 = rx * RS, z0 = rz * RS;
			float[,] H = new float[RS + 1, RS + 1];
			float minH = float.MaxValue, maxH = float.MinValue;
			for (int i = 0; i <= RS; i++)
			for (int j = 0; j <= RS; j++)
			{
				float h = sampler.VertexY(x0 + i, z0 + j);
				H[i, j] = h; minH = Math.Min(minH, h); maxH = Math.Max(maxH, h);
			}
			int ryMin = (int)Math.Floor((minH - 3f) / RS) - 1, ryMax = (int)Math.Floor(maxH / RS) + 4;
			ryMin = Math.Max(ryMin, -120); ryMax = Math.Min(ryMax, 120);
			columnRange[CKey(rx, rz)] = new[] { ryMin, ryMax };

			// per sub-column surface height (bilinear in the cell's corners)
			float[,] sub = new float[RS * 8, RS * 8];
			for (int bx = 0; bx < RS; bx++)
			for (int bz = 0; bz < RS; bz++)
			for (int ix = 0; ix < 8; ix++)
			for (int iz = 0; iz < 8; iz++)
			{
				float u = (ix + 0.5f) / 8f, v = (iz + 0.5f) / 8f;
				float h0 = H[bx, bz] * (1 - u) + H[bx + 1, bz] * u, h1 = H[bx, bz + 1] * (1 - u) + H[bx + 1, bz + 1] * u;
				sub[bx * 8 + ix, bz * 8 + iz] = h0 * (1 - v) + h1 * v;
			}

			for (int ry = ryMin; ry <= ryMax; ry++)
			{
				Region reg = new Region();
				float yLo = ry * RS, yHi = yLo + RS;
				// triangles overlapping this region vertically
				for (int bx = 0; bx < RS; bx++)
				for (int bz = 0; bz < RS; bz++)
				{
					float a = H[bx, bz], b = H[bx, bz + 1], c = H[bx + 1, bz + 1], d = H[bx + 1, bz];
					float x = x0 + bx, z = z0 + bz;
					AddTri(reg, yLo, yHi, x, a, z, x, b, z + 1, x + 1, c, z + 1);
					AddTri(reg, yLo, yHi, x, a, z, x + 1, c, z + 1, x + 1, d, z);
				}
				// voxels: what lies up to two metres under the surface is ground
				var ms = new MemoryStream();
				var w = new BinaryWriter(ms);
				int blocks = 0;
				var bw = new MemoryStream();
				var bwr = new BinaryWriter(bw);
				for (int bx = 0; bx < RS; bx++)
				for (int bz = 0; bz < RS; bz++)
				for (int by = 0; by < RS; by++)
				{
					float blockLo = yLo + by;
					ulong[] layers = null;
					for (int iy = 0; iy < 8; iy++)
					{
						float yc = blockLo + (iy + 0.5f) / 8f;
						ulong bits = 0;
						for (int iz = 0; iz < 8; iz++)
						for (int ix = 0; ix < 8; ix++)
						{
							float h = sub[bx * 8 + ix, bz * 8 + iz];
							if (yc <= h && yc >= h - 2f) bits |= 1UL << (iz * 8 + ix);
						}
						if (bits != 0) { if (layers == null) layers = new ulong[8]; layers[iy] = bits; }
					}
					if (layers == null) continue;
					bwr.Write(x0 + bx); bwr.Write((int)(yLo + by)); bwr.Write(z0 + bz); bwr.Write(0);
					for (int iy = 0; iy < 8; iy++) bwr.Write(layers[iy]);
					blocks++;
				}
				WriteHeader(w, rx, ry, rz, (uint)blocks);
				w.Write(bw.ToArray());
				reg.Voxels = ms.ToArray();
				reg.BlockCount = blocks;
				long key = RKey(rx, ry, rz);
				regions[key] = reg;
				sendQueue.Add(key);
			}
		}

		static void AddTri(Region r, float yLo, float yHi, float ax, float ay, float az, float bx, float by, float bz, float cx, float cy, float cz)
		{
			float lo = Math.Min(ay, Math.Min(by, cy)), hi = Math.Max(ay, Math.Max(by, cy));
			if (hi < yLo - 0.01f || lo > yHi + 0.01f) return;
			r.TerrainTris.Add(ax); r.TerrainTris.Add(ay); r.TerrainTris.Add(az);
			r.TerrainTris.Add(bx); r.TerrainTris.Add(by); r.TerrainTris.Add(bz);
			r.TerrainTris.Add(cx); r.TerrainTris.Add(cy); r.TerrainTris.Add(cz);
		}

		void Evict(int cx, int cz, int R)
		{
			if (builtColumns.Count < (2 * R + 1) * (2 * R + 1) * 3) return;
			var drop = new List<long>();
			foreach (long ck in builtColumns)
			{
				int rx = (int)(ck >> 32), rz = (int)(uint)ck;
				if (Math.Max(Math.Abs(rx - cx), Math.Abs(rz - cz)) > R + 3) drop.Add(ck);
			}
			foreach (long ck in drop)
			{
				builtColumns.Remove(ck);
				int rx = (int)(ck >> 32), rz = (int)(uint)ck;
				int[] range;
				if (columnRange.TryGetValue(ck, out range))
				{
					for (int ry = range[0]; ry <= range[1]; ry++) regions.Remove(RKey(rx, ry, rz));
					columnRange.Remove(ck);
				}
			}
		}

		// ---- water ----------------------------------------------------------------------------

		float waterTimer;

		/// <summary>The sea around Steve as a 16x16 height grid (Kerbin's oceans; nothing elsewhere).</summary>
		public void WriteWater(double mcX, double mcZ, uint siteId, float dt)
		{
			if (sampler == null || Link.Base == null) return;
			waterTimer -= dt;
			if (waterTimer > 0f) return;
			waterTimer = 0.2f;
			int n = Proto.WATER_GRID_SIZE;
			int ox = (int)Math.Floor(mcX) - n / 2, oz = (int)Math.Floor(mcZ) - n / 2;
			byte* g = Link.Base + Proto.OFF_WATER_GRID;
			int seq = *(int*)(g + Proto.WG_SEQ);
			*(int*)(g + Proto.WG_SEQ) = seq + 1;
			System.Threading.Thread.MemoryBarrier();
			*(int*)(g + Proto.WG_ORIGIN_X) = ox; *(int*)(g + Proto.WG_ORIGIN_Z) = oz; *(uint*)(g + Proto.WG_WORLD_ID) = siteId;
			for (int j = 0; j < n; j++)
			for (int i = 0; i < n; i++)
			{
				float s = -1.0e30f;
				if (sampler.HasOcean)
				{
					double lx = ox + i + 0.5 - frame.SlotX, lz = oz + j + 0.5 - frame.SlotZ;
					double sea = sampler.SeaY(lx, lz);
					if (sampler.VertexY(ox + i, oz + j) < sea - 0.05) s = (float)sea;
				}
				*(float*)(g + Proto.WG_SURFACE + (j * n + i) * 4) = s;
			}
			System.Threading.Thread.MemoryBarrier();
			*(int*)(g + Proto.WG_SEQ) = seq + 2 == 0 ? 2 : seq + 2;
		}
	}

	/// <summary>
	/// Triangles of the loaded vessels' colliders in Minecraft coordinates, bucketed by region.
	/// A vessel is re-extracted when it moved; unchanged ones keep their cached triangles.
	/// </summary>
	public class VesselGeometry
	{
		class Dyn
		{
			public long Signature; public List<float> Tris = new List<float>(); public List<long> Regions = new List<long>();
		}

		readonly Dictionary<Vessel, Dyn> known = new Dictionary<Vessel, Dyn>();
		readonly Dictionary<long, List<float>> byRegion = new Dictionary<long, List<float>>();
		static readonly Dictionary<Mesh, KeyValuePair<Vector3[], int[]>> meshCache = new Dictionary<Mesh, KeyValuePair<Vector3[], int[]>>();

		public void Clear() { known.Clear(); byRegion.Clear(); }

		public bool RegionHasTriangles(long key) { return byRegion.ContainsKey(key); }
		public List<float> TrianglesFor(long key) { List<float> l; return byRegion.TryGetValue(key, out l) ? l : null; }

		static long RKey(int rx, int ry, int rz) { return ((long)(rx & 0x1FFFFF) << 42) | ((long)(ry & 0x1FFFFF) << 21) | (long)(rz & 0x1FFFFF); }

		/// <summary>Refresh from the scene; returns regions whose vessel triangles changed (including ones just vacated).</summary>
		public List<long> Update(SiteFrame f, double mcX, double mcZ, Vessel ignore)
		{
			var changed = new HashSet<long>();
			var seen = new HashSet<Vessel>();
			double maxD = Settings.ActorRadius + 40.0;
			foreach (Vessel v in FlightGlobals.VesselsLoaded)
			{
				if (v == null || v == ignore || v.isEVA || v.packed || v.mainBody != f.Body) continue;
				double vx, vy, vz;
				f.WorldToMc(v.transform.position, out vx, out vy, out vz);
				if (Math.Abs(vx - mcX) > maxD || Math.Abs(vz - mcZ) > maxD) continue;
				seen.Add(v);
				Dyn d;
				long sig = Signature(v);
				if (known.TryGetValue(v, out d) && d.Signature == sig) continue;
				if (d == null) { d = new Dyn(); known[v] = d; }
				foreach (long r in d.Regions) changed.Add(r);
				d.Signature = sig;
				Extract(f, v, d);
				foreach (long r in d.Regions) changed.Add(r);
			}
			// vessels that left
			var gone = new List<Vessel>();
			foreach (var kv in known) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
			foreach (Vessel v in gone) { foreach (long r in known[v].Regions) changed.Add(r); known.Remove(v); }
			if (changed.Count > 0) Rebucket();
			return new List<long>(changed);
		}

		static long Signature(Vessel v)
		{
			Vector3 p = v.transform.position; Quaternion q = v.transform.rotation;
			long h = v.parts.Count;
			h = h * 31 + (long)(p.x * 100f); h = h * 31 + (long)(p.y * 100f); h = h * 31 + (long)(p.z * 100f);
			h = h * 31 + (long)(q.x * 1000f); h = h * 31 + (long)(q.y * 1000f); h = h * 31 + (long)(q.z * 1000f);
			return h;
		}

		void Extract(SiteFrame f, Vessel v, Dyn d)
		{
			d.Tris.Clear(); d.Regions.Clear();
			var regs = new HashSet<long>();
			for (int i = 0; i < v.parts.Count; i++)
			{
				Part part = v.parts[i];
				if (part == null) continue;
				Collider[] cols = part.GetComponentsInChildren<Collider>(false);
				for (int c = 0; c < cols.Length; c++)
				{
					Collider col = cols[c];
					if (col == null || col.isTrigger || !col.enabled) continue;
					if (col is WheelCollider) { AddBox(f, col.bounds, d); continue; }
					MeshCollider mc = col as MeshCollider;
					if (mc != null && mc.sharedMesh != null && mc.sharedMesh.isReadable) AddMesh(f, mc.sharedMesh, mc.transform.localToWorldMatrix, d);
					else if (col is BoxCollider)
					{
						BoxCollider bc = (BoxCollider)col;
						AddOrientedBox(f, bc.transform.localToWorldMatrix, bc.center, bc.size, d);
					}
					else AddBox(f, col.bounds, d);
				}
			}
			// which regions do these triangles touch?
			for (int i = 0; i + 8 < d.Tris.Count; i += 9)
			{
				float minX = Math.Min(d.Tris[i], Math.Min(d.Tris[i + 3], d.Tris[i + 6])), maxX = Math.Max(d.Tris[i], Math.Max(d.Tris[i + 3], d.Tris[i + 6]));
				float minY = Math.Min(d.Tris[i + 1], Math.Min(d.Tris[i + 4], d.Tris[i + 7])), maxY = Math.Max(d.Tris[i + 1], Math.Max(d.Tris[i + 4], d.Tris[i + 7]));
				float minZ = Math.Min(d.Tris[i + 2], Math.Min(d.Tris[i + 5], d.Tris[i + 8])), maxZ = Math.Max(d.Tris[i + 2], Math.Max(d.Tris[i + 5], d.Tris[i + 8]));
				for (int rx = FloorDiv(minX); rx <= FloorDiv(maxX); rx++)
				for (int ry = FloorDiv(minY); ry <= FloorDiv(maxY); ry++)
				for (int rz = FloorDiv(minZ); rz <= FloorDiv(maxZ); rz++) regs.Add(RKey(rx, ry, rz));
			}
			d.Regions.AddRange(regs);
		}

		static int FloorDiv(float v) { return (int)Math.Floor(v / 8f); }

		void Rebucket()
		{
			byRegion.Clear();
			foreach (Dyn d in known.Values)
			{
				for (int i = 0; i + 8 < d.Tris.Count; i += 9)
				{
					float minX = Math.Min(d.Tris[i], Math.Min(d.Tris[i + 3], d.Tris[i + 6])), maxX = Math.Max(d.Tris[i], Math.Max(d.Tris[i + 3], d.Tris[i + 6]));
					float minY = Math.Min(d.Tris[i + 1], Math.Min(d.Tris[i + 4], d.Tris[i + 7])), maxY = Math.Max(d.Tris[i + 1], Math.Max(d.Tris[i + 4], d.Tris[i + 7]));
					float minZ = Math.Min(d.Tris[i + 2], Math.Min(d.Tris[i + 5], d.Tris[i + 8])), maxZ = Math.Max(d.Tris[i + 2], Math.Max(d.Tris[i + 5], d.Tris[i + 8]));
					for (int rx = FloorDiv(minX); rx <= FloorDiv(maxX); rx++)
					for (int ry = FloorDiv(minY); ry <= FloorDiv(maxY); ry++)
					for (int rz = FloorDiv(minZ); rz <= FloorDiv(maxZ); rz++)
					{
						long k = RKey(rx, ry, rz);
						List<float> l;
						if (!byRegion.TryGetValue(k, out l)) { l = new List<float>(); byRegion[k] = l; }
						for (int q = 0; q < 9; q++) l.Add(d.Tris[i + q]);
					}
				}
			}
		}

		static void AddMesh(SiteFrame f, Mesh mesh, Matrix4x4 m, Dyn d)
		{
			KeyValuePair<Vector3[], int[]> data;
			if (!meshCache.TryGetValue(mesh, out data))
			{
				data = new KeyValuePair<Vector3[], int[]>(mesh.vertices, mesh.triangles);
				meshCache[mesh] = data;
			}
			Vector3[] vs = data.Key; int[] ts = data.Value;
			for (int i = 0; i + 2 < ts.Length; i += 3)
			{
				AddTri(f, m.MultiplyPoint3x4(vs[ts[i]]), m.MultiplyPoint3x4(vs[ts[i + 1]]), m.MultiplyPoint3x4(vs[ts[i + 2]]), d);
			}
		}

		static void AddTri(SiteFrame f, Vector3 a, Vector3 b, Vector3 c, Dyn d)
		{
			double x, y, z;
			f.WorldToMc((Vector3d)a, out x, out y, out z); d.Tris.Add((float)x); d.Tris.Add((float)y); d.Tris.Add((float)z);
			f.WorldToMc((Vector3d)b, out x, out y, out z); d.Tris.Add((float)x); d.Tris.Add((float)y); d.Tris.Add((float)z);
			f.WorldToMc((Vector3d)c, out x, out y, out z); d.Tris.Add((float)x); d.Tris.Add((float)y); d.Tris.Add((float)z);
		}

		static void AddOrientedBox(SiteFrame f, Matrix4x4 m, Vector3 center, Vector3 size, Dyn d)
		{
			Vector3 h = size * 0.5f;
			Vector3[] c = new Vector3[8];
			for (int i = 0; i < 8; i++)
				c[i] = m.MultiplyPoint3x4(center + new Vector3((i & 1) == 0 ? -h.x : h.x, (i & 2) == 0 ? -h.y : h.y, (i & 4) == 0 ? -h.z : h.z));
			int[][] faces = { new[] {0,1,3,2}, new[] {4,6,7,5}, new[] {0,4,5,1}, new[] {2,3,7,6}, new[] {0,2,6,4}, new[] {1,5,7,3} };
			foreach (int[] q in faces)
			{
				AddTri(f, c[q[0]], c[q[1]], c[q[2]], d);
				AddTri(f, c[q[0]], c[q[2]], c[q[3]], d);
			}
		}

		static void AddBox(SiteFrame f, Bounds b, Dyn d)
		{
			AddOrientedBox(f, Matrix4x4.identity, b.center, b.size, d);
		}
	}
}
