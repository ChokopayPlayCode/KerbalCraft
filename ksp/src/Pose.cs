using System;
using System.Collections.Generic;
using UnityEngine;

namespace KerbalCraft
{
	/// <summary>
	/// Minecraft ticks at 20 Hz; the camera renders at KSP's frame rate. Each tick Minecraft
	/// publishes the previous and current position with the QPC time the tick started; we replay
	/// those a fixed delay in the past and interpolate between them, like Minecraft's own partial ticks.
	/// </summary>
	public class TickInterpolator
	{
		struct Tick { public long At; public double Px, Py, Pz, Cx, Cy, Cz; public float EyeO, Eye, WalkO, Walk, BobO, Bob; }
		readonly List<Tick> hist = new List<Tick>();
		long lastTickQpc;
		public double PeriodMs = 50;

		public struct Pose
		{
			public double FeetX, FeetY, FeetZ, EyeX, EyeY, EyeZ;
			public float BobPhase, BobAmount;
		}

		public void Reset() { hist.Clear(); lastTickQpc = 0; }

		public void Feed(Link.McState m)
		{
			if (m.TickQpc == 0 || m.TickQpc == lastTickQpc) return;
			lastTickQpc = m.TickQpc;
			if (m.TickMs > 5f) PeriodMs = m.TickMs;
			hist.Add(new Tick { At = m.TickQpc, Px = m.PrevX, Py = m.PrevY, Pz = m.PrevZ, Cx = m.CurX, Cy = m.CurY, Cz = m.CurZ,
				EyeO = m.TickEyeO, Eye = m.TickEye, WalkO = m.WalkDistO, Walk = m.WalkDist, BobO = m.BobO, Bob = m.Bob });
			if (hist.Count > 8) hist.RemoveAt(0);
		}

		public bool Sample(Link.McState m, out Pose pose)
		{
			pose = new Pose();
			if (hist.Count == 0)
			{
				pose.FeetX = m.X; pose.FeetY = m.Y; pose.FeetZ = m.Z;
				pose.EyeX = m.EyeX; pose.EyeY = m.EyeY; pose.EyeZ = m.EyeZ;
				pose.BobPhase = m.BobPhase; pose.BobAmount = m.BobAmount;
				return m.InWorld;
			}
			long freq = Link.QpcFrequency();
			long render = Link.Qpc() - (long)(Settings.InterpDelayMs * 0.001 * freq);
			long periodTicks = (long)(PeriodMs * 0.001 * freq);
			int i = hist.Count - 1;
			while (i > 0 && hist[i].At > render) i--;
			Tick t = hist[i];
			double f = periodTicks > 0 ? (double)(render - t.At) / periodTicks : 1.0;
			f = Math.Max(0.0, Math.Min(1.0, f));
			pose.FeetX = t.Px + (t.Cx - t.Px) * f; pose.FeetY = t.Py + (t.Cy - t.Py) * f; pose.FeetZ = t.Pz + (t.Cz - t.Pz) * f;
			float ef = t.EyeO + (t.Eye - t.EyeO) * (float)f;
			pose.EyeX = pose.FeetX; pose.EyeY = pose.FeetY + ef; pose.EyeZ = pose.FeetZ;
			pose.BobPhase = -(t.WalkO + (t.Walk - t.WalkO) * (float)f);
			pose.BobAmount = t.BobO + (t.Bob - t.BobO) * (float)f;
			return true;
		}
	}

	/// <summary>Takes over KSP's cameras for the Steve view.</summary>
	public class CameraDriver
	{
		public bool Active;
		public Vector3 EyeWorld;
		public Quaternion Rotation = Quaternion.identity;
		public float Fov = 70f;
		bool hooked;

		readonly Dictionary<Camera, float> savedFov = new Dictionary<Camera, float>();

		// KSP's own local pose of every transform we move, taken before Steve mode first touches it.
		// KSP's cameras are children that it doesn't rewrite every frame, so without this they would
		// keep Steve's tilt after we hand the view back.
		struct LocalPose { public Vector3 Pos; public Quaternion Rot; }
		readonly Dictionary<Transform, LocalPose> kspPose = new Dictionary<Transform, LocalPose>();

		public void Hook() { if (!hooked) { Camera.onPreCull += OnPreCull; hooked = true; } }

		/// <summary>Remember KSP's local pose of a transform (once per session), before we move it.</summary>
		public void Track(Transform t)
		{
			if (t == null || kspPose.ContainsKey(t)) return;
			kspPose[t] = new LocalPose { Pos = t.localPosition, Rot = t.localRotation };
		}

		public void Unhook()
		{
			if (hooked) { Camera.onPreCull -= OnPreCull; hooked = false; }
			Active = false;
			foreach (var kv in savedFov) if (kv.Key != null) kv.Key.fieldOfView = kv.Value;   // вернуть FOV KSP
			savedFov.Clear();
			foreach (var kv in kspPose)
			{
				if (kv.Key == null) continue;
				kv.Key.localPosition = kv.Value.Pos;   // KSP's camera back exactly as it was
				kv.Key.localRotation = kv.Value.Rot;
			}
			kspPose.Clear();
		}

		/// <summary>
		/// Runs right before each camera culls, i.e. after every LateUpdate: KSP's FlightCamera,
		/// scaled-space and sky cameras have already been placed, and we overwrite them.
		/// </summary>
		void OnPreCull(Camera c)
		{
			if (!Active || c == null) return;
			if (float.IsNaN(EyeWorld.x + EyeWorld.y + EyeWorld.z + Rotation.x + Rotation.y + Rotation.z + Rotation.w)) return;   // keep KSP's own camera this frame
			string n = c.name;
			bool flight = n == "Camera 00" || n == "Camera 01" || n.StartsWith("Camera VE");
			bool scaled = n == "Camera ScaledSpace";
			bool galaxy = n.Contains("Galaxy");
			if (!flight && !scaled && !galaxy) return;
			Track(c.transform);
			if (!savedFov.ContainsKey(c)) savedFov[c] = c.fieldOfView;   // запомнить FOV KSP до подмены
			if (flight)
			{
				c.transform.position = EyeWorld; c.transform.rotation = Rotation; c.fieldOfView = Fov;
			}
			else if (scaled)
			{
				c.transform.position = ScaledSpace.LocalToScaledSpace((Vector3d)EyeWorld); c.transform.rotation = Rotation; c.fieldOfView = Fov;
			}
			else
			{
				c.transform.rotation = Rotation; c.fieldOfView = Fov;
			}
		}
	}
}
