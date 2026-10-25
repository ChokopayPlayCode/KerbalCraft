package dev.kerbalcraft.client.mixin;

import com.mojang.blaze3d.platform.Window;
import dev.kerbalcraft.client.KspClient;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** The MC window is hidden while linked; KSP has the real focus, so pretend we do too. */
@Mixin(Window.class)
public abstract class WindowMixin {
	@Inject(method = "isFocused", at = @At("HEAD"), cancellable = true)
	private void kerbalcraft$focused(CallbackInfoReturnable<Boolean> cir) {
		if (KspClient.tookOver()) {
			// Focused while KSP is connected; if KSP goes away, act unfocused so MC
			// never tries to grab the (hidden) mouse.
			cir.setReturnValue(KspClient.linked());
		}
	}

	@Inject(method = "isIconified", at = @At("HEAD"), cancellable = true)
	private void kerbalcraft$notIconified(CallbackInfoReturnable<Boolean> cir) {
		if (KspClient.linked()) {
			cir.setReturnValue(false);
		}
	}
}
