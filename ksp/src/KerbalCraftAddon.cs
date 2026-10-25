using System;
using UnityEngine;

namespace KerbalCraft
{
	/// <summary>Runs once from the main menu: opens the shared memory and (optionally) starts Minecraft early, since it takes a while to boot.</summary>
	[KSPAddon(KSPAddon.Startup.MainMenu, true)]
	public class KerbalCraftBoot : MonoBehaviour
	{
		void Start()
		{
			Settings.Load();
			Shaders.Load();
			if (!Link.Create("KerbalCraft")) return;
			if (Settings.AutoStartMinecraft) Launcher.Start();
			DontDestroyOnLoad(this);
		}

		void Update() { Link.Heartbeat(); }
	}

	/// <summary>
	/// Steve mode in the flight scene. Toggle with the configured key (Insert by default) while
	/// landed or on EVA: the camera becomes Minecraft's player, KSP's controls are locked, and
	/// the ground around you turns into a Minecraft world you can walk, dig and build in.
	/// </summary>
	[KSPAddon(KSPAddon.Startup.Flight, false)]
	public class KerbalCraftAddon : MonoBehaviour
	{
		const string LockId = "KerbalCraft";
		const ControlTypes Locks = ControlTypes.ALL_SHIP_CONTROLS | ControlTypes.CAMERACONTROLS | ControlTypes.TIMEWARP | ControlTypes.PAUSE
			| ControlTypes.QUICKSAVE | ControlTypes.QUICKLOAD | ControlTypes.EVA_INPUT | ControlTypes.MAP;

		bool active;
		WorldRenderer world;
		Overlay overlay;
		InputBridge input;
		readonly CameraDriver cam = new CameraDriver();
		readonly TickInterpolator interp = new TickInterpolator();
		readonly CollisionExporter coll = new CollisionExporter();
		readonly VesselMirror mirror = new VesselMirror();
		readonly Hazards hazards = new Hazards();
		readonly Link.McState mc = new Link.McState();
		readonly Link.HostState hs = new Link.HostState();

		Site site; SiteFrame frame; CelestialBody body; Vessel anchorVessel;
		float yaw, pitch;
		double spawnX, spawnY, spawnZ;          // where to (re)spawn, Minecraft coordinates
		double lastX, lastY, lastZ;             // last known player position, Minecraft coordinates
		uint epoch;
		bool uiHidden;
		float noticeTimer;
		Vector3 eyeWorld;
		Quaternion lastRotation = Quaternion.identity;
		// Straight up or down makes Quaternion.LookRotation turn the whole view (sky included) around.
		const float PitchLimit = 89f;

		// ---- lifecycle --------------------------------------------------------------------------

		void Start() { Settings.Load(); }

		void OnDestroy() { if (active) Exit(false); DestroyWorld(); cam.Unhook(); }

		void Update()
		{
			Link.Heartbeat();
			if (!active)
			{
				if (Input.GetKeyDown(Settings.ToggleKey)) TryEnter();
				else KeepBuilt();
				return;
			}
			try { UpdateActive(); }
			catch (Exception e)
			{
				Log.Error("Steve mode failed, leaving it: {0}", e);
				Exit(true);
			}
		}

		void FixedUpdate()
		{
			if (!active || !Settings.FollowWithEva || anchorVessel == null || !anchorVessel.isEVA || frame == null) return;
			try
			{
				anchorVessel.SetPosition(frame.McToWorld(lastX, lastY + 0.2, lastZ));
				if (anchorVessel.rootPart != null && anchorVessel.rootPart.Rigidbody != null) anchorVessel.rootPart.Rigidbody.velocity = Vector3.zero;
			}
			catch (Exception e) { Log.Warn("FollowWithEva failed: {0}", e.Message); Settings.FollowWithEva = false; }
		}

		void OnGUI()
		{
			if (active && overlay != null) overlay.Draw();
		}

		static void Say(string text) { ScreenMessages.PostScreenMessage("[KerbalCraft] " + text, 5f, ScreenMessageStyle.UPPER_CENTER); }

		// ---- entering and leaving ---------------------------------------------------------------

		void TryEnter()
		{
			if (!Link.Valid) { Say("The Minecraft link isn't available (Windows only)."); return; }
			Vessel v = FlightGlobals.ActiveVessel;
			if (v == null) return;
			if (!Link.McAlive())
			{
				Launcher.Start();
				Say("Minecraft isn't running yet. Starting it; press " + Settings.ToggleKey + " again once it is up.");
				return;
			}
			if (!v.LandedOrSplashed && v.situation != Vessel.Situations.PRELAUNCH)
			{
				Say("Land first: Steve walks on the ground, not in orbit.");
				return;
			}
			anchorVessel = v;
			body = v.mainBody;

			// Spawn a few metres in front of the camera, beside the vessel
			Vector3d vp = v.GetWorldPos3D();
			Vector3d up = (vp - body.position).normalized;
			Vector3d fwd = (Vector3d)(Vector3)(FlightCamera.fetch != null && FlightCamera.fetch.mainCamera != null ? FlightCamera.fetch.mainCamera.transform.forward : v.transform.forward);
			fwd -= up * Vector3d.Dot(fwd, up);
			if (fwd.magnitude < 0.1) fwd = Vector3d.Cross(up, (Vector3d)(Vector3)v.transform.right);
			fwd = fwd.normalized;
			double clearance = v.isEVA ? 0.0 : Math.Max(5.0, v.vesselSize.magnitude * 0.6 + 2.0);
			Vector3d spawnWorld = vp + fwd * clearance;

			site = Sites.Find(body, spawnWorld, Settings.SiteRadius) ?? Sites.Create(body, spawnWorld);
			frame = new SiteFrame(site, body);
			Link.SetWorldName(Sites.WorldName);
			Link.ResetOverlay();

			epoch++;
			coll.Reset(frame, epoch);
			double mx, my, mz;
			frame.WorldToMc(spawnWorld, out mx, out my, out mz);
			double ground = coll.Sampler.Compute(mx - frame.SlotX, mz - frame.SlotZ);
			spawnX = mx; spawnY = ground + 0.05; spawnZ = mz;
			double vx, vy, vz;
			frame.WorldToMc(vp, out vx, out vy, out vz);
			yaw = (float)(Math.Atan2(-(vx - mx), vz - mz) * 180.0 / Math.PI);
			pitch = 8f;
			lastX = spawnX; lastY = spawnY; lastZ = spawnZ;

			// The blocks built so far stay in KSP's scene between Steve sessions: reuse their renderer.
			if (world == null) world = new WorldRenderer();
			world.SetSteveMode(true);
			overlay = new Overlay();
			input = new InputBridge();
			interp.Reset();
			hs.X = spawnX; hs.Y = spawnY; hs.Z = spawnZ;
			hs.TeleportSeq++;

			cam.Hook();
			InputLockManager.SetControlLock(Locks, LockId);
			if (Settings.HideKspUi) { GameEvents.onHideUI.Fire(); uiHidden = true; }
			active = true;
			Log.Info("Steve mode on: {0} site {1}", body.bodyName, site.Id);
		}

		void Exit(bool announce)
		{
			active = false;
			cam.Active = false;
			cam.Unhook();
			hs.Flags = 0;
			Link.WriteHostState(hs);
			Link.ReleaseAllInput();
			InputLockManager.RemoveControlLock(LockId);
			if (uiHidden) { GameEvents.onShowUI.Fire(); uiHidden = false; }
			Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
			// The blocks stay in KSP's scene (see KeepBuilt); only Steve's own extras go away.
			if (world != null) world.SetSteveMode(false);
			if (overlay != null) { overlay.Destroy(); overlay = null; }
			mirror.Clear();
			if (FlightCamera.fetch != null && FlightGlobals.ActiveVessel != null)
				FlightCamera.fetch.SetTargetVessel(FlightGlobals.ActiveVessel);   // камера обратно к кораблю
			if (announce) Say("Steve mode off.");
		}

		/// <summary>
		/// KSP mode: Steve is off, but the blocks around the site stay where they were built, drawn in
		/// KSP's own scene. Minecraft keeps the parked player's world up to date, so the render ring keeps
		/// draining and the site keeps following the body.
		/// </summary>
		void KeepBuilt()
		{
			if (world == null || frame == null || !Link.Valid) return;
			try
			{
				frame.Update();
				Vector3 cameraWorld = FlightCamera.fetch != null ? FlightCamera.fetch.transform.position : (Vector3)frame.Origin;
				world.Keep(frame, cameraWorld);
			}
			catch (Exception e)
			{
				Log.Warn("keeping the built blocks failed, dropping them: {0}", e.Message);
				DestroyWorld();
			}
		}

		void DestroyWorld()
		{
			if (world != null) { world.Destroy(); world = null; }
		}

		// ---- per frame --------------------------------------------------------------------------

		void UpdateActive()
		{
			float dt = Time.unscaledDeltaTime;
			frame.Update();
			Link.ReadMcState(mc);
			interp.Feed(mc);

			if (!Link.McAlive()) { Say("Minecraft stopped responding."); Exit(false); return; }

			float dx, dy; bool toggle, board;
			int ow = Math.Max((int)(Screen.width * Settings.OverlayScale), 64), oh = Math.Max((int)(Screen.height * Settings.OverlayScale), 64);
			input.Poll(mc.ScreenOpen, ow, oh, out dx, out dy, out toggle, out board);
			if (toggle) { Exit(true); return; }

			if (!mc.ScreenOpen)
			{
				float s = Mathf.Clamp01(mc.Sensitivity) * 0.6f + 0.2f;
				float factor = s * s * s * 8f * 0.15f;
				yaw += dx * factor;
				pitch = Mathf.Clamp(pitch + dy * factor, -PitchLimit, PitchLimit);
				Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
			}
			else { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }

			bool pending = mc.TeleportAck != hs.TeleportSeq;
			TickInterpolator.Pose pose;
			bool ok = interp.Sample(mc, out pose);
			if (!pending && mc.InWorld) { lastX = mc.X; lastY = mc.Y; lastZ = mc.Z; }

			if (board && TryBoard()) return;
			if (!pending && mc.InWorld) { CheckTether(); CheckSite(); }

			bool playerDied = false;
			Vessel puppet = anchorVessel != null && anchorVessel.isEVA ? anchorVessel : null;   // a kerbal that follows Steve is not an obstacle
			mirror.Publish(frame, lastX, lastY, lastZ, puppet, dt);
			mirror.HandleEvents(frame, eyeWorld, ref playerDied);
			if (playerDied) { SetTeleport(spawnX, spawnY, spawnZ, yaw, pitch); }

			coll.Tick(lastX, lastZ, dt, puppet);
			coll.WriteWater(lastX, lastZ, site.Id, dt);
			if (!pending && mc.InWorld && !mc.Dead) hazards.Update(frame, (Vector3)frame.McToWorld(lastX, lastY, lastZ), dt, puppet);

			WriteHostState(ow, oh, pending);

			overlay.Update();
			world.Drain();
			world.Place(frame);
			PlaceCamera(pose, ok);
		}

		void WriteHostState(int ow, int oh, bool pending)
		{
			hs.Flags = (uint)Proto.HOST_IN_GAME;
			hs.WorldId = site.Id;
			hs.CollisionEpoch = epoch;
			hs.X = pending ? hs.X : lastX; hs.Y = pending ? hs.Y : lastY; hs.Z = pending ? hs.Z : lastZ;
			hs.Yaw = yaw; hs.Pitch = pitch;
			hs.ViewportW = (uint)ow; hs.ViewportH = (uint)oh;
			hs.GameHour = 12f;
			hs.Gravity = (float)(body.GeeASL * 9.80665);
			hs.BodyFlags = (uint)((body.atmosphere ? Proto.BODY_ATMOSPHERE : 0) | (body.ocean ? Proto.BODY_OCEAN : 0));
			Link.WriteHostState(hs);
		}

		void SetTeleport(double x, double y, double z, float newYaw, float newPitch)
		{
			hs.X = x; hs.Y = y; hs.Z = z; yaw = newYaw; pitch = newPitch;
			hs.TeleportSeq++;
			interp.Reset();
		}

		/// <summary>Steve may wander only as far as KSP keeps the physics and terrain around the vessel loaded.</summary>
		void CheckTether()
		{
			if (anchorVessel == null || anchorVessel.isEVA) return;
			Vector3d p = frame.McToWorld(lastX, lastY, lastZ);
			double d = (p - anchorVessel.GetWorldPos3D()).magnitude;
			if (d <= Settings.TetherMeters) return;
			if (Time.unscaledTime > noticeTimer) { Say("Too far from your vessel: KSP only keeps the terrain loaded around it."); noticeTimer = Time.unscaledTime + 8f; }
			Vector3d back = anchorVessel.GetWorldPos3D() + (p - anchorVessel.GetWorldPos3D()).normalized * (Settings.TetherMeters - 20.0);
			double x, y, z; frame.WorldToMc(back, out x, out y, out z);
			double g = coll.Sampler.Compute(x - frame.SlotX, z - frame.SlotZ);
			SetTeleport(x, g + 0.05, z, yaw + 180f, pitch);
		}

		/// <summary>Beyond the site's radius, continue in a new site: the old patch stays where it is, with its blocks.</summary>
		void CheckSite()
		{
			double lx = lastX - frame.SlotX, lz = lastZ - frame.SlotZ;
			if (lx * lx + lz * lz < Settings.SiteRadius * Settings.SiteRadius) return;
			Vector3d here = frame.McToWorld(lastX, lastY, lastZ);
			// look direction in the world, to keep facing the same way in the new frame
			double fx, fy, fz;
			SiteFrame.LookDirection(yaw, pitch, out fx, out fy, out fz);
			Vector3d look = frame.McDirToWorld(fx, fy, fz);
			Site next = Sites.Find(body, here, Settings.SiteRadius * 0.5) ?? Sites.Create(body, here);
			if (next == site) return;
			site = next;
			frame = new SiteFrame(site, body);
			epoch++;
			coll.Reset(frame, epoch);
			mirror.Clear();
			double x, y, z; frame.WorldToMc(here, out x, out y, out z);
			double ground = coll.Sampler.Compute(x - frame.SlotX, z - frame.SlotZ);
			double lxn = Vector3d.Dot(look, frame.XDir), lyn = Vector3d.Dot(look, frame.Up), lzn = Vector3d.Dot(look, frame.South);
			float newYaw = (float)(Math.Atan2(-lxn, lzn) * 180.0 / Math.PI);
			float newPitch = (float)(-Math.Asin(Math.Max(-1.0, Math.Min(1.0, lyn))) * 180.0 / Math.PI);
			spawnX = x; spawnY = Math.Max(y, ground) + 0.05; spawnZ = z;
			SetTeleport(x, Math.Max(y, ground + 0.05), z, newYaw, newPitch);
			Log.Info("moved on to site {0}", site.Id);
		}

		/// <summary>The "enter" key: look at a vessel and climb aboard (it becomes the active vessel).</summary>
		bool TryBoard()
		{
			RaycastHit hit;
			Vector3 fwd = cam.Rotation * Vector3.forward;
			if (!Physics.Raycast(eyeWorld, fwd, out hit, 8f, ~0, QueryTriggerInteraction.Ignore)) return false;
			Part p = hit.collider.GetComponentInParent<Part>();
			if (p == null || p.vessel == null) return false;
			Vessel target = p.vessel;
			Exit(false);
			if (target != FlightGlobals.ActiveVessel) FlightGlobals.ForceSetActiveVessel(target);
			Say("Back aboard " + target.vesselName + ".");
			return true;
		}

		// ---- camera -----------------------------------------------------------------------------

		static bool IsFinite(Quaternion q) { return !float.IsNaN(q.x + q.y + q.z + q.w); }

		void PlaceCamera(TickInterpolator.Pose pose, bool ok)
		{
			bool pending = mc.TeleportAck != hs.TeleportSeq || !ok;
			double ex, ey, ez;
			if (pending) { ex = hs.X; ey = hs.Y + 1.62; ez = hs.Z; pose.FeetX = hs.X; pose.FeetY = hs.Y; pose.FeetZ = hs.Z; pose.BobAmount = 0f; }
			else { ex = pose.EyeX; ey = pose.EyeY; ez = pose.EyeZ; }

			float camYaw = yaw, camPitch = pitch, roll = 0f;
			// head bob, as Minecraft does it
			if (!pending && mc.CameraMode == 0)
			{
				float phase = pose.BobPhase * Mathf.PI, bob = pose.BobAmount;
				float side = -Mathf.Sin(phase) * bob * 0.5f, lift = Mathf.Abs(Mathf.Cos(phase) * bob);
				double ry = yaw * Math.PI / 180.0;
				ex += -Math.Cos(ry) * side; ez += -Math.Sin(ry) * side; ey += lift;
				camPitch += Mathf.Abs(Mathf.Cos(phase - 0.2f) * bob) * 5f;
				roll = Mathf.Sin(phase) * bob * 3f;
			}
			// third person: 1 = behind, 2 = in front looking back
			if (mc.CameraMode != 0)
			{
				float back = mc.CameraDistance > 0.5f ? mc.CameraDistance : 4f;
				if (mc.CameraMode == 2) { camYaw += 180f; camPitch = -camPitch; }
				double fx, fy, fz;
				SiteFrame.LookDirection(camYaw, camPitch, out fx, out fy, out fz);
				ex -= fx * back; ey -= fy * back; ez -= fz * back;
			}

			camPitch = Mathf.Clamp(camPitch, -PitchLimit, PitchLimit);

			double lx, ly, lz;
			SiteFrame.LookDirection(camYaw, camPitch, out lx, out ly, out lz);
			Vector3d fwd = frame.McDirToWorld(lx, ly, lz).normalized;
			Quaternion rot = Quaternion.LookRotation((Vector3)fwd, (Vector3)frame.Up);
			if (!IsFinite(rot)) rot = lastRotation;   // never hand KSP a broken camera
			lastRotation = rot;
			if (roll != 0f) rot = Quaternion.AngleAxis(roll, (Vector3)fwd) * rot;

			eyeWorld = (Vector3)frame.McToWorld(ex, ey, ez);
			cam.EyeWorld = eyeWorld; cam.Rotation = rot; cam.Fov = mc.Fov > 1f ? Mathf.Clamp(mc.Fov, 30f, 110f) : 70f;
			cam.Active = true;

			// KSP's own flight camera should stay near us so terrain detail, shadows and culling follow
			if (FlightCamera.fetch != null) FlightCamera.fetch.transform.position = eyeWorld;

			world.Frame(world.ToLocal(ex, ey, ez), pose.FeetX, pose.FeetY, pose.FeetZ, mc.CameraMode != 0, mc.Dead);
		}
	}
}
