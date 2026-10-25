using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace KerbalCraft
{
	/// <summary>
	/// KSP's end of the shared-memory link. KSP creates the mapping and owns it; the Minecraft
	/// Fabric mod opens it. Layout: protocol constants in Proto.cs (generated from Proto.java).
	/// Windows only (kernel32 named file mapping), like the Fabric side.
	/// </summary>
	public static unsafe class Link
	{
		const uint PAGE_READWRITE = 0x04;
		const uint FILE_MAP_ALL_ACCESS = 0xF001F;
		const long INVALID_HANDLE = -1;

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		static extern IntPtr CreateFileMappingW(IntPtr file, IntPtr attributes, uint protect, uint maxHigh, uint maxLow, string name);
		[DllImport("kernel32.dll", SetLastError = true)]
		static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offHigh, uint offLow, UIntPtr bytes);
		[DllImport("kernel32.dll")] static extern ulong GetTickCount64();
		[DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
		[DllImport("kernel32.dll")] static extern int QueryPerformanceCounter(out long value);
		[DllImport("kernel32.dll")] static extern int QueryPerformanceFrequency(out long value);
		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		static extern IntPtr OpenMutexW(uint access, int inherit, string name);
		[DllImport("kernel32.dll")] static extern int CloseHandle(IntPtr handle);

		static IntPtr mapping;
		static byte* b;
		static int overlayFront = 2;
		const ulong McTimeoutMs = 3000;

		public static bool Valid { get { return b != null; } }
		public static byte* Base { get { return b; } }

		public static long Qpc() { long v; QueryPerformanceCounter(out v); return v; }
		public static long QpcFrequency() { long v; QueryPerformanceFrequency(out v); return v; }

		/// <summary>True if a Minecraft with the mod holds its "I am running" mutex.</summary>
		public static bool MinecraftProcessRunning()
		{
			IntPtr h = OpenMutexW(0x00100000 /* SYNCHRONIZE */, 0, Proto.MAPPING_NAME + "_minecraft");
			if (h == IntPtr.Zero) return false;
			CloseHandle(h);
			return true;
		}

		public static bool Create(string worldName)
		{
			if (b != null) { SetWorldName(worldName); return true; }
			ulong size = (ulong)Proto.MAPPING_BYTES;
			mapping = CreateFileMappingW(new IntPtr(INVALID_HANDLE), IntPtr.Zero, PAGE_READWRITE, (uint)(size >> 32), (uint)(size & 0xFFFFFFFF), Proto.MAPPING_NAME);
			if (mapping == IntPtr.Zero) { Log.Error("CreateFileMapping failed ({0})", Marshal.GetLastWin32Error()); return false; }
			IntPtr view = MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, UIntPtr.Zero);
			if (view == IntPtr.Zero) { Log.Error("MapViewOfFile failed ({0})", Marshal.GetLastWin32Error()); return false; }
			b = (byte*)view.ToPointer();
			// A stale mapping can survive while Minecraft still has it open: reset everything we own.
			Zero(Proto.OFF_HOST_STATE, 0x100);
			Zero(Proto.OFF_OVERLAY_CTL, 0x100);
			Zero(Proto.OFF_INPUT_RING, Proto.IR_DATA);
			Zero(Proto.OFF_COLLISION_RING, Proto.CR_DATA);
			Zero(Proto.OFF_ACTOR_TABLE, 0x40 + Proto.ACTOR_RECORD_BYTES * Proto.MAX_ACTORS);
			Zero(Proto.OFF_EVENT_RING, Proto.ER_DATA);
			Zero(Proto.OFF_WORLD_ENTITIES, 0x40 + Proto.WORLD_ENTITY_BYTES * Proto.MAX_WORLD_ENTITIES);
			Zero(Proto.OFF_RENDER_RING, Proto.RR_DATA);
			Zero(Proto.OFF_WATER_GRID, 0x400);
			SetWorldName(worldName);
			*(int*)(b + Proto.H_VERSION) = Proto.VERSION;
			*(uint*)(b + Proto.H_HOST_PID) = GetCurrentProcessId();
			*(ulong*)(b + Proto.H_HOST_HEARTBEAT) = GetTickCount64();
			Thread.MemoryBarrier();
			*(int*)(b + Proto.H_MAGIC) = Proto.MAGIC;
			Log.Info("shared memory {0} created ({1} MB)", Proto.MAPPING_NAME, size >> 20);
			return true;
		}

		static void Zero(long off, long bytes)
		{
			byte* p = b + off;
			for (long i = 0; i < bytes; i++) p[i] = 0;
		}

		public static void SetWorldName(string name)
		{
			if (b == null || name == null) return;
			byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(name);
			int n = Math.Min(utf8.Length, Proto.WORLD_NAME_BYTES - 1);
			byte* p = b + Proto.H_WORLD_NAME;
			for (int i = 0; i < n; i++) p[i] = utf8[i];
			p[n] = 0;
		}

		public static void Heartbeat()
		{
			if (b != null) { Thread.MemoryBarrier(); *(ulong*)(b + Proto.H_HOST_HEARTBEAT) = GetTickCount64(); }
		}

		public static bool McAlive()
		{
			if (b == null) return false;
			ulong last = *(ulong*)(b + Proto.H_MC_HEARTBEAT);
			return last != 0 && GetTickCount64() - last < McTimeoutMs;
		}

		public static uint McPid() { return b == null ? 0 : *(uint*)(b + Proto.H_MC_PID); }

		// ---- HostState (write) -----------------------------------------------------------------

		public class HostState
		{
			public uint Flags, WorldId, CollisionEpoch, TeleportSeq, ViewportW, ViewportH, BodyFlags;
			public double X, Y, Z;
			public float Yaw, Pitch, GameHour, Gravity = 9.80665f, SunElevation;
		}

		public static void WriteHostState(HostState s)
		{
			if (b == null) return;
			byte* p = b + Proto.OFF_HOST_STATE;
			int seq = *(int*)(p + Proto.SS_SEQ);
			*(int*)(p + Proto.SS_SEQ) = seq + 1;
			Thread.MemoryBarrier();
			*(uint*)(p + Proto.SS_FLAGS) = s.Flags;
			*(uint*)(p + Proto.SS_WORLD_ID) = s.WorldId;
			*(uint*)(p + Proto.SS_COLLISION_EPOCH) = s.CollisionEpoch;
			*(double*)(p + Proto.SS_POS_X) = s.X;
			*(double*)(p + Proto.SS_POS_Y) = s.Y;
			*(double*)(p + Proto.SS_POS_Z) = s.Z;
			*(float*)(p + Proto.SS_YAW) = s.Yaw;
			*(float*)(p + Proto.SS_PITCH) = s.Pitch;
			*(uint*)(p + Proto.SS_TELEPORT_SEQ) = s.TeleportSeq;
			*(uint*)(p + Proto.SS_VIEWPORT_W) = s.ViewportW;
			*(uint*)(p + Proto.SS_VIEWPORT_H) = s.ViewportH;
			*(float*)(p + Proto.SS_GAME_HOUR) = s.GameHour;
			*(float*)(p + Proto.SS_GRAVITY) = s.Gravity;
			*(uint*)(p + Proto.SS_BODY_FLAGS) = s.BodyFlags;
			*(float*)(p + Proto.SS_SUN_ELEVATION) = s.SunElevation;
			Thread.MemoryBarrier();
			*(int*)(p + Proto.SS_SEQ) = seq + 2;
		}

		// ---- McState (read) --------------------------------------------------------------------

		public class McState
		{
			public uint Flags, TeleportAck, GuiScale, CameraMode;
			public double X, Y, Z, EyeX, EyeY, EyeZ, PrevX, PrevY, PrevZ, CurX, CurY, CurZ;
			public float Yaw, Pitch, EyeHeight, Sensitivity, Fov, BobPhase, BobAmount;
			public long TickQpc;
			public float TickEyeO, TickEye, WalkDistO, WalkDist, BobO, Bob, TickMs, CameraDistance;
			public ulong FrameCounter;
			public bool InWorld { get { return (Flags & Proto.MC_IN_WORLD) != 0; } }
			public bool ScreenOpen { get { return (Flags & Proto.MC_SCREEN_OPEN) != 0; } }
			public bool OnGround { get { return (Flags & Proto.MC_ON_GROUND) != 0; } }
			public bool Sneaking { get { return (Flags & Proto.MC_SNEAKING) != 0; } }
			public bool Dead { get { return (Flags & Proto.MC_DEAD) != 0; } }
		}

		public static bool ReadMcState(McState o)
		{
			if (b == null) return false;
			byte* p = b + Proto.OFF_MC_STATE;
			for (int attempt = 0; attempt < 64; attempt++)
			{
				int s1 = *(int*)(p + Proto.MS_SEQ);
				Thread.MemoryBarrier();
				if ((s1 & 1) != 0) { Thread.SpinWait(20); continue; }
				o.Flags = *(uint*)(p + Proto.MS_FLAGS);
				o.X = *(double*)(p + Proto.MS_X); o.Y = *(double*)(p + Proto.MS_Y); o.Z = *(double*)(p + Proto.MS_Z);
				o.Yaw = *(float*)(p + Proto.MS_YAW); o.Pitch = *(float*)(p + Proto.MS_PITCH);
				o.EyeHeight = *(float*)(p + Proto.MS_EYE_HEIGHT);
				o.Sensitivity = *(float*)(p + Proto.MS_SENSITIVITY);
				o.TeleportAck = *(uint*)(p + Proto.MS_TELEPORT_ACK);
				o.GuiScale = *(uint*)(p + Proto.MS_GUI_SCALE);
				o.FrameCounter = *(ulong*)(p + Proto.MS_FRAME_COUNTER);
				o.Fov = *(float*)(p + Proto.MS_FOV);
				o.BobPhase = *(float*)(p + Proto.MS_BOB_PHASE);
				o.BobAmount = *(float*)(p + Proto.MS_BOB_AMOUNT);
				o.EyeX = *(double*)(p + Proto.MS_EYE_X); o.EyeY = *(double*)(p + Proto.MS_EYE_Y); o.EyeZ = *(double*)(p + Proto.MS_EYE_Z);
				o.TickQpc = *(long*)(p + Proto.MS_TICK_QPC);
				o.PrevX = *(double*)(p + Proto.MS_PREV_X); o.PrevY = *(double*)(p + Proto.MS_PREV_X + 8); o.PrevZ = *(double*)(p + Proto.MS_PREV_X + 16);
				o.CurX = *(double*)(p + Proto.MS_CUR_X); o.CurY = *(double*)(p + Proto.MS_CUR_X + 8); o.CurZ = *(double*)(p + Proto.MS_CUR_X + 16);
				o.TickEyeO = *(float*)(p + Proto.MS_EYE_HEIGHT_O); o.TickEye = *(float*)(p + Proto.MS_EYE_HEIGHT_T);
				o.WalkDistO = *(float*)(p + Proto.MS_WALK_O); o.WalkDist = *(float*)(p + Proto.MS_WALK);
				o.BobO = *(float*)(p + Proto.MS_BOB_O); o.Bob = *(float*)(p + Proto.MS_BOB);
				o.TickMs = *(float*)(p + Proto.MS_TICK_MS);
				o.CameraMode = *(uint*)(p + Proto.MS_CAMERA_MODE);
				o.CameraDistance = *(float*)(p + Proto.MS_CAMERA_DISTANCE);
				Thread.MemoryBarrier();
				if (*(int*)(p + Proto.MS_SEQ) == s1) return true;
			}
			return false;
		}

		// ---- input ring (produce) --------------------------------------------------------------

		public static void PushInput(int type, int code, int a = 0, int bb = 0, int c = 0)
		{
			if (b == null) return;
			byte* ring = b + Proto.OFF_INPUT_RING;
			ulong head = *(ulong*)(ring + Proto.IR_HEAD);
			Thread.MemoryBarrier();
			ulong tail = *(ulong*)(ring + Proto.IR_TAIL);
			if (head - tail >= (ulong)Proto.INPUT_RING_ENTRIES) return;
			byte* e = ring + Proto.IR_DATA + (long)(head & (ulong)(Proto.INPUT_RING_ENTRIES - 1)) * 16;
			*(ushort*)e = (ushort)type;
			*(ushort*)(e + 2) = (ushort)code;
			*(int*)(e + 4) = a; *(int*)(e + 8) = bb; *(int*)(e + 12) = c;
			Thread.MemoryBarrier();
			*(ulong*)(ring + Proto.IR_HEAD) = head + 1;
		}

		public static void ReleaseAllInput() { PushInput(Proto.IN_RELEASE_ALL, 0); }

		// ---- collision ring (produce) ----------------------------------------------------------

		/// <summary>Writes one message {type, payload[0..bytes)} into the collision ring. False if it is full.</summary>
		public static bool WriteCollision(int type, byte* payload, int bytes)
		{
			if (b == null) return false;
			byte* ring = b + Proto.OFF_COLLISION_RING;
			byte* data = ring + Proto.CR_DATA;
			long size = Proto.CR_DATA_BYTES;
			long msg = (8 + bytes + 7) & ~7L;
			if (msg > size / 2) { Log.Error("collision message too large ({0} bytes)", msg); return false; }
			long head = *(long*)(ring + Proto.CR_HEAD);
			Thread.MemoryBarrier();
			long tail = *(long*)(ring + Proto.CR_TAIL);
			long pos = head % size;
			long pad = pos + msg > size ? size - pos : 0;
			if (size - (head - tail) < msg + pad) return false;
			if (pad > 0)
			{
				*(int*)(data + pos) = Proto.COL_PAD; *(int*)(data + pos + 4) = 0;
				head += pad; pos = 0;
			}
			*(int*)(data + pos) = type; *(int*)(data + pos + 4) = bytes;
			Buffer.MemoryCopy(payload, data + pos + 8, bytes, bytes);
			Thread.MemoryBarrier();
			*(long*)(ring + Proto.CR_HEAD) = head + msg;
			return true;
		}

		// ---- actor table (produce) -------------------------------------------------------------

		public struct Actor
		{
			public uint Id, Flags; public float X, Y, Z, Yaw, Width, Height, HealthFrac; public ushort Level; public string Name;
		}

		public static void WriteActors(Actor[] actors, int count)
		{
			if (b == null) return;
			byte* t = b + Proto.OFF_ACTOR_TABLE;
			int seq = *(int*)(t + Proto.AT_SEQ);
			*(int*)(t + Proto.AT_SEQ) = seq + 1;
			Thread.MemoryBarrier();
			count = Math.Min(count, Proto.MAX_ACTORS);
			*(int*)(t + Proto.AT_COUNT) = count;
			for (int i = 0; i < count; i++)
			{
				byte* r = t + Proto.AT_RECORDS + i * Proto.ACTOR_RECORD_BYTES;
				Actor a = actors[i];
				*(uint*)r = a.Id; *(uint*)(r + 4) = a.Flags;
				*(float*)(r + 8) = a.X; *(float*)(r + 12) = a.Y; *(float*)(r + 16) = a.Z;
				*(float*)(r + 20) = a.Yaw; *(float*)(r + 24) = a.Width; *(float*)(r + 28) = a.Height;
				*(float*)(r + 32) = a.HealthFrac; *(ushort*)(r + 36) = a.Level; *(ushort*)(r + 38) = 0;
				byte* n = r + 40;
				for (int k = 0; k < 24; k++) n[k] = 0;
				if (a.Name != null)
				{
					byte[] u = System.Text.Encoding.UTF8.GetBytes(a.Name);
					int len = Math.Min(u.Length, 23);
					for (int k = 0; k < len; k++) n[k] = u[k];
				}
			}
			Thread.MemoryBarrier();
			*(int*)(t + Proto.AT_SEQ) = seq + 2;
		}

		// ---- event ring (consume) --------------------------------------------------------------

		public struct McEvent { public uint Type, ActorId, Flags, Weapon; public float A, B, C, D; }

		public static bool PopEvent(out McEvent e)
		{
			e = default(McEvent);
			if (b == null) return false;
			byte* ring = b + Proto.OFF_EVENT_RING;
			ulong head = *(ulong*)(ring + Proto.ER_HEAD);
			Thread.MemoryBarrier();
			ulong tail = *(ulong*)(ring + Proto.ER_TAIL);
			if (tail >= head) return false;
			if (head - tail > (ulong)Proto.EVENT_RING_ENTRIES) tail = head - (ulong)Proto.EVENT_RING_ENTRIES;
			byte* p = ring + Proto.ER_DATA + (long)(tail & (ulong)(Proto.EVENT_RING_ENTRIES - 1)) * Proto.EVENT_BYTES;
			e.Type = *(uint*)p; e.ActorId = *(uint*)(p + 4);
			e.A = *(float*)(p + 8); e.B = *(float*)(p + 12); e.C = *(float*)(p + 16); e.D = *(float*)(p + 20);
			e.Flags = *(uint*)(p + 24); e.Weapon = *(uint*)(p + 28);
			Thread.MemoryBarrier();
			*(ulong*)(ring + Proto.ER_TAIL) = tail + 1;
			return true;
		}

		// ---- world entities (read) -------------------------------------------------------------

		public struct WorldEntity
		{
			public uint Kind, Id; public float X, Y, Z, Yaw, Pitch, Scale; public float E0, E1, E2;
			public float U00, V00, U01, V01, U10, V10, U11, V11, U20, V20, U21, V21; public uint Tint;
		}

		public class WorldEntities
		{
			public bool HasSelection; public float[] Sel = new float[6];
			public int Count; public WorldEntity[] Items = new WorldEntity[Proto.MAX_WORLD_ENTITIES];
		}

		public static bool ReadWorldEntities(WorldEntities o)
		{
			if (b == null) return false;
			byte* p = b + Proto.OFF_WORLD_ENTITIES;
			for (int attempt = 0; attempt < 16; attempt++)
			{
				int s1 = *(int*)(p + Proto.WE_SEQ);
				Thread.MemoryBarrier();
				if ((s1 & 1) != 0 || s1 == 0) { if (s1 == 0) return false; Thread.SpinWait(20); continue; }
				int count = Math.Min(*(int*)(p + Proto.WE_COUNT), Proto.MAX_WORLD_ENTITIES);
				o.HasSelection = *(int*)(p + Proto.WE_HAS_SELECTION) != 0;
				for (int i = 0; i < 6; i++) o.Sel[i] = *(float*)(p + Proto.WE_SEL_MIN + i * 4);
				for (int i = 0; i < count; i++)
				{
					byte* r = p + Proto.WE_RECORDS + i * Proto.WORLD_ENTITY_BYTES;
					WorldEntity w = new WorldEntity();
					w.Kind = *(uint*)r; w.Id = *(uint*)(r + 4);
					w.X = *(float*)(r + 8); w.Y = *(float*)(r + 12); w.Z = *(float*)(r + 16);
					w.Yaw = *(float*)(r + 20); w.Pitch = *(float*)(r + 24); w.Scale = *(float*)(r + 28);
					w.E0 = *(float*)(r + 32); w.E1 = *(float*)(r + 36); w.E2 = *(float*)(r + 40);
					float* uv = (float*)(r + 44);
					w.U00 = uv[0]; w.V00 = uv[1]; w.U01 = uv[2]; w.V01 = uv[3];
					w.U10 = uv[4]; w.V10 = uv[5]; w.U11 = uv[6]; w.V11 = uv[7];
					w.U20 = uv[8]; w.V20 = uv[9]; w.U21 = uv[10]; w.V21 = uv[11];
					w.Tint = *(uint*)(r + 92);
					o.Items[i] = w;
				}
				Thread.MemoryBarrier();
				if (*(int*)(p + Proto.WE_SEQ) == s1) { o.Count = count; return true; }
			}
			return false;
		}

		// ---- render ring (consume) -------------------------------------------------------------

		public delegate void RenderHandler(uint type, byte* payload, int bytes);

		/// <summary>Hands pending render messages to the handler, up to about maxBytes. Payloads point into shared memory.</summary>
		public static void DrainRender(RenderHandler handler, long maxBytes)
		{
			if (b == null) return;
			byte* ring = b + Proto.OFF_RENDER_RING;
			long head = *(long*)(ring + Proto.RR_HEAD);
			Thread.MemoryBarrier();
			long tail = *(long*)(ring + Proto.RR_TAIL);
			byte* data = ring + Proto.RR_DATA;
			long size = Proto.RR_DATA_BYTES;
			long done = 0;
			while (tail < head && done < maxBytes)
			{
				long pos = tail % size;
				uint type = *(uint*)(data + pos);
				int payload = *(int*)(data + pos + 4);
				if (type == Proto.REN_PAD) { tail += size - pos; continue; }
				try { handler(type, data + pos + 8, payload); }
				catch (Exception e) { Log.Error("render message {0} failed: {1}", type, e); }
				tail += (8 + payload + 7) & ~7L;
				done += payload;
			}
			Thread.MemoryBarrier();
			*(long*)(ring + Proto.RR_TAIL) = tail;
		}

		// ---- overlay (consume) -----------------------------------------------------------------

		public static void ResetOverlay() { overlayFront = 2; }

		/// <summary>If Minecraft published a newer frame, swaps it into the front slot and returns true.</summary>
		public static bool AcquireOverlayFrame()
		{
			if (b == null) return false;
			int* state = (int*)(b + Proto.OFF_OVERLAY_CTL + Proto.OC_STATE);
			if ((*state & Proto.OVERLAY_DIRTY) == 0) return false;
			int old = Interlocked.Exchange(ref *state, overlayFront);
			overlayFront = old & 3;
			return true;
		}

		public static byte* FrontPixels { get { return b + Proto.OFF_OVERLAY_PIXELS + (long)overlayFront * Proto.OVERLAY_SLOT_BYTES; } }

		public static void FrontHeader(out int width, out int height, out bool bottomUp)
		{
			byte* h = b + Proto.OFF_OVERLAY_SLOT_HDR + (long)overlayFront * Proto.SLOT_HDR_SIZE;
			width = *(int*)(h + Proto.SH_WIDTH);
			height = *(int*)(h + Proto.SH_HEIGHT);
			bottomUp = (*(int*)(h + Proto.SH_FLAGS) & 1) != 0;
		}
	}
}
