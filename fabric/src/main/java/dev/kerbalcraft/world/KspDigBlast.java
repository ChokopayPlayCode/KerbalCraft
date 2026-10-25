package dev.kerbalcraft.world;

import dev.kerbalcraft.link.KspLink;
import it.unimi.dsi.fastutil.longs.Long2IntOpenHashMap;
import it.unimi.dsi.fastutil.longs.LongOpenHashSet;
import java.util.ArrayList;
import java.util.List;
import java.util.Optional;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.level.ServerExplosion;
import net.minecraft.world.phys.Vec3;
import org.jspecify.annotations.Nullable;

/**
 * Minecraft explosions (TNT, creepers, fireballs, beds...) blowing up KSP's ground and rock as if
 * they were blocks of what they're made of: the blast is soaked up by them like by those blocks
 * (dirt gives way, stone holds more, buildings stop it), and what it reaches becomes that block for
 * the explosion to break the normal way (drops and all), dug out of KSP. The crater is then
 * lined like a dug hole (KspDig).
 *
 * Needs KSP's geometry around the blast, which only the host's Minecraft has (the integrated
 * server shares its KspCollision): a guest's explosions far from the host leave KSP alone.
 */
public final class KspDigBlast {
	/** Blast resistance of what can't be dug (buildings): stops a blast like obsidian. */
	private static final float BUILDING_RESISTANCE = 1200.0F;
	private static final double[] SAMPLE = { 0.2, 0.5, 0.8 };
	private static final KspLink.HostState HOST = new KspLink.HostState();

	private final ServerLevel level;
	private final int world;
	private final KspDig.Probe probe;
	private final Long2IntOpenHashMap cells = new Long2IntOpenHashMap(); // cell -> material, 0 none, KEEP
	private final List<BlockPos> opened = new ArrayList<>();

	private KspDigBlast(ServerLevel level, int world, Vec3 center, float radius) {
		this.level = level;
		this.world = world;
		double reach = radius * 1.4 + 2.0; // rays go up to 1.3 x radius
		this.probe = new KspDig.Probe().around(center.x - reach, center.y - reach, center.z - reach, center.x + reach, center.y + reach, center.z + reach);
		this.cells.defaultReturnValue(Integer.MIN_VALUE);
	}

	/** For an explosion about to go off: null unless KSP's geometry around it is known. */
	public static @Nullable KspDigBlast begin(ServerExplosion explosion) {
		Vec3 c = explosion.center();
		if (!KspDig.destruction || !KspCollision.active() || !KspCollision.isKnown((int) Math.floor(c.x), (int) Math.floor(c.y), (int) Math.floor(c.z)) || !KspLink.readHostState(HOST)) {
			return null;
		}
		return new KspDigBlast(explosion.level(), HOST.worldId, c, explosion.radius());
	}

	/**
	 * What KSP geometry fills this cell as far as a blast cares: a Proto.DIG_* material if any of
	 * it is diggable ground or rock (even a little: the surface layer goes too), KEEP for a building,
	 * 0 for nothing (or already dug, or a Minecraft block there, which the blast handles itself).
	 */
	private int material(BlockPos pos) {
		long key = pos.asLong();
		int known = this.cells.get(key);
		if (known != Integer.MIN_VALUE) {
			return known;
		}
		int result = 0;
		if (this.level.getBlockState(pos).isAir() && !KspDig.isDug(this.level, this.world, pos)) {
			for (double sy : SAMPLE) {
				for (double sz : SAMPLE) {
					for (double sx : SAMPLE) {
						int r = this.probe.test(pos.getX() + sx, pos.getY() + sy, pos.getZ() + sz);
						if (r == KspDig.KEEP) {
							result = KspDig.KEEP;
							break;
						}
						if (r > KspDig.AIR && result == 0) {
							result = KspDig.underground(r, Math.max(this.probe.depth, 0.5));
						}
					}
				}
			}
		}
		this.cells.put(key, result);
		return result;
	}

	/** The explosion's ray reached this cell: how much KSP geometry there soaks up. */
	public Optional<Float> resistance(BlockPos pos, Optional<Float> vanilla) {
		if (vanilla.isPresent()) {
			return vanilla;
		}
		int m = material(pos);
		if (m == KspDig.KEEP) {
			return Optional.of(BUILDING_RESISTANCE);
		}
		return m > 0 ? Optional.of(KspDig.materialState(m).getBlock().getExplosionResistance()) : vanilla;
	}

	/**
	 * The cells the explosion will break: KSP geometry in them becomes the block it's made of
	 * (dug out of KSP), so the explosion breaks it like any block.
	 */
	public void materialize(List<BlockPos> targets, boolean breaksBlocks) {
		if (!breaksBlocks) {
			return;
		}
		for (BlockPos pos : targets) {
			int m = material(pos);
			if (m <= 0) {
				continue;
			}
			KspDig.markDug(this.level, this.world, pos);
			this.level.setBlock(pos, KspDig.blockFor(this.level, pos, m), 2 | 16);
			this.opened.add(pos.immutable());
		}
	}

	/** After the explosion: the cells around the crater that are inside KSP's geometry. */
	public void finish() {
		if (this.opened.isEmpty()) {
			return;
		}
		LongOpenHashSet done = new LongOpenHashSet();
		for (BlockPos pos : this.opened) {
			done.add(pos.asLong());
		}
		for (BlockPos pos : this.opened) {
			for (Direction d : Direction.values()) {
				BlockPos n = pos.relative(d);
				if (!done.add(n.asLong()) || KspDig.isDug(this.level, this.world, n)) {
					continue;
				}
				int material = KspDig.classify(n.getX(), n.getY(), n.getZ());
				if (material > KspDig.AIR) {
					KspDig.digCell(this.level, this.world, n, material);
				}
			}
		}
	}
}
