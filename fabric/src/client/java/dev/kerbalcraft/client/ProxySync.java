package dev.kerbalcraft.client;

import dev.kerbalcraft.combat.VesselProxyEntity;
import dev.kerbalcraft.link.KspLink;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import net.minecraft.client.Minecraft;
import net.minecraft.world.entity.Entity;

/**
 * Puts the client's copies of the KSP actor stand-ins exactly where KSP has the actors this
 * frame, so the crosshair and melee reach line up with what's on screen (the server copy only
 * moves once per tick and reaches the client a tick or two later).
 */
final class ProxySync {
	private static final List<KspLink.Actor> ACTORS = new ArrayList<>();
	private static final Map<Integer, KspLink.Actor> BY_ID = new HashMap<>();

	private ProxySync() {
	}

	static void frame(Minecraft minecraft) {
		if (minecraft.level == null || !KspLink.readActors(ACTORS)) {
			return;
		}
		BY_ID.clear();
		for (KspLink.Actor a : ACTORS) {
			BY_ID.put(a.actorId(), a);
		}
		for (Entity entity : minecraft.level.entitiesForRendering()) {
			if (entity instanceof VesselProxyEntity proxy) {
				KspLink.Actor a = BY_ID.get(proxy.actorId());
				if (a == null) {
					continue;
				}
				proxy.setSize(a.width(), a.height());
				proxy.setPos(a.x(), a.y(), a.z());
				proxy.xo = a.x();
				proxy.yo = a.y();
				proxy.zo = a.z();
				proxy.setYRot(a.yaw());
				proxy.yRotO = a.yaw();
			}
		}
	}
}
