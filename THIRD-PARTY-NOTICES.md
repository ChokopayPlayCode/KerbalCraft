# Third-party notices

KerbalCraft is MIT-licensed (see `LICENSE`). It is derived from **SkyCraft** by chasmlol (MIT): the
Fabric mod and the shared-memory protocol come from it. A release also contains, or is built from,
the following.

## In the KSP plugin (`KerbalCraft.dll`)

Nothing beyond Kerbal Space Program's own assemblies (referenced at build time, not redistributed)
and Unity. The optional shader bundle is built from the `.shader` files in `ksp/Unity/Shaders`.

## In the bundled Minecraft (`KerbalCraft-Minecraft.zip`)

| Component | License | Source |
|---|---|---|
| Prism Launcher (unmodified portable Windows build) | GPL-3.0 | https://github.com/PrismLauncher/PrismLauncher (the version is in the bundle's `THIRD-PARTY.txt`) |
| Fabric API | Apache-2.0 | https://github.com/FabricMC/fabric |
| e4mc | MIT | https://github.com/vgskye/e4mc-minecraft-architectury |

The bundle carries Prism Launcher's full license text as `Prism/LICENSE-PrismLauncher.txt`.

## Not included

Minecraft, Java and Fabric Loader aren't included. Prism Launcher downloads them from Mojang,
the Java vendor and FabricMC after the player signs in with a Microsoft account that owns
Minecraft: Java Edition. Kerbal Space Program isn't included either.

KerbalCraft isn't affiliated with or endorsed by Mojang, Microsoft, Squad, Take-Two or Intercept Games.
