using System;
using UnityEngine;

namespace KerbalCraft
{
	/// <summary>
	/// Maps between Minecraft coordinates (blocks, +Y up, +Z south, a site's slot added to X and Z)
	/// and KSP's Unity world around one site. One block is one metre. The patch is flat: the
	/// tangent plane at the site's anchor, so on a small body far from the anchor the ground
	/// curves away below it (and Steve simply walks downhill).
	/// </summary>
	public class SiteFrame
	{
		public Site Site;
		public CelestialBody Body;
		public Vector3d Origin;            // KSP world position of the anchor
		public Vector3d Up, South, XDir;   // unit vectors; XDir = South x Up so that Minecraft stays right-handed
		public double SlotX, SlotZ;
		public double R0;                  // anchor's distance from the body's centre

		public SiteFrame(Site site, CelestialBody body)
		{
			Site = site; Body = body;
			Sites.SlotOrigin(site.SlotIndex, out SlotX, out SlotZ);
			Update();
		}

		/// <summary>Recompute from the body (it rotates, and KSP's floating origin shifts the world).</summary>
		public void Update()
		{
			Origin = Body.GetWorldSurfacePosition(Site.Lat, Site.Lon, Site.Alt);
			Vector3d fromCentre = Origin - Body.position;
			R0 = fromCentre.magnitude;
			Up = fromCentre / R0;
			double step = 10.0 / Body.Radius * (180.0 / Math.PI);
			bool nearPole = Site.Lat > 89.0;
			Vector3d p = Body.GetWorldSurfacePosition(nearPole ? Site.Lat - step : Site.Lat + step, Site.Lon, Site.Alt) - Origin;
			p -= Up * Vector3d.Dot(p, Up);
			Vector3d north = p.normalized;
			if (nearPole) north = -north;
			South = -north;
			XDir = Vector3d.Cross(South, Up).normalized;
		}

		public Vector3d McToWorld(double x, double y, double z)
		{
			return Origin + XDir * (x - SlotX) + Up * y + South * (z - SlotZ);
		}

		public void WorldToMc(Vector3d w, out double x, out double y, out double z)
		{
			Vector3d d = w - Origin;
			x = Vector3d.Dot(d, XDir) + SlotX;
			y = Vector3d.Dot(d, Up);
			z = Vector3d.Dot(d, South) + SlotZ;
		}

		/// <summary>A direction in Minecraft axes as a KSP world direction.</summary>
		public Vector3d McDirToWorld(double x, double y, double z) { return XDir * x + Up * y + South * z; }

		/// <summary>Minecraft's look direction (yaw 0 faces +Z, positive yaw turns right; pitch positive looks down).</summary>
		public static void LookDirection(float yawDeg, float pitchDeg, out double x, out double y, out double z)
		{
			double yaw = yawDeg * Math.PI / 180.0, pitch = pitchDeg * Math.PI / 180.0;
			double cp = Math.Cos(pitch);
			x = -Math.Sin(yaw) * cp; y = -Math.Sin(pitch); z = Math.Cos(yaw) * cp;
		}

		/// <summary>Rotation of the render root: local Minecraft axes -> world. Needs localScale (-1, 1, 1) to mirror into Unity's left-handed world.</summary>
		public Quaternion RootRotation { get { return Quaternion.LookRotation((Vector3)South, (Vector3)Up); } }
		public static readonly Vector3 RootScale = new Vector3(-1f, 1f, 1f);

		/// <summary>Position of the render root. Its local origin is Minecraft (SlotX, 0, SlotZ).</summary>
		public Vector3 RootPosition { get { return (Vector3)Origin; } }
	}
}
