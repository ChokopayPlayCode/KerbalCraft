package dev.kerbalcraft.world;

import net.minecraft.world.entity.ai.attributes.AttributeInstance;
import net.minecraft.world.entity.ai.attributes.Attributes;
import net.minecraft.world.entity.player.Player;

/**
 * Scales Minecraft's player physics to the gravity of the celestial body KSP says the player
 * stands on. Minecraft's own movement code then does the rest: on the Mun (1.63 m/s^2) the same
 * jump carries a player about six times higher, on Minmus about twenty.
 */
public final class BodyPhysics {
	/** Gravity Minecraft's defaults correspond to (Kerbin's surface is 9.81 as well). */
	public static final double REFERENCE_G = 9.80665;
	private static final double DEFAULT_GRAVITY = 0.08;
	private static final double DEFAULT_SAFE_FALL = 3.0;

	private BodyPhysics() {
	}

	public static void apply(Player player, float gravityMs2) {
		if (player == null || !(gravityMs2 > 0.01F)) {
			return;
		}
		double ratio = Math.min(Math.max(gravityMs2 / REFERENCE_G, 0.02), 4.0);
		set(player.getAttribute(Attributes.GRAVITY), DEFAULT_GRAVITY * ratio);
		// Impact speed grows with g * height, so the same speed needs a longer fall in low gravity.
		set(player.getAttribute(Attributes.SAFE_FALL_DISTANCE), Math.min(DEFAULT_SAFE_FALL / ratio, 200.0));
		set(player.getAttribute(Attributes.FALL_DAMAGE_MULTIPLIER), Math.min(ratio, 1.0));
	}

	private static void set(AttributeInstance attribute, double value) {
		if (attribute != null && Math.abs(attribute.getBaseValue() - value) > 1.0E-6) {
			attribute.setBaseValue(value);
		}
	}
}
