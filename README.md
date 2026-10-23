# **THIS MOD ONLY NEEDS TO BE ON THE HOST! UNMODDED CLIENTS CAN PLAY!**



## Big Lobbies



## Mod made by "AdamMady" you can join my discord here to give me feedback or give me new mod ideas: https://discord.gg/5z3WvVhxCf



## Features:



#### Fixes the main problem with the original "Bigger Walk" mod by adding voice support past 12 players!



#### Host bigger lobbies! Defaults to 50 players, adjustable up to 100 through Mod Settings.



#### Supports megaphones, walkie-talkies and radio stations!



#### Change player limit, hearing distance and network update cap while playing!



#### Can (in theory) reduce network traffic even in lobbies with less than 13 players!



## Settings:



#### Get the Mod Settings Menu mod to change these settings in-game. Without it, settings can be changed in BepInEx/config/AdamMady.BiggerWalkAndTalk.cfg.



#### MaxPlayerCount: 50 by default.



#### QuietThreshold: 0.004 by default. Quiet speakers lose priority only when a listener's voice slots are full.



#### Voice Quality: A live 15-100% slider, defaulting to 100% (17 kbps). Lower values recompress guest voices on the host using extra CPU. 100% preserves original guest packets. FEC settings and audio frame duration are unchanged.



#### Host Voice Quality: A separate live 15-100% slider under Voice for your own transmitted voice. 100% targets 17 kbps; it is independent of guest Voice Quality.



#### Update Cap Hz: Normal by default. Cycles 5, 10, 15, 20, 25, Normal, 25, 20, 15, 10, then back to 5 Hz. Lower values make movement less smooth but can reduce outgoing world-update traffic.



#### Hearing Distance: 30 metres by default, Can improve network traffic and ping.



## Important:



#### Unmodded clients still have a limited number of people that they can hear. This mod reuses those slots based on item, volume, distance, so on but it does not let everyone in a huge crowd speak simultaneously. Each listener gets voice slots assigned to speakers they can hear.



#### Bigger lobbies need more host upload bandwidth and processing power. Expect the game to crash at any moment, not only on your side, but even sometimes on other people's devices, more optimization will happen as feedback is given, You can adjust values for network optimization in the mod settings menu.



## Installation:



#### Step 1: Install BepInEx 6 IL2CPP for Windows x64 from https://builds.bepinex.dev/projects/bepinex_be and extract it into the game directory. Launch the game once to finish setup, then close it.



#### Step 2: Copy the BepInEx folder included with this mod into the game directory. The DLL is BepInEx/plugins/BigLobbies.dll. If updating from BiggerWalkAndTalk.dll, remove that old DLL first. Disable Bigger Walk and the old event version so only one lobby-capacity mod is loaded.



#### Step 3: Host a game and invite your friends! Only the host needs this mod. Mod Settings Menu is optional.



## Thank you for installing my mod!



## Credit and license:



#### Based on Bigger Walk by glarmer: https://github.com/glarmer/bigger_walk



#### Big Lobbies source: https://github.com/AdamMady/Big-Lobbies



#### Source matching version 1.0.0: https://github.com/AdamMady/Big-Lobbies/tree/582566503f865da1289f194c7fdfacdbe67372ae



#### Voice routing and live settings by AdamMady. This is a separate modified version, not the original Bigger Walk release.



#### Licensed under GNU GPL v3. See LICENSE and NOTICE. If you distribute this DLL, provide its corresponding modified source and build files under the same license.
