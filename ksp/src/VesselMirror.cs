using System;
using System.Collections.Generic;
using UnityEngine;

namespace KerbalCraft
{
	/// <summary>
	/// Every vessel part near Steve gets a stand-in entity in Minecraft (a hittable box of the
	/// part's size). Minecraft reports hits and explosions back, and they become real impulses on
	/// the part's rigidbody: kicking a rocket tips it over, TNT sends it flying.
	/// </summary>
	public class VesselMirror
	{
		struct Candidate { public uint Id; public float Dist; public Link.Actor Actor; public Part Part; }

		readonly Dictionary<uint, Part> byId = new Dictionary<uint, Part>();
		readonly Dictionary<Part, Collider[]> colliders = new Dictionary<Part, Collider[]>();
		readonly List<Candidate> list = new List<Candidate>();
		readonly Link.Actor[] buffer = new Link.Actor[Proto.MAX_ACTORS];
		float timer;

		public void Clear() { byId.Clear(); colliders.Clear(); list.Clear(); Link.WriteActors(buffer, 0); }

		public void Publish(SiteFrame f, double mcX, double mcY, double mcZ, Vessel ignore, float dt)
		{
			timer -= dt;
			if (timer > 0f) return;
			timer = 0.05f;
			list.Clear(); byId.Clear();
			float radius = Settings.ActorRadius;
			foreach (Vessel v in FlightGlobals.VesselsLoaded)
			{
				if (v == null || v == ignore || v.packed || v.mainBody != f.Body) continue;
				for (int i = 0; i < v.parts.Count; i++)
				{
					Part part = v.parts[i];
					if (part == null) continue;
					Collider[] cols;
					if (!colliders.TryGetValue(part, out cols) || cols == null) { cols = part.GetComponentsInChildren<Collider>(false); colliders[part] = cols; }
					double minX = 1e30, minY = 1e30, minZ = 1e30, maxX = -1e30, maxY = -1e30, maxZ = -1e30;
					bool any = false;
					for (int c = 0; c < cols.Length; c++)
					{
						Collider col = cols[c];
						if (col == null || col.isTrigger) continue;
						Bounds lb; Matrix4x4 m;
						MeshCollider mc = col as MeshCollider;
						BoxCollider bc = col as BoxCollider;
						if (mc != null && mc.sharedMesh != null) { lb = mc.sharedMesh.bounds; m = mc.transform.localToWorldMatrix; }
						else if (bc != null) { lb = new Bounds(bc.center, bc.size); m = bc.transform.localToWorldMatrix; }
						else { lb = col.bounds; m = Matrix4x4.identity; }
						Vector3 e = lb.extents, ce = lb.center;
						for (int k = 0; k < 8; k++)
						{
							Vector3 corner = m.MultiplyPoint3x4(ce + new Vector3((k & 1) == 0 ? -e.x : e.x, (k & 2) == 0 ? -e.y : e.y, (k & 4) == 0 ? -e.z : e.z));
							double x, y, z;
							f.WorldToMc((Vector3d)corner, out x, out y, out z);
							if (x < minX) minX = x; if (x > maxX) maxX = x;
							if (y < minY) minY = y; if (y > maxY) maxY = y;
							if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
							any = true;
						}
					}
					if (!any) continue;
					double cx = (minX + maxX) * 0.5, cz = (minZ + maxZ) * 0.5;
					double dist = Math.Sqrt((cx - mcX) * (cx - mcX) + (cz - mcZ) * (cz - mcZ) + ((minY + maxY) * 0.5 - mcY) * ((minY + maxY) * 0.5 - mcY));
					if (dist > radius + 20.0) continue;
					uint baseId = (uint)(part.flightID * 2654435761UL ^ (ulong)v.persistentId * 40503UL) & ~7u;
					if (baseId == 0) baseId = 8;
					// long parts (a lying tank, a wing): several boxes along the long horizontal axis
					double sx = maxX - minX, sz = maxZ - minZ;
					int pieces = 1; bool alongX = sx >= sz;
					double longSide = Math.Max(sx, sz), shortSide = Math.Max(Math.Min(sx, sz), 0.3);
					if (longSide > shortSide * 1.8) pieces = (int)Math.Min(7, Math.Ceiling(longSide / shortSide));
					string name = part.partInfo != null ? part.partInfo.title : part.name;
					for (int p = 0; p < pieces; p++)
					{
						double px = cx, pz = cz, w;
						if (pieces == 1) w = Math.Max(sx, sz);
						else if (alongX) { w = longSide / pieces; px = minX + w * (p + 0.5); w = Math.Max(w, sz); }
						else { w = longSide / pieces; pz = minZ + w * (p + 0.5); w = Math.Max(w, sx); }
						Link.Actor a = new Link.Actor();
						a.Id = baseId | (uint)p; a.Flags = 0;
						a.X = (float)px; a.Y = (float)minY; a.Z = (float)pz;
						a.Yaw = 0f; a.Width = (float)Math.Max(w, 0.3); a.Height = (float)Math.Max(maxY - minY, 0.3);
						a.HealthFrac = 1f; a.Level = 1; a.Name = name;
						list.Add(new Candidate { Id = a.Id, Dist = (float)dist, Actor = a, Part = part });
					}
				}
			}
			list.Sort((x, y) => x.Dist.CompareTo(y.Dist));
			int n = Math.Min(list.Count, Proto.MAX_ACTORS);
			for (int i = 0; i < n; i++) { buffer[i] = list[i].Actor; byId[list[i].Id] = list[i].Part; }
			Link.WriteActors(buffer, n);
		}

		/// <summary>Drains Minecraft's events: hits on stand-ins and explosions become impulses.</summary>
		public void HandleEvents(SiteFrame f, Vector3 eyeWorld, ref bool playerDied)
		{
			Link.McEvent e;
			while (Link.PopEvent(out e))
			{
				switch ((int)e.Type)
				{
					case Proto.EV_HIT_ACTOR: Hit(e, eyeWorld); break;
					case Proto.EV_EXPLOSION: Explode(f, e); break;
					case Proto.EV_PLAYER_DIED: playerDied = true; break;
					default: break;
				}
			}
		}

		void Hit(Link.McEvent e, Vector3 eye)
		{
			Part part;
			if (!byId.TryGetValue(e.ActorId, out part) || part == null) return;
			Rigidbody rb = part.Rigidbody;
			if (rb == null && part.vessel != null && part.vessel.rootPart != null) rb = part.vessel.rootPart.Rigidbody;
			if (rb == null) { Log.Warn("hit {0}, but it has no rigidbody (vessel on rails?)", part.name); return; }
			float damage = e.A, push = e.D;
			float impulse = Settings.KickImpulse * (Math.Max(damage, 0f) + 2f * Math.Max(push, 0f));
			if (impulse <= 0f) return;
			Vector3 contact = ClosestPoint(part, eye);
			Vector3 dir = (contact - eye);
			if (dir.sqrMagnitude < 1e-6f) return;
			dir.Normalize();
			// a kick pops slightly upward so that heavy things don't just scrape along the ground
			Vector3 up = (eye - (Vector3)FlightGlobals.currentMainBody.position).normalized;
			dir = (dir + up * 0.15f).normalized;
			rb.AddForceAtPosition(dir * impulse, contact, ForceMode.Impulse);
			if (Settings.Diagnostics) Log.Info("kick {0}: {1:F1} kN*s", part.name, impulse);
		}

		static Vector3 ClosestPoint(Part part, Vector3 from)
		{
			Collider[] cols = part.GetComponentsInChildren<Collider>(false);
			float best = float.MaxValue; Vector3 pt = part.transform.position;
			for (int i = 0; i < cols.Length; i++)
			{
				if (cols[i] == null || cols[i].isTrigger) continue;
				Vector3 c;
				MeshCollider mc = cols[i] as MeshCollider;
				if (mc != null && !mc.convex) c = cols[i].bounds.ClosestPoint(from); else c = cols[i].ClosestPoint(from);
				float d = (c - from).sqrMagnitude;
				if (d < best) { best = d; pt = c; }
			}
			return pt;
		}

		void Explode(SiteFrame f, Link.McEvent e)
		{
			float radius = Math.Max(e.D, 1f);
			Vector3 centre = (Vector3)f.McToWorld(e.A, e.B, e.C);
			float reach = radius * 3f;
			float power = Settings.ExplosionImpulse * (radius / 4f);
			foreach (Vessel v in FlightGlobals.VesselsLoaded)
			{
				if (v == null || v.packed) continue;
				for (int i = 0; i < v.parts.Count; i++)
				{
					Part p = v.parts[i];
					if (p == null || p.Rigidbody == null) continue;
					if ((p.transform.position - centre).sqrMagnitude > reach * reach) continue;
					p.Rigidbody.AddExplosionForce(power, centre, reach, 0.3f, ForceMode.Impulse);
				}
			}
			if (Settings.Diagnostics) Log.Info("explosion radius {0:F1}", radius);
		}
	}
}
