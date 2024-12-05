# Inaba-Exe-Patcher
A [Reloaded-II](https://github.com/Reloaded-Project/Reloaded-II) mod for patching the executable of games at startup. Firstly made for P4G but now compatible with any game! 

For details on using with P4G (2020) and the regular patch format check out the [Gamebanana page](https://gamebanana.com/tools/6872) for this mod.

## Ex Patches
Ex Patches are a format that allows mod makers to quickly make simple (or somewhat complicated) assembly patches and replacements of data embedded in a game's exe (such as strings). 

For documentation on creating and using Ex Patches please check the [Wiki](https://github.com/TekkaGB/Inaba-Exe-Patcher/wiki).

# The "UTF-16" Part
replacement blocks on this fork support an additional config option called "encoding", which can be any of the following values (default is us-ascii)
- utf-16
- utf-16BE
- utf-32
- utf-32BE
- us-ascii
- iso-8859-1
- utf-8
