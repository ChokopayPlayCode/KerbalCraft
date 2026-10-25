package dev.kerbalcraft.mixin;

import dev.kerbalcraft.KerbalCraft;
import dev.kerbalcraft.combat.VesselCombat;
import dev.kerbalcraft.combat.VesselProxyEntity;
import dev.kerbalcraft.link.Proto;
import dev.kerbalcraft.link.KspLink;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.entity.Entity;
import net.minecraft.server.level.ServerPlayer;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

@Mixin(ServerPlayer.class)
public abstract class ServerPlayerMixin {
	/** Critical hits on a KSP actor are flagged so KSP can play them up. */
	@Inject(method = "crit", at = @At("HEAD"))
	private void kerbalcraft$critKSP(Entity entity, CallbackInfo ci) {
		if (entity instanceof VesselProxyEntity proxy) {
			proxy.markCritical();
		}
	}

	/** Dying in Minecraft is dying in KSP: the host's through the link, a guest's through theirs. */
	@Inject(method = "die", at = @At("HEAD"))
	private void kerbalcraft$diesInKSP(DamageSource source, CallbackInfo ci) {
		ServerPlayer self = (ServerPlayer) (Object) this;
		int attacker = VesselCombat.attackerId(source);
		if (!dev.kerbalcraft.net.KspNet.isHost(self)) {
			if (net.fabricmc.fabric.api.networking.v1.ServerPlayNetworking.canSend(self, dev.kerbalcraft.net.KspNet.Died.TYPE)) {
				net.fabricmc.fabric.api.networking.v1.ServerPlayNetworking.send(self, new dev.kerbalcraft.net.KspNet.Died(attacker));
			}
			KerbalCraft.LOG.info("KerbalCraft: guest {} died ({}); telling their KSP", self.getPlainTextName(), source.getMsgId());
			return;
		}
		if (KspLink.active()) {
			KspLink.pushEvent(Proto.EV_PLAYER_DIED, attacker, 0, 0, 0, 0, 0);
			KerbalCraft.LOG.info("KerbalCraft: player died ({}); telling KSP", source.getMsgId());
		}
	}
}
