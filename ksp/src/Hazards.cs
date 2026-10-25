using System;
using UnityEngine;

namespace KerbalCraft
{
	/// <summary>
	/// KSP can hurt Steve: a part that hits him at speed, and the exhaust of a running engine.
	/// The damage is sent as Minecraft damage (IN_HURT), so armour, absorption and the hearts
	/// work as usual and dying respawns him at the site.
	/// </summary>
	public class Hazards
	{
		float timer;
		const float Step = 0.1f;
		const float HostToMc = 5f;   // same ratio as VesselCombat.HOST_TO_MC_DAMAGE on the Minecraft side

		/// <param name="feetWorld">Steve's feet in KSP world coordinates.</param>
		public void Update(SiteFrame f, Vector3 feetWorld, float dt, Vessel ignore)
		{
			timer -= dt;
			if (timer > 0f) return;
			timer = Step;
			Vector3 up = (Vector3)f.Up;
			Vector3 a = feetWorld + up * 0.4f, b = feetWorld + up * 1.4f;

			// 1. impacts: anything solid and fast enough that touches the capsule
			Collider[] hits = Physics.OverlapCapsule(a, b, 0.35f, ~0, QueryTriggerInteraction.Ignore);
			float worst = 0f; Part worstPart = null;
			for (int i = 0; i < hits.Length; i++)
			{
				Part p = hits[i].GetComponentInParent<Part>();
				if (p == null || p.vessel == null || p.vessel == ignore || p.vessel.packed) continue;
				float speed = (float)p.vessel.srfSpeed;
				Rigidbody rb = p.Rigidbody;
				if (rb != null) speed = Math.Max(speed, (rb.velocity - (Vector3)Krakensbane.GetFrameVelocity()).magnitude);
				if (speed < 2.5f) continue;
				float damage = (speed - 2f) * 0.6f * (0.5f + Mathf.Sqrt(Mathf.Max(p.mass + p.GetResourceMass(), 0.01f)));
				if (damage > worst) { worst = damage; worstPart = p; }
			}
			if (worst > 0f) Hurt(Mathf.Min(worst, 20f), Proto.HURT_OTHER);

			// 2. exhaust: inside the cone behind a firing engine
			foreach (Vessel v in FlightGlobals.VesselsLoaded)
			{
				if (v == null || v.packed || v == ignore || (feetWorld - v.transform.position).sqrMagnitude > 90f * 90f) continue;
				for (int i = 0; i < v.parts.Count; i++)
				{
					Part part = v.parts[i];
					for (int m = 0; m < part.Modules.Count; m++)
					{
						ModuleEngines e = part.Modules[m] as ModuleEngines;
						if (e == null || !e.isOperational || e.finalThrust < 5f) continue;
						for (int t = 0; t < e.thrustTransforms.Count; t++)
						{
							Transform tt = e.thrustTransforms[t];
							// the exhaust leaves along the thrust transform's forward axis (the ship is pushed along -forward)
							Vector3 to = feetWorld + up - tt.position;
							float along = Vector3.Dot(to, tt.forward);
							float reach = 3f + Mathf.Sqrt(e.finalThrust) * 0.8f;
							if (along < 0f || along > reach) continue;
							float radius = 0.8f + along * 0.25f;
							if ((to - tt.forward * along).magnitude > radius) continue;
							Hurt(Mathf.Min(1.5f + e.finalThrust / 400f, 8f), Proto.HURT_OTHER);
						}
					}
				}
			}
		}

		/// <summary>mcDamage is in Minecraft health points (hearts * 2).</summary>
		static void Hurt(float mcDamage, int kind)
		{
			int units = (int)(mcDamage * HostToMc * 100f);
			if (units > 0) Link.PushInput(Proto.IN_HURT, kind, units, 0, 0);
		}
	}
}
