![Mod Logo](https://moddbcdn.vintagestory.at/ModDB+Card_99e77500822135694d92d8a6b1d47262.png)

# No Offline Container Food Spoil

### $\color{#FF0000}\text{DISCLAIMER}$
This mod does not prevent spoilage in a player's inventory. There is an existing [mod](https://mods.vintagestory.at/offlinefoodnospoil) for this made by [Wiltoga](https://github.com/Wiltoga), and I really don't know how to make a good-enough solution that wouldn't just copy their work. Both mods should be compatible with each other.

Also I really don't know what I am doing, so if I messed something obvious up in the code I apologize. I will do my best to keep this working/bug free as possible.

---

### Overview
This is a mod created for the game "Vintage Story".

This mod targets an issue with food in Vintage Story multiplayer. In the base game, if a player leaves food in a container and then leaves the server, it will gradually spoil over time. Players that spend large amounts of time on servers can negatively affect other player's experience as by the time they log back on, everything they have will be turned into rot.

More in-depth testing is in progress to make sure that it performs as expected. I will put a list of what was tested in this at a later point.

### How it works
Containers blocks have custom behavior attached to them that tracks who placed the block, as well as who last opened it. If the owner of the block is offline, the "transition" rate for that container will be changed to 0.

If a player interacts with this container, the food in it will spoil like normal until that player leaves. If requested enough, I may try to find a way to make this feature toggleable through a config.

---

### Credit where it is due!
This was not my idea! I knew this was a problem and was looking for a solution, and credit for this idea goes fully to [Tabulius](https://www.vintagestory.at/profile/347446-tabulius/)! The post I found their idea on is [here](https://www.vintagestory.at/forums/topic/18000-mod-request-solution-to-offline-food-perishing-in-multiplayer/).

Releases for this can be found on Vintage Story's [Mod Portal](https://mods.vintagestory.at/noofflinecontainerfoodspoil).
