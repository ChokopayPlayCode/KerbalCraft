package dev.kerbalcraft.client;

import dev.kerbalcraft.KerbalCraft;
import dev.kerbalcraft.client.render.WorldExporter;
import dev.kerbalcraft.link.Proto;
import dev.kerbalcraft.link.KspLink;
import dev.kerbalcraft.world.KspCollision;
import net.minecraft.client.Camera;
import net.minecraft.client.Minecraft;
import net.minecraft.client.player.LocalPlayer;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.phys.Vec3;
import org.lwjgl.sdl.SDLVideo;

/**
 * Per-frame glue between the Minecraft client and KSP. Everything here runs on the render
 * thread, called from MinecraftMixin.
 */
public final class KspClient {
	private static final boolean SHOW_WINDOW = Boolean.getBoolean("kerbalcraft.showWindow");
	// Started by KSP (KerbalCraft's bundled instance passes -Dkerbalcraft.startHidden=true): no window and
	// no title-screen music from the first frame, even while KSP is paused (Alt-Tabbed) and the
	// two haven't linked up yet. Otherwise the window only goes once KSP is there.
	private static final boolean START_HIDDEN = Boolean.getBoolean("kerbalcraft.startHidden");
	private static boolean startedHidden;

	private static final KspLink.HostState host = new KspLink.HostState();
	private static final KspLink.McState mc = new KspLink.McState();
	private static volatile boolean linked;
	private static boolean tookOver;
	private static boolean windowHidden;
	private static int appliedViewportW, appliedViewportH;

	// Teleport / hold state: KSP decides where the player is after loads, doors and respawns.
	private static int lastTeleportSeq = -1;
	private static int teleportAck;
	private static boolean teleportPending;
	private static LocalPlayer lastPlayer;
	private static Vec3 holdPos;
	private static Vec3 unlinkedHold;
	private static long holdSince;
	private static long qpcFreq;
	private static LocalPlayer eyePlayer;
	private static float eyeSmoothed;
	private static long frameCounter;
	private static int lastPacedSeq;
	private static boolean kspStalled;
	private static int exporterErrors;
	private static boolean steveOnLastFrame;

	private KspClient() {
	}

	public static boolean linked() {
		return linked;
	}

	/**
	 * True once KSP has connected in this session. From then on Minecraft never touches the
	 * real mouse or keyboard again (even if KSP closes), since its window is hidden.
	 */
	public static boolean tookOver() {
		return tookOver;
	}

	public static KspLink.HostState host() {
		return host;
	}

	/** Start of Minecraft.runTick: pull state and input from KSP before anything else runs. */
	public static void beginFrame() {
		KspLink.poll();
		quitWithKsp(Minecraft.getInstance());
		if (START_HIDDEN && !startedHidden) {
			startedHidden = true;
			Minecraft minecraft = Minecraft.getInstance();
			hideWindowOnce(minecraft);
			minecraft.options.getSoundSourceOptionInstance(net.minecraft.sounds.SoundSource.MUSIC).set(0.0);
			minecraft.getMusicManager().stopPlaying();
		}
		boolean nowLinked = KspLink.active();
		if (nowLinked) {
			KspLink.readHostState(host); // on a torn read we simply keep last frame's state
			dev.kerbalcraft.world.KspWater.refresh();
		} else {
			dev.kerbalcraft.world.KspWater.clear();
		}
		if (nowLinked != linked) {
			linked = nowLinked;
			KerbalCraft.LOG.info("KerbalCraft: KSP link {}", linked ? "up" : "down");
			if (linked) {
				tookOver = true;
				unlinkedHold = null;
				KspCollision.startConsumer();
				applyLinkedOptions();
			} else {
				InputBridge.releaseAll();
				LocalPlayer player = Minecraft.getInstance().player;
				unlinkedHold = player != null ? player.position() : null;
			}
		}
		// Each Steve-mode session starts KSP's renderer empty (no atlas, no meshes): send everything again.
		boolean steveOn = nowLinked && host.inGame();
		if (steveOn && !steveOnLastFrame) {
			KspLink.invalidateRender();
		}
		steveOnLastFrame = steveOn;

		if (!linked) {
			return;
		}

		Minecraft minecraft = Minecraft.getInstance();
		hideWindowOnce(minecraft);
		applyViewportSize(minecraft);
		MirrorWorld.openWhenReady(minecraft);

		if (host.menuOpen() || host.loading()) {
			InputBridge.releaseAll();
		}
		InputBridge.drain(minecraft);
		ProxySync.frame(minecraft);

		LocalPlayer player = minecraft.player;
		if (player == null) {
			lastPlayer = null;
			return;
		}

		dev.kerbalcraft.world.BodyPhysics.apply(player, host.gravity);

		// A new player object means we just joined or respawned: put it where KSP's player is.
		if (player != lastPlayer) {
			lastPlayer = player;
			teleportPending = true;
		}
		if (host.teleportSeq != lastTeleportSeq) {
			lastTeleportSeq = host.teleportSeq;
			teleportPending = true;
		}
		if (teleportPending && host.inGame() && !host.loading()) {
			requestTeleport(minecraft, host.x, host.y, host.z, host.yaw, host.pitch);
			teleportAck = host.teleportSeq;
			teleportPending = false;
			holdPos = new Vec3(host.x, host.y, host.z);
		}

		// Look direction is driven by KSP (zero-latency camera); MC uses it for everything else.
		if (minecraft.gui.screen() == null) {
			player.setYRot(host.yaw);
			player.setXRot(host.pitch);
			player.yRotO = host.yaw;
			player.xRotO = host.pitch;
		}
	}

	// Minecraft is started with KSP (the SKSE plugin launches it), so it goes when that KSP has
	// closed for good: saved and shut down the normal way. -Dkerbalcraft.quitWithKsp=false keeps it
	// running instead (development: restarting KSP without restarting Minecraft).
	private static final boolean QUIT_WITH_KSP = Boolean.parseBoolean(System.getProperty("kerbalcraft.quitWithKsp", "true"));
	private static long kspGoneSince;
	private static long nextKSPCheck;
	// Started hidden by KSP but never connected: nobody can see or use this Minecraft, and it
	// would stop the next KSP from starting a fresh one ("already running"). It goes after this.
	private static final long NEVER_CONNECTED_QUIT_MS = 10 * 60 * 1000;
	private static final long STARTED_AT = System.currentTimeMillis();
	private static boolean gaveUpWaiting;

	private static void quitWithKsp(Minecraft minecraft) {
		int pid = KspLink.hostPid();
		long now = System.currentTimeMillis();
		if (QUIT_WITH_KSP && START_HIDDEN && pid == 0 && !tookOver && !gaveUpWaiting && now - STARTED_AT > NEVER_CONNECTED_QUIT_MS) {
			gaveUpWaiting = true;
			KerbalCraft.LOG.warn("KerbalCraft: started hidden but KSP never connected in {} minutes; quitting", NEVER_CONNECTED_QUIT_MS / 60000);
			minecraft.stop();
			return;
		}
		if (!QUIT_WITH_KSP || pid == 0 || now < nextKSPCheck) {
			return;
		}
		nextKSPCheck = now + 1000;
		if (ProcessHandle.of(pid).map(ProcessHandle::isAlive).orElse(false)) {
			kspGoneSince = 0;
			return;
		}
		if (kspGoneSince == 0) {
			kspGoneSince = now;
		} else if (now - kspGoneSince > 5000) {
			KerbalCraft.LOG.info("KerbalCraft: KSP (pid {}) has closed; saving and quitting", pid);
			minecraft.stop();
		}
	}

	/** Called at the end of every client tick. */
	public static void clientTick(Minecraft minecraft) {
		MirrorWorld.tick(minecraft);
		KspDigClient.tick(minecraft);
		freezeWhileUnlinked(minecraft);
		holdUntilReady(minecraft);
		publishTick(minecraft);
	}

	/**
	 * KSP went quiet (a long loading screen, a stall, or it closed). Its collision around the
	 * player may be about to change (interior doors), so keep the player exactly where they were
	 * instead of letting them fall; KSP puts them where they belong when it's back.
	 */
	private static void freezeWhileUnlinked(Minecraft minecraft) {
		LocalPlayer player = minecraft.player;
		if (linked || !tookOver || player == null) {
			return;
		}
		if (unlinkedHold == null) {
			unlinkedHold = player.position();
		}
		player.setDeltaMovement(Vec3.ZERO);
		player.setPos(unlinkedHold.x, unlinkedHold.y, unlinkedHold.z);
		player.xo = unlinkedHold.x;
		player.yo = unlinkedHold.y;
		player.zo = unlinkedHold.z;
		player.resetFallDistance();
	}

	/**
	 * Hands KSP the raw physics tick (previous + latest feet, smoothed eye height, walk bob) with a
	 * QueryPerformanceCounter timestamp. KSP interpolates between them on its own frame clock,
	 * exactly like Minecraft's renderer does with partial ticks.
	 */
	private static void publishTick(Minecraft minecraft) {
		LocalPlayer player = minecraft.player;
		if (!linked || player == null) {
			return;
		}
		if (qpcFreq == 0) {
			qpcFreq = KspLink.qpcFrequency();
		}
		float tickMs = minecraft.level != null ? minecraft.level.tickRateManager().millisecondsPerTick() : 50.0F;
		// The tick really "happened" partial ticks ago (DeltaTracker keeps the remainder).
		float remainder = minecraft.getDeltaTracker().getGameTimeDeltaPartialTick(false);
		mc.tickQpc = KspLink.qpc() - (long) (remainder * tickMs * qpcFreq / 1000.0);
		mc.tickMs = tickMs;
		mc.prevX = player.xo;
		mc.prevY = player.yo;
		mc.prevZ = player.zo;
		mc.curX = player.getX();
		mc.curY = player.getY();
		mc.curZ = player.getZ();
		// Same smoothing as Camera.tick(): eye height eases halfway toward the target each tick.
		if (player != eyePlayer) {
			eyePlayer = player;
			eyeSmoothed = player.getEyeHeight();
		}
		mc.eyeHeightO = eyeSmoothed;
		eyeSmoothed += (player.getEyeHeight() - eyeSmoothed) * 0.5F;
		mc.eyeHeightT = eyeSmoothed;
		boolean bob = minecraft.options.bobView().get();
		var avatar = player.avatarState();
		mc.walkDistO = bob ? avatar.getInterpolatedWalkDistance(0.0F) : 0.0F;
		mc.walkDist = bob ? avatar.getInterpolatedWalkDistance(1.0F) : 0.0F;
		mc.bobO = bob ? avatar.getInterpolatedBob(0.0F) : 0.0F;
		mc.bob = bob ? avatar.getInterpolatedBob(1.0F) : 0.0F;
		KspLink.writeMcState(mc);
	}

	/** Freeze the player until KSP's collision around them has arrived. */
	private static void holdUntilReady(Minecraft minecraft) {
		LocalPlayer player = minecraft.player;
		if (!linked || player == null) {
			return;
		}
		if (!host.inGame() || host.loading()) {
			// KSP is on its main menu or a loading screen: park the player where they are.
			if (holdPos == null) {
				holdPos = player.position();
			}
			teleportPending = true;
		}
		if (holdPos == null) {
			holdSince = 0;
			return;
		}
		if (holdSince == 0) {
			holdSince = System.currentTimeMillis();
		}
		int bx = (int) Math.floor(holdPos.x), by = (int) Math.floor(holdPos.y), bz = (int) Math.floor(holdPos.z);
		boolean known = KspCollision.isKnown(bx, by - 1, bz) && KspCollision.isKnown(bx, by, bz)
			&& KspCollision.isKnown(bx, by - KspCollision.REGION_SIZE, bz);
		// Release once there is actual ground below (or after a timeout, e.g. when mid-air on purpose).
		boolean ready = known && (KspCollision.hasSolidBelow(bx, by, bz, 12) || System.currentTimeMillis() - holdSince > 6000);
		if (ready && host.inGame() && !host.loading()) {
			// KSP's feet can sit a fraction of a voxel inside our ground layer. Minecraft's
			// collision never pushes you out of a shape, so you'd drop through: lift out first.
			Vec3 safe = liftOutOfGeometry(player, holdPos);
			if (safe.y != holdPos.y) {
				player.setPos(safe.x, safe.y, safe.z);
				player.yo = safe.y;
				KerbalCraft.LOG.info("KerbalCraft: lifted player {} blocks out of the ground", String.format("%.3f", safe.y - holdPos.y));
			}
			holdPos = null;
			return;
		}
		player.setDeltaMovement(Vec3.ZERO);
		player.setPos(holdPos.x, holdPos.y, holdPos.z);
		player.xo = holdPos.x;
		player.yo = holdPos.y;
		player.zo = holdPos.z;
		player.resetFallDistance();
	}

	private static Vec3 liftOutOfGeometry(LocalPlayer player, Vec3 pos) {
		// Stand on the exact KSP ground if it is slightly above the feet (up to 2.5 blocks).
		double ground = KspCollider.groundAt(pos.x, pos.y, pos.z, 2.5);
		return !Double.isNaN(ground) && ground > pos.y ? new Vec3(pos.x, ground, pos.z) : pos;
	}

	private static void requestTeleport(Minecraft minecraft, double x, double y, double z, float yaw, float pitch) {
		LocalPlayer player = minecraft.player;
		player.setPos(x, y, z);
		player.setDeltaMovement(Vec3.ZERO);
		player.resetFallDistance();
		var server = minecraft.getSingleplayerServer();
		if (server != null) {
			var uuid = player.getUUID();
			server.execute(() -> {
				ServerPlayer sp = server.getPlayerList().getPlayer(uuid);
				if (sp != null) {
					sp.teleportTo(x, y, z);
					sp.setYRot(yaw);
					sp.setXRot(pitch);
					sp.resetFallDistance();
				}
			});
		}
		KerbalCraft.LOG.info("KerbalCraft: teleported to {} {} {}", x, y, z);
	}

	/** After GameRenderer.render(): report the player to KSP and ship the overlay frame. */
	public static void afterRender() {
		if (!linked) {
			return;
		}
		Minecraft minecraft = Minecraft.getInstance();
		LocalPlayer player = minecraft.player;
		int flags = 0;
		if (player != null && minecraft.level != null) {
			float partial = minecraft.getDeltaTracker().getGameTimeDeltaPartialTick(false);
			Vec3 feet = player.getPosition(partial);
			Camera camera = minecraft.gameRenderer.mainCamera();
			flags |= Proto.MC_IN_WORLD;
			if (player.onGround()) {
				flags |= Proto.MC_ON_GROUND;
			}
			if (player.isShiftKeyDown()) {
				flags |= Proto.MC_SNEAKING;
			}
			if (player.isSprinting()) {
				flags |= Proto.MC_SPRINTING;
			}
			if (player.isDeadOrDying()) {
				flags |= Proto.MC_DEAD;
			}
			if (player.isSwimming()) {
				flags |= Proto.MC_SWIMMING;
			}
			if (player.getAbilities().flying) {
				flags |= Proto.MC_FLYING;
			}
			mc.x = feet.x;
			mc.y = feet.y;
			mc.z = feet.z;
			mc.yaw = player.getYRot();
			mc.pitch = player.getXRot();
			// The eye, not the camera: in third person Minecraft's camera sits behind or in front.
			Vec3 eye = camera.isDetached() ? player.getEyePosition(partial) : camera.position();
			mc.eyeHeight = (float) (eye.y - feet.y);
			mc.eyeX = eye.x;
			mc.eyeY = eye.y;
			mc.eyeZ = eye.z;
			mc.fov = camera.getFov();
			// Minecraft's F5 camera: KSP puts its camera where Minecraft's would be.
			mc.cameraMode = minecraft.options.getCameraType().ordinal();
			mc.cameraDistance = camera.isDetached() ? (float) camera.position().distanceTo(player.getEyePosition(partial)) : 0.0F;
			// Walk bob, exactly what GameRenderer.bobView() uses this frame.
			var entityState = minecraft.gameRenderer.gameRenderState().levelRenderState.cameraRenderState.entityRenderState;
			boolean bob = minecraft.options.bobView().get() && entityState.isPlayer;
			mc.bobPhase = bob ? entityState.backwardsInterpolatedWalkDistance : 0.0F;
			mc.bobAmount = bob ? entityState.bob : 0.0F;
		}
		if (minecraft.gui.screen() != null) {
			flags |= Proto.MC_SCREEN_OPEN;
		}
		mc.flags = flags;
		mc.sensitivity = minecraft.options.sensitivity().get().floatValue();
		mc.teleportAck = holdPos == null ? teleportAck : teleportAck - 1; // not "arrived" until we are released
		mc.guiScale = minecraft.getWindow().getGuiScale();
		mc.frameCounter = ++frameCounter;
		KspLink.writeMcState(mc);

		if ((flags & Proto.MC_IN_WORLD) != 0) {
			try {
				WorldExporter.frame(minecraft, minecraft.getDeltaTracker().getGameTimeDeltaPartialTick(false));
			} catch (RuntimeException e) {
				if (exporterErrors++ < 5) {
					KerbalCraft.LOG.error("KerbalCraft: world export failed", e);
				}
			}
			FrameExporter.capture(minecraft);
		}
	}

	/** End of the frame: render at most once per KSP frame instead of spinning freely. */
	public static void paceFrame() {
		if (!linked) {
			return;
		}
		if (kspStalled && (KspLink.hostStateSeq() >>> 1) == lastPacedSeq) {
			return; // KSP is paused (menu / alt-tab): don't block every frame waiting for it
		}
		kspStalled = false;
		long deadline = System.nanoTime() + 25_000_000L;
		// HostState.seq advances by 2 per KSP frame (odd while writing).
		while ((KspLink.hostStateSeq() >>> 1) == lastPacedSeq && System.nanoTime() < deadline) {
			Thread.onSpinWait();
			if (deadline - System.nanoTime() > 2_000_000L) {
				Thread.yield();
			}
		}
		int seqNow = KspLink.hostStateSeq() >>> 1;
		kspStalled = seqNow == lastPacedSeq;
		lastPacedSeq = seqNow;
	}

	private static void applyLinkedOptions() {
		Minecraft minecraft = Minecraft.getInstance();
		var options = minecraft.options;
		options.pauseOnLostFocus = false;
		options.vignette().set(false);
		options.enableVsync().set(false);
		options.framerateLimit().set(260);
		// Minecraft doesn't draw the world itself; these only decide how far out placed blocks,
		// arrows and KSP vessel-part stand-ins stay loaded and simulated.
		options.renderDistance().set(8);
		options.simulationDistance().set(8);
		options.autoJump().set(false);
		options.onboardAccessibility = false;
		if (options.tutorialStep != net.minecraft.client.tutorial.TutorialSteps.NONE) {
			minecraft.getTutorial().setStep(net.minecraft.client.tutorial.TutorialSteps.NONE);
		}
		options.getSoundSourceOptionInstance(net.minecraft.sounds.SoundSource.MUSIC).set(0.0);
		options.save();
	}

	private static void hideWindowOnce(Minecraft minecraft) {
		if (windowHidden || SHOW_WINDOW) {
			return;
		}
		windowHidden = true;
		SDLVideo.SDL_HideWindow(minecraft.getWindow().handle());
		KerbalCraft.LOG.info("KerbalCraft: game window hidden (run with -Dkerbalcraft.showWindow=true to keep it)");
	}

	private static void applyViewportSize(Minecraft minecraft) {
		int w = host.viewportW;
		int h = host.viewportH;
		// Keep the aspect ratio when the window is bigger than the overlay buffer.
		double fit = Math.min(1.0, Math.min((double) Proto.MAX_OVERLAY_W / Math.max(w, 1), (double) Proto.MAX_OVERLAY_H / Math.max(h, 1)));
		w = (int) (w * fit);
		h = (int) (h * fit);
		if (w <= 0 || h <= 0 || (w == appliedViewportW && h == appliedViewportH)) {
			return;
		}
		appliedViewportW = w;
		appliedViewportH = h;
		minecraft.getWindow().setWindowed(w, h);
		KerbalCraft.LOG.info("KerbalCraft: sizing overlay to KSP viewport {}x{}", w, h);
	}
}
