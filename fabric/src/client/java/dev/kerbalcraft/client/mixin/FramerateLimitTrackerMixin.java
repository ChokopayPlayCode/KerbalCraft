package dev.kerbalcraft.client.mixin;

import com.mojang.blaze3d.platform.FramerateLimitTracker;
import dev.kerbalcraft.client.KspClient;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** KspClient.paceFrame() locks us to KSP's frame rate; don't let MC throttle on its own. */
@Mixin(FramerateLimitTracker.class)
public abstract class FramerateLimitTrackerMixin {
	@Inject(method = "getFramerateLimit", at = @At("HEAD"), cancellable = true)
	private void kerbalcraft$unlimited(CallbackInfoReturnable<Integer> cir) {
		if (KspClient.linked()) {
			cir.setReturnValue(260);
		}
	}
}
