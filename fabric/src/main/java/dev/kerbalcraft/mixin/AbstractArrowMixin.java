package dev.kerbalcraft.mixin;

import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import com.llamalad7.mixinextras.injector.wrapoperation.WrapOperation;
import dev.kerbalcraft.combat.VesselProxyEntity;
import dev.kerbalcraft.link.Proto;
import dev.kerbalcraft.link.KspLink;
import dev.kerbalcraft.world.KspClip;
import net.minecraft.util.Mth;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.entity.projectile.arrow.AbstractArrow;
import net.minecraft.world.entity.projectile.arrow.Arrow;
import net.minecraft.world.entity.projectile.arrow.SpectralArrow;
import net.minecraft.world.phys.EntityHitResult;
import net.minecraft.world.phys.Vec3;
import org.spongepowered.asm.mixin.Unique;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;
import net.minecraft.world.level.ClipContext;
import net.minecraft.world.level.Level;
import net.minecraft.world.phys.BlockHitResult;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;

/**
 * Arrows and tridents hit KSP's exact surfaces. They then stick where they hit: the block state
 * there is air, the same as what they recorded on impact, so vanilla never makes them fall out.
 */
@Mixin(AbstractArrow.class)
public abstract class AbstractArrowMixin {
	@WrapOperation(
		method = "tick",
		at = @At(value = "INVOKE", target = "Lnet/minecraft/world/level/Level;clipIncludingBorder(Lnet/minecraft/world/level/ClipContext;)Lnet/minecraft/world/phys/BlockHitResult;")
	)
	private BlockHitResult kerbalcraft$hitKSP(Level level, ClipContext context, Operation<BlockHitResult> original) {
		return KspClip.refine(context.getFrom(), context.getTo(), original.call(level, context), KspClip.Use.PROJECTILE);
	}

	@Unique
	private Vec3 kerbalcraft$hitAt;

	@Inject(method = "onHitEntity", at = @At("HEAD"))
	private void kerbalcraft$rememberHit(EntityHitResult hitResult, CallbackInfo ci) {
		this.kerbalcraft$hitAt = hitResult.getLocation();
	}

	/**
	 * Where Minecraft counts an arrow as stuck in a creature (it hurt it and didn't pierce): if that
	 * creature is a vessel part's stand-in, the host pins the arrow to that part.
	 */
	@WrapOperation(method = "onHitEntity", at = @At(value = "INVOKE", target = "Lnet/minecraft/world/entity/LivingEntity;setArrowCount(I)V"))
	private void kerbalcraft$stickInKSPActor(LivingEntity mob, int count, Operation<Void> original) {
		original.call(mob, count);
		if (!(mob instanceof VesselProxyEntity actor) || this.kerbalcraft$hitAt == null || !KspLink.active()) {
			return;
		}
		AbstractArrow self = (AbstractArrow) (Object) this;
		Vec3 v = self.getDeltaMovement();
		float yaw = (float) (Mth.atan2(v.x, v.z) * Mth.RAD_TO_DEG);
		float pitch = (float) (Mth.atan2(v.y, v.horizontalDistance()) * Mth.RAD_TO_DEG);
		int texture = self instanceof SpectralArrow ? 2 : self instanceof Arrow tippable && tippable.getColor() > 0 ? 1 : 0;
		Vec3 at = this.kerbalcraft$hitAt;
		KspLink.pushEvent(Proto.EV_ARROW_STUCK, actor.actorId(), (float) at.x, (float) at.y, (float) at.z, yaw, Float.floatToRawIntBits(pitch), texture);
	}
}
