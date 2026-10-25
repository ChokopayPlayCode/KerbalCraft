package dev.kerbalcraft.client.mixin;

import dev.kerbalcraft.client.KspClient;
import net.minecraft.client.Minecraft;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

@Mixin(Minecraft.class)
public abstract class MinecraftMixin {
	@Inject(method = "runTick", at = @At("HEAD"))
	private void kerbalcraft$beginFrame(boolean advanceGameTime, CallbackInfo ci) {
		KspClient.beginFrame();
	}

	@Inject(
		method = "renderFrame",
		at = @At(value = "INVOKE", target = "Lnet/minecraft/client/renderer/GameRenderer;render()V", shift = At.Shift.AFTER)
	)
	private void kerbalcraft$afterRender(boolean advanceGameTime, CallbackInfo ci) {
		KspClient.afterRender();
	}

	@Inject(method = "renderFrame", at = @At("TAIL"))
	private void kerbalcraft$pace(boolean advanceGameTime, CallbackInfo ci) {
		KspClient.paceFrame();
	}
}
