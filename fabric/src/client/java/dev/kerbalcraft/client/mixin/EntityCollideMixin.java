package dev.kerbalcraft.client.mixin;

import dev.kerbalcraft.client.KspClient;
import dev.kerbalcraft.client.KspCollider;
import net.minecraft.client.player.LocalPlayer;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.phys.Vec3;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * After vanilla has collided the local player's movement with Minecraft blocks, collide it with
 * KSP's exact triangles (smooth slopes instead of voxel stair-steps).
 */
@Mixin(Entity.class)
public abstract class EntityCollideMixin {
	@Inject(method = "collide", at = @At("RETURN"), cancellable = true)
	private void kerbalcraft$smoothKSPCollision(Vec3 movement, CallbackInfoReturnable<Vec3> cir) {
		if ((Object) this instanceof LocalPlayer player && KspClient.linked() && !player.noPhysics) {
			cir.setReturnValue(KspCollider.collide(player, cir.getReturnValue()));
		}
	}
}
