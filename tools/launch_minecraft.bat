@echo off
rem Starts the KerbalCraft Minecraft client from source (dev launcher, offline name "Jebediah").
rem It waits on the title screen until KSP (with the KerbalCraft plugin) is running, then hides
rem its window and loads the mirror world by itself.
cd /d "%~dp0..\fabric"
call gradlew.bat runClient --no-configuration-cache
