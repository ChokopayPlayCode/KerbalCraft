package dev.kerbalcraft.client.mixin;

import com.mojang.blaze3d.platform.InputConstants;
import com.mojang.blaze3d.platform.Window;
import dev.kerbalcraft.client.InputBridge;
import dev.kerbalcraft.client.KspClient;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** Keyboard state and mouse capture come from KSP while linked, not from SDL. */
@Mixin(InputConstants.class)
public abstract class InputConstantsMixin {
	@Inject(method = "isKeyDown", at = @At("HEAD"), cancellable = true)
	private static void kerbalcraft$isKeyDown(int key, CallbackInfoReturnable<Boolean> cir) {
		if (KspClient.tookOver()) {
			cir.setReturnValue(InputBridge.isKeyDown(key));
		}
	}

	@Inject(method = "grabMouse", at = @At("HEAD"), cancellable = true)
	private static void kerbalcraft$grabMouse(Window window, double xpos, double ypos, CallbackInfo ci) {
		if (KspClient.tookOver()) {
			ci.cancel();
		}
	}

	@Inject(method = "releaseMouse", at = @At("HEAD"), cancellable = true)
	private static void kerbalcraft$releaseMouse(Window window, double xpos, double ypos, CallbackInfo ci) {
		if (KspClient.tookOver()) {
			ci.cancel();
		}
	}
}
