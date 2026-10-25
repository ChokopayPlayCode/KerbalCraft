using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace KerbalCraft
{
	/// <summary>
	/// Draws what Minecraft's mod ships through the render ring, inside KSP's own scene: block
	/// sections as Unity meshes (so KSP's sun, shadows, fog and depth apply and KSP geometry hides
	/// them correctly), entities and particles, the player's body, dropped items, block cracks and
	/// the block outline. All of it hangs under one root whose local axes are Minecraft's.
	/// </summary>
	public unsafe class WorldRenderer
	{
		public GameObject Root;
		public Transform RootT;

		Texture2D atlas;
		bool atlasStale;
		readonly Dictionary<long, Section> sections = new Dictionary<long, Section>();
		readonly Dictionary<uint, Texture2D> entityTextures = new Dictionary<uint, Texture2D>();
		readonly Dictionary<long, Material> materials = new Dictionary<long, Material>();
		readonly DynamicMesh avatar, scene;
		readonly EntityMesh entities;
		readonly BlockLights lights = new BlockLights();
		readonly SolidBlocks solids = new SolidBlocks();
		readonly Link.WorldEntities worldEntities = new Link.WorldEntities();
		double slotX, slotZ;
		float cullTimer;

		class Section
		{
			public GameObject Go; public Mesh Mesh; public int Sx, Sy, Sz; public byte[] Lights;
		}

		public WorldRenderer()
		{
			Shaders.Load();
			Root = new GameObject("KerbalCraft world");
			RootT = Root.transform;
			RootT.localScale = SiteFrame.RootScale;
			avatar = new DynamicMesh(this, "KerbalCraft avatar");
			scene = new DynamicMesh(this, "KerbalCraft entities");
			entities = new EntityMesh(this);
			Root.AddComponent<OutlineDrawer>().Source = this;
			solids.Init(RootT);
		}

		public void Destroy()
		{
			ClearAll();
			UnityEngine.Object.Destroy(Root);
		}

		public Link.WorldEntities Entities { get { return worldEntities; } }
		public Texture2D Atlas { get { return atlas; } }
		public BlockLights Lights { get { return lights; } }
		public SolidBlocks Solids { get { return solids; } }

		/// <summary>Place the root for this frame. Its local origin is Minecraft (slotX, 0, slotZ).</summary>
		public void Place(SiteFrame f)
		{
			if (f.SlotX != slotX || f.SlotZ != slotZ)
			{
				slotX = f.SlotX; slotZ = f.SlotZ;
				solids.SlotX = slotX; solids.SlotZ = slotZ; solids.Relocate();
				foreach (Section s in sections.Values) PositionSection(s);
			}
			RootT.position = f.RootPosition;
			RootT.rotation = f.RootRotation;
		}

		void PositionSection(Section s)
		{
			s.Go.transform.localPosition = new Vector3((float)(s.Sx * 16 - slotX), s.Sy * 16, (float)(s.Sz * 16 - slotZ));
		}

		/// <summary>Minecraft position -> render-root local position.</summary>
		public Vector3 ToLocal(double x, double y, double z) { return new Vector3((float)(x - slotX), (float)y, (float)(z - slotZ)); }

		// ---- the ring ---------------------------------------------------------------------------

		public void Drain()
		{
			Link.DrainRender(Handle, 16L << 20);
			if (atlasStale && atlas != null) { atlas.Apply(false, false); atlasStale = false; }
		}

		void Handle(uint type, byte* p, int bytes)
		{
			switch (type)
			{
				case Proto.REN_ATLAS: OnAtlas(p, bytes); break;
				case Proto.REN_ATLAS_REGION: OnAtlasRegion(p, bytes); break;
				case Proto.REN_SECTION: OnSection(p, bytes); break;
				case Proto.REN_CLEAR_ALL: ClearAll(); break;
				case Proto.REN_TEXTURE: OnTexture(p, bytes); break;
				case Proto.REN_AVATAR: avatar.Set(p, bytes, false); break;
				case Proto.REN_SCENE: scene.Set(p, bytes, true); break;
				case Proto.REN_LIGHTS: OnLights(p, bytes); break;
				case Proto.REN_SOLIDS: solids.OnSolids(p, bytes); break;
				default: break; // ragdoll, dug blocks: not used in KSP
			}
		}

		void OnAtlas(byte* p, int bytes)
		{
			int w = *(int*)p, h = *(int*)(p + 4);
			if (w <= 0 || h <= 0 || bytes < 8 + (long)w * h * 4) return;
			if (atlas == null || atlas.width != w || atlas.height != h)
			{
				if (atlas != null) UnityEngine.Object.Destroy(atlas);
				atlas = new Texture2D(w, h, TextureFormat.RGBA32, false, false);   // без мипмапов: KSP не размажет текстуры анизотропией
				atlas.filterMode = FilterMode.Point;
				atlas.wrapMode = TextureWrapMode.Clamp;
				atlas.anisoLevel = 0;
				foreach (Material m in materials.Values) if (m.name == "atlas") m.mainTexture = atlas;
			}
			// Rows arrive top first and we keep them in that order: the vertices' V runs the same way.
			atlas.LoadRawTextureData((IntPtr)(p + 8), w * h * 4);
			atlas.Apply(false, false);
			Log.Info("received Minecraft texture atlas {0}x{1}", w, h);
		}

		void OnAtlasRegion(byte* p, int bytes)
		{
			if (atlas == null) return;
			int x = *(int*)p, y = *(int*)(p + 4), w = *(int*)(p + 8), h = *(int*)(p + 12);
			if (w <= 0 || h <= 0 || x < 0 || y < 0 || x + w > atlas.width || y + h > atlas.height || bytes < 16 + w * h * 4) return;
			Color32[] px = new Color32[w * h];
			byte* src = p + 16;
			for (int i = 0; i < px.Length; i++) px[i] = new Color32(src[i * 4], src[i * 4 + 1], src[i * 4 + 2], src[i * 4 + 3]);
			atlas.SetPixels32(x, y, w, h, px);
			atlasStale = true;
		}

		void OnTexture(byte* p, int bytes)
		{
			uint id = *(uint*)p; int w = *(int*)(p + 4), h = *(int*)(p + 8);
			if (w <= 0 || h <= 0 || w > 4096 || h > 4096 || bytes < 16 + (long)w * h * 4) return;
			Texture2D t;
			if (entityTextures.TryGetValue(id, out t)) UnityEngine.Object.Destroy(t);
			t = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
			t.filterMode = FilterMode.Point; t.wrapMode = TextureWrapMode.Clamp;
			t.LoadRawTextureData((IntPtr)(p + 16), w * h * 4);
			t.Apply(false, false);
			entityTextures[id] = t;
			long k1 = ((long)id << 1), k2 = k1 | 1;
			Material m;
			if (materials.TryGetValue(k1, out m)) m.mainTexture = t;
			if (materials.TryGetValue(k2, out m)) m.mainTexture = t;
		}

		/// <summary>Material for an atlas (texture 0) or entity texture batch.</summary>
		public Material MaterialFor(uint texture, bool translucent)
		{
			long key = ((long)texture << 1) | (translucent ? 1L : 0L);
			Material m;
			if (materials.TryGetValue(key, out m)) return m;
			Texture tex = texture == 0 ? (Texture)atlas : (entityTextures.ContainsKey(texture) ? entityTextures[texture] : null);
			m = Shaders.MakeBlockMaterial(translucent, tex);
			m.name = texture == 0 ? "atlas" : "entity" + texture;
			materials[key] = m;
			return m;
		}

		// ---- sections ---------------------------------------------------------------------------

		static long Key(int x, int y, int z) { return ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF); }

		// Minecraft Direction ordinal -> face normal: down, up, north, south, west, east.
		static readonly Vector3[] Normals = {
			new Vector3(0, -1, 0), new Vector3(0, 1, 0), new Vector3(0, 0, -1), new Vector3(0, 0, 1), new Vector3(-1, 0, 0), new Vector3(1, 0, 0)
		};

		void OnSection(byte* p, int bytes)
		{
			int sx = *(int*)p, sy = *(int*)(p + 4), sz = *(int*)(p + 8);
			uint count = *(uint*)(p + 12); count -= count % 3;
			long key = Key(sx, sy, sz);
			Section s;
			if (sections.TryGetValue(key, out s))
			{
				UnityEngine.Object.Destroy(s.Mesh); UnityEngine.Object.Destroy(s.Go);
				sections.Remove(key);
			}
			if (count == 0 || bytes < 16 + (long)count * Proto.REN_VERTEX_BYTES) return;
			Mesh mesh = BuildMesh((RenVertex*)(p + 16), (int)count, true);
			s = new Section { Sx = sx, Sy = sy, Sz = sz, Mesh = mesh };
			s.Go = new GameObject("section " + sx + "," + sy + "," + sz);
			s.Go.transform.SetParent(RootT, false);
			PositionSection(s);
			s.Go.AddComponent<MeshFilter>().sharedMesh = mesh;
			MeshRenderer r = s.Go.AddComponent<MeshRenderer>();
			r.sharedMaterials = new Material[] { MaterialFor(0, false), MaterialFor(0, true) };
			r.shadowCastingMode = ShadowCastingMode.On;
			r.receiveShadows = true;
			sections[key] = s;
		}

		public struct RenVertex
		{
			public float x, y, z, u, v; public uint color, light, flags;
		}

		/// <summary>Triangle list in RenVertex layout -> Mesh with an opaque/cutout and a translucent submesh.</summary>
		public static Mesh BuildMesh(RenVertex* v, int count, bool split)
		{
			Vector3[] pos = new Vector3[count];
			Vector2[] uv = new Vector2[count], uv2 = new Vector2[count];
			Color32[] col = new Color32[count];
			Vector3[] nrm = new Vector3[count];
			int opaque = 0, trans = 0;
			for (int i = 0; i < count; i++)
			{
				pos[i] = new Vector3(v[i].x, v[i].y, v[i].z);
				uv[i] = new Vector2(v[i].u, v[i].v);
				uint c = v[i].color;
				col[i] = Shaders.VertexColours ? new Color32((byte)c, (byte)(c >> 8), (byte)(c >> 16), (byte)(c >> 24)) : new Color32(255, 255, 255, (byte)(c >> 24));
				uv2[i] = new Vector2((v[i].light & 0xFF) / 15f, ((v[i].light >> 8) & 0xFF) / 15f);
				int ni = (int)((v[i].flags >> 4) & 7);
				nrm[i] = ni >= 1 && ni <= 6 ? Normals[ni - 1] : Vector3.up;
				if (i % 3 == 0) { if ((v[i].flags & 2) != 0) trans += 3; else opaque += 3; }
			}
			int[] a = new int[opaque], t = new int[trans];
			int ai = 0, ti = 0;
			for (int i = 0; i < count; i += 3)
			{
				if ((v[i].flags & 2) != 0) { t[ti++] = i; t[ti++] = i + 1; t[ti++] = i + 2; }
				else { a[ai++] = i; a[ai++] = i + 1; a[ai++] = i + 2; }
			}
			Mesh m = new Mesh();
			m.indexFormat = count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
			m.vertices = pos; m.uv = uv; m.uv2 = uv2; m.colors32 = col; m.normals = nrm;
			m.subMeshCount = 2;
			m.SetTriangles(a, 0, false); m.SetTriangles(t, 1, false);
			m.RecalculateBounds();
			return m;
		}

		void ClearAll()
		{
			foreach (Section s in sections.Values) { UnityEngine.Object.Destroy(s.Mesh); UnityEngine.Object.Destroy(s.Go); }
			sections.Clear();
			lights.Clear();
			solids.Clear();
			avatar.Clear(); scene.Clear();
		}

		void OnLights(byte* p, int bytes)
		{
			int sx = *(int*)p, sy = *(int*)(p + 4), sz = *(int*)(p + 8), n = *(int*)(p + 12);
			long key = Key(sx, sy, sz);
			if (n <= 0 || bytes < 16 + n * 8) { lights.Remove(key); return; }
			lights.Set(key, sx, sy, sz, p + 16, n);
		}

		// ---- steve / ksp mode -------------------------------------------------------------------

		bool steveMode = true;

		/// <summary>True while Steve mode is on: the player, particles and dropped items are shown.</summary>
		public bool SteveMode { get { return steveMode; } }

		/// <summary>
		/// Steve mode on: everything Minecraft ships is drawn. Off (KSP mode): only the blocks stay.
		/// </summary>
		public void SetSteveMode(bool on)
		{
			steveMode = on;
			avatar.SetVisible(false);   // Frame shows the body again while Steve is on
			scene.SetVisible(on);
			entities.SetShown(on);
		}

		/// <summary>
		/// KSP mode: the blocks built so far stay in KSP's own scene where they were placed. The render
		/// ring is still drained (Minecraft keeps sending section updates while the player is parked),
		/// the root follows the site (the caller updates the frame first), and sections are culled
		/// against KSP's camera.
		/// </summary>
		public void Keep(SiteFrame f, Vector3 cameraWorld)
		{
			Drain();
			Place(f);
			Vector3 cameraLocal = RootT.InverseTransformPoint(cameraWorld);
			CullSections(cameraLocal);
			lights.Update(this, cameraLocal);
		}

		void CullSections(Vector3 cameraLocal)
		{
			cullTimer -= Time.unscaledDeltaTime;
			if (cullTimer > 0f) return;
			cullTimer = 0.5f;
			float r2 = 400f * 400f;
			foreach (Section s in sections.Values)
			{
				Vector3 c = new Vector3((float)(s.Sx * 16 - slotX) + 8f, s.Sy * 16 + 8f, (float)(s.Sz * 16 - slotZ) + 8f);
				bool near = (c - cameraLocal).sqrMagnitude < r2;
				if (s.Go.activeSelf != near) s.Go.SetActive(near);
			}
		}

		// ---- per frame --------------------------------------------------------------------------

		/// <summary>Everything that follows the camera: dynamic meshes, entities, culling, lights.</summary>
		public void Frame(Vector3 cameraLocal, double feetX, double feetY, double feetZ, bool thirdPerson, bool dead)
		{
			avatar.SetVisible(thirdPerson && !dead);
			avatar.Go.transform.localPosition = ToLocal(feetX, feetY, feetZ);
			scene.ApplyOrigin();
			if (Link.ReadWorldEntities(worldEntities)) entities.Build(worldEntities);
			CullSections(cameraLocal);
			lights.Update(this, cameraLocal);
		}

		// ---- dynamic meshes (player body, entities, particles) ----------------------------------

		class DynamicMesh
		{
			public GameObject Go; Mesh mesh; MeshRenderer renderer; WorldRenderer owner;
			double ox, oy, oz; bool hasOrigin; bool hasData;

			public DynamicMesh(WorldRenderer o, string name)
			{
				owner = o;
				Go = new GameObject(name);
				Go.transform.SetParent(o.RootT, false);
				mesh = new Mesh(); mesh.MarkDynamic();
				Go.AddComponent<MeshFilter>().sharedMesh = mesh;
				renderer = Go.AddComponent<MeshRenderer>();
				renderer.shadowCastingMode = ShadowCastingMode.On;
				renderer.receiveShadows = true;
			}

			public void SetVisible(bool v) { renderer.enabled = v && hasData; }

			public void Clear() { mesh.Clear(); hasData = false; renderer.enabled = false; }

			/// <summary>RenScene positions are relative to an origin block; RenAvatar's to the player's feet.</summary>
			public void ApplyOrigin()
			{
				if (hasOrigin) Go.transform.localPosition = owner.ToLocal(ox, oy, oz);
			}

			public void Set(byte* p, int bytes, bool withOrigin)
			{
				int head = withOrigin ? 32 : 8;
				if (bytes < head) return;
				uint batchCount, vertexCount;
				if (withOrigin) { ox = *(double*)p; oy = *(double*)(p + 8); oz = *(double*)(p + 16); batchCount = *(uint*)(p + 24); vertexCount = *(uint*)(p + 28); hasOrigin = true; }
				else { batchCount = *(uint*)p; vertexCount = *(uint*)(p + 4); }
				if (batchCount == 0 || vertexCount == 0 || bytes < head + (long)batchCount * 16 + (long)vertexCount * Proto.REN_VERTEX_BYTES) { Clear(); return; }
				byte* batches = p + head;
				RenVertex* verts = (RenVertex*)(batches + batchCount * 16);
				int n = (int)vertexCount;
				Vector3[] pos = new Vector3[n]; Vector2[] uv = new Vector2[n], uv2 = new Vector2[n]; Color32[] col = new Color32[n]; Vector3[] nrm = new Vector3[n];
				for (int i = 0; i < n; i++)
				{
					pos[i] = new Vector3(verts[i].x, verts[i].y, verts[i].z);
					uv[i] = new Vector2(verts[i].u, verts[i].v);
					uint c = verts[i].color;
					col[i] = Shaders.VertexColours ? new Color32((byte)c, (byte)(c >> 8), (byte)(c >> 16), (byte)(c >> 24)) : new Color32(255, 255, 255, (byte)(c >> 24));
					uv2[i] = new Vector2((verts[i].light & 0xFF) / 15f, ((verts[i].light >> 8) & 0xFF) / 15f);
					int ni = (int)((verts[i].flags >> 4) & 7);
					nrm[i] = ni >= 1 && ni <= 6 ? Normals[ni - 1] : Vector3.up;
				}
				mesh.Clear();
				mesh.indexFormat = n > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
				mesh.vertices = pos; mesh.uv = uv; mesh.uv2 = uv2; mesh.colors32 = col; mesh.normals = nrm;
				mesh.subMeshCount = (int)batchCount;
				Material[] mats = new Material[batchCount];
				for (int b = 0; b < batchCount; b++)
				{
					uint* bt = (uint*)(batches + b * 16);
					uint tex = bt[0], first = bt[1], cnt = bt[2], flags = bt[3];
					if (first + cnt > vertexCount) { cnt = 0; first = 0; }
					int[] idx = new int[cnt];
					for (int i = 0; i < cnt; i++) idx[i] = (int)first + i;
					mesh.SetTriangles(idx, b, false);
					mats[b] = owner.MaterialFor(tex, (flags & 1) != 0);
				}
				mesh.RecalculateBounds();
				renderer.sharedMaterials = mats;
				hasData = true; renderer.enabled = owner.SteveMode;
			}
		}

		// ---- dropped items, arrows, block cracks (WorldEntities) --------------------------------

		class EntityMesh
		{
			GameObject go; Mesh mesh; MeshRenderer renderer; WorldRenderer owner;
			readonly List<Vector3> pos = new List<Vector3>(); readonly List<Vector2> uv = new List<Vector2>();
			readonly List<Color32> col = new List<Color32>(); readonly List<Vector3> nrm = new List<Vector3>(); readonly List<int> idx = new List<int>();

			public EntityMesh(WorldRenderer o)
			{
				owner = o;
				go = new GameObject("KerbalCraft items");
				go.transform.SetParent(o.RootT, false);
				mesh = new Mesh(); mesh.MarkDynamic();
				go.AddComponent<MeshFilter>().sharedMesh = mesh;
				renderer = go.AddComponent<MeshRenderer>();
				renderer.shadowCastingMode = ShadowCastingMode.Off;
			}

			public void SetShown(bool shown) { renderer.enabled = shown && pos.Count > 0; }

			void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float u0, float v0, float u1, float v1, Color32 tint, Vector3 n)
			{
				int i = pos.Count;
				pos.Add(a); pos.Add(b); pos.Add(c); pos.Add(d);
				uv.Add(new Vector2(u0, v0)); uv.Add(new Vector2(u0, v1)); uv.Add(new Vector2(u1, v1)); uv.Add(new Vector2(u1, v0));
				for (int k = 0; k < 4; k++) { col.Add(tint); nrm.Add(n); }
				idx.Add(i); idx.Add(i + 1); idx.Add(i + 2); idx.Add(i); idx.Add(i + 2); idx.Add(i + 3);
			}

			void Box(Vector3 mn, Vector3 sz, float rot, float[] side, float[] top, float[] bottom, Color32 topTint)
			{
				Vector3 c = mn + sz * 0.5f;
				Quaternion q = Quaternion.AngleAxis(rot, Vector3.up);
				Vector3 h = sz * 0.5f;
				Func<float, float, float, Vector3> P = (x, y, z) => c + q * new Vector3(x * h.x, y * h.y, z * h.z);
				Color32 w = new Color32(255, 255, 255, 255);
				// +Y, -Y, then the four sides; Minecraft winding (counter-clockwise from outside)
				Quad(P(-1, 1, -1), P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), top[0], top[1], top[2], top[3], topTint, Vector3.up);
				Quad(P(-1, -1, 1), P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), bottom[0], bottom[1], bottom[2], bottom[3], w, Vector3.down);
				Quad(P(1, 1, -1), P(1, -1, -1), P(-1, -1, -1), P(-1, 1, -1), side[0], side[1], side[2], side[3], w, Vector3.back);
				Quad(P(-1, 1, 1), P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), side[0], side[1], side[2], side[3], w, Vector3.forward);
				Quad(P(-1, 1, -1), P(-1, -1, -1), P(-1, -1, 1), P(-1, 1, 1), side[0], side[1], side[2], side[3], w, Vector3.left);
				Quad(P(1, 1, 1), P(1, -1, 1), P(1, -1, -1), P(1, 1, -1), side[0], side[1], side[2], side[3], w, Vector3.right);
			}

			static float[] Rect(ref Link.WorldEntity e, int i)
			{
				return i == 0 ? new[] { e.U00, e.V00, e.U01, e.V01 } : i == 1 ? new[] { e.U10, e.V10, e.U11, e.V11 } : new[] { e.U20, e.V20, e.U21, e.V21 };
			}

			public void Build(Link.WorldEntities we)
			{
				pos.Clear(); uv.Clear(); col.Clear(); nrm.Clear(); idx.Clear();
				for (int n = 0; n < we.Count; n++)
				{
					Link.WorldEntity e = we.Items[n];
					Vector3 p = owner.ToLocal(e.X, e.Y, e.Z);
					float[] r0 = Rect(ref e, 0);
					switch (e.Kind)
					{
						case Proto.WE_ITEM:
						{
							// a flat sprite turning about the vertical
							Quaternion q = Quaternion.AngleAxis(e.Yaw, Vector3.up);
							float s = e.Scale * 0.5f;
							Vector3 right = q * Vector3.right * s, up = Vector3.up * s;
							Quad(p - right + up, p - right - up, p + right - up, p + right + up, r0[0], r0[1], r0[2], r0[3], new Color32(255, 255, 255, 255), q * Vector3.forward);
							Quad(p + right + up, p + right - up, p - right - up, p - right + up, r0[0], r0[1], r0[2], r0[3], new Color32(255, 255, 255, 255), q * Vector3.back);
							break;
						}
						case Proto.WE_ARROW:
						case Proto.WE_TRIDENT:
						{
							// two crossed sprites along the flight direction (yaw: 0 = +Z, pitch: up positive)
							Quaternion q = Quaternion.AngleAxis(e.Yaw, Vector3.up) * Quaternion.AngleAxis(-e.Pitch, Vector3.right);
							float len = e.Scale * 0.6f, wd = e.Scale * 0.3f;
							Vector3 f = q * Vector3.forward * len, up = q * Vector3.up * wd, side = q * Vector3.right * wd;
							Quad(p - f + up, p - f - up, p + f - up, p + f + up, r0[0], r0[1], r0[2], r0[3], new Color32(255, 255, 255, 255), q * Vector3.right);
							Quad(p + f + up, p + f - up, p - f - up, p - f + up, r0[0], r0[1], r0[2], r0[3], new Color32(255, 255, 255, 255), q * Vector3.left);
							Quad(p - f + side, p - f - side, p + f - side, p + f + side, r0[0], r0[1], r0[2], r0[3], new Color32(255, 255, 255, 255), q * Vector3.up);
							Quad(p + f + side, p + f - side, p - f - side, p - f + side, r0[0], r0[1], r0[2], r0[3], new Color32(255, 255, 255, 255), q * Vector3.down);
							break;
						}
						case Proto.WE_BLOCK:
						{
							float s = e.Scale;
							Color32 tint = e.Tint == 0 ? new Color32(255, 255, 255, 255) : new Color32((byte)e.Tint, (byte)(e.Tint >> 8), (byte)(e.Tint >> 16), 255);
							Box(p - new Vector3(s, s, s) * 0.5f, new Vector3(s, s, s), e.Yaw, r0, Rect(ref e, 1), Rect(ref e, 2), tint);
							break;
						}
						case Proto.WE_CRACK:
						{
							// the cracks of a block being broken: the crack frame on all six faces, a hair outside
							Vector3 ext = new Vector3(e.E0, e.E1, e.E2) + Vector3.one * 0.004f;
							Box(p - Vector3.one * 0.002f, ext, 0f, r0, r0, r0, new Color32(255, 255, 255, 255));
							break;
						}
						default: break; // contact shadows: KSP casts its own
					}
				}
				mesh.Clear();
				if (pos.Count == 0) { renderer.enabled = false; return; }
				mesh.indexFormat = pos.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
				mesh.SetVertices(pos); mesh.SetUVs(0, uv); mesh.SetColors(col); mesh.SetNormals(nrm); mesh.SetTriangles(idx, 0, true);
				renderer.sharedMaterial = owner.MaterialFor(0, false);
				renderer.enabled = owner.SteveMode;
			}
		}

		// ---- block outline (GL lines) -----------------------------------------------------------

		public class OutlineDrawer : MonoBehaviour
		{
			public WorldRenderer Source;
			static Material mat;

			void OnRenderObject()
			{
				if (Source == null || !Source.SteveMode || !Source.worldEntities.HasSelection) return;
				Camera cam = Camera.current;
				if (cam == null || !(cam.name.StartsWith("Camera 0"))) return;
				if (mat == null)
				{
					Shader s = Shader.Find("Hidden/Internal-Colored");
					if (s == null) return;
					mat = new Material(s) { hideFlags = HideFlags.HideAndDontSave };
					mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha); mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
					mat.SetInt("_Cull", (int)CullMode.Off); mat.SetInt("_ZWrite", 0);
				}
				float[] s6 = Source.worldEntities.Sel;
				Vector3 a = Source.ToLocal(s6[0], s6[1], s6[2]) - Vector3.one * 0.002f, b = Source.ToLocal(s6[3], s6[4], s6[5]) + Vector3.one * 0.002f;
				mat.SetPass(0);
				GL.PushMatrix();
				GL.MultMatrix(Source.RootT.localToWorldMatrix);
				GL.Begin(GL.LINES);
				GL.Color(new Color(0f, 0f, 0f, 0.65f));
				Vector3[] c = {
					new Vector3(a.x, a.y, a.z), new Vector3(b.x, a.y, a.z), new Vector3(b.x, a.y, b.z), new Vector3(a.x, a.y, b.z),
					new Vector3(a.x, b.y, a.z), new Vector3(b.x, b.y, a.z), new Vector3(b.x, b.y, b.z), new Vector3(a.x, b.y, b.z) };
				int[,] edges = { {0,1},{1,2},{2,3},{3,0},{4,5},{5,6},{6,7},{7,4},{0,4},{1,5},{2,6},{3,7} };
				for (int i = 0; i < 12; i++) { GL.Vertex(c[edges[i, 0]]); GL.Vertex(c[edges[i, 1]]); }
				GL.End();
				GL.PopMatrix();
			}
		}
	}
}
