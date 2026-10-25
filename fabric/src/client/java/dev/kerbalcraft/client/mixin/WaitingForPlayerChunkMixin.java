package dev.kerbalcraft.client.mixin;

import dev.kerbalcraft.client.KspClient;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * The "Loading terrain" screen waits for the player's chunk section to be compiled for
 * rendering. We never render Minecraft's level while linked, so don't wait for it.
 */
@Mixin(targets = "net.minecraft.client.multiplayer.LevelLoadTracker$WaitingForPlayerChunk")
public abstract class WaitingForPlayerChunkMixin {
	@Inject(method = "isReady", at = @At("HEAD"), cancellable = true)
	private void kerbalcraft$ready(CallbackInfoReturnable<Boolean> cir) {
		if (KspClient.linked()) {
			cir.setReturnValue(true);
		}
	}
}
