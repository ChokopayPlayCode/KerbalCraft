package dev.kerbalcraft.client.mixin;

import dev.kerbalcraft.client.KspClient;
import net.minecraft.client.renderer.LevelRenderer;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * KSP draws the world. While linked, Minecraft renders nothing of its own level (no sky,
 * clouds, fog or terrain) so the overlay is just hand + HUD on a transparent background.
 */
@Mixin(LevelRenderer.class)
public abstract class LevelRendererMixin {
	@Inject(
		method = "render(Lcom/mojang/blaze3d/resource/GraphicsResourceAllocator;ZLnet/minecraft/client/renderer/state/level/CameraRenderState;Lcom/mojang/renderpearl/api/buffers/GpuBufferSlice;Lorg/joml/Vector4f;ZZ)V",
		at = @At("HEAD"),
		cancellable = true
	)
	private void kerbalcraft$skipLevel(CallbackInfo ci) {
		if (KspClient.linked()) {
			ci.cancel();
		}
	}
}
