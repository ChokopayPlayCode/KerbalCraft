package dev.kerbalcraft.mixin;

import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import com.llamalad7.mixinextras.injector.wrapoperation.WrapOperation;
import dev.kerbalcraft.world.KspWater;
import net.minecraft.core.BlockPos;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.material.FluidState;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;

/** Sprint-swimming starts in KSP's water too (see {@link KspWater}). */
@Mixin(Entity.class)
public abstract class EntitySwimMixin {
	@WrapOperation(
		method = "updateSwimming",
		at = @At(value = "INVOKE", target = "Lnet/minecraft/world/level/Level;getFluidState(Lnet/minecraft/core/BlockPos;)Lnet/minecraft/world/level/material/FluidState;")
	)
	private FluidState kerbalcraft$swimInKSPWater(Level level, BlockPos pos, Operation<FluidState> original) {
		FluidState state = original.call(level, pos);
		if (state.isEmpty() && KspWater.active()) {
			FluidState water = KspWater.fluidAt(level, pos);
			if (water != null) {
				return water;
			}
		}
		return state;
	}
}
