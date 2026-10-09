# GamePowerPlan

A tiny tray app for Windows that puts your PC on the high performance power plan while you're playing a game, and back on balanced when you're not.

I made it because electric is getting expensive and I didn't want my PC sitting on full performance all day for no reason. I just wanted it to switch by itself when I launch a game and switch back when I'm done. That's it.

I built this and tested it on my own PC (Ryzen, Windows 10 and 11). Read the source, it's one file.

It's made by [Nimbonk](https://nimbonk.fyi).

## Download

Get it from the [Microsoft Store](https://apps.microsoft.com/detail/9PF9KW933WVG). The Store version is signed by Microsoft, so you won't get SmartScreen or antivirus warnings. Or build it yourself, see below.

## How it works

It sits in your system tray and checks every 5 seconds whether a game is running. It doesn't have a list of game names. Instead, it looks at where each running program lives on disk. If the program is inside a game folder (Steam, Epic, GOG, Riot, Xbox, Ubisoft, EA, Battle.net, or any folder you add), it counts as a game.

- Game found: switches to your gaming plan.
- Game closed: waits 60 seconds, then switches back to your idle plan. The wait stops it flipping back and forth during loading screens or launcher restarts.
- While a game is running it only checks that one game is still alive, so it costs basically nothing while you play.

It only changes the plan when it actually needs to, not every check.

## Which plans it uses

It looks at the plans on your PC and picks for you:

- Gaming: AMD Ryzen High Performance if you have it, otherwise the normal Windows High performance.
- Idle: AMD Ryzen Balanced if you have it, otherwise the normal Windows Balanced.

It never picks Ultimate Performance by itself. That plan just uses more power and runs hotter for no real gain in games. You can pick different plans yourself from the menu if you want.

## The tray icon

The icon is green on your idle plan and orange on your gaming plan. You can change both colours.

Left click the icon for a quick panel: current plan, what mode it's in, which game it found and for how long, and Auto / High / Balanced buttons.

Right click for the full menu:

- Auto, Force gaming plan, Force idle plan
- Games: add a game by picking its .exe or folder, remove folders, and ignore programs that shouldn't count as games (it also has an "ignore detected program" button for when something is wrongly triggering high performance)
- Power plans: choose your gaming and idle plans
- Settings: grace period, check interval, hotkey, icon colours, notifications, start with Windows
- Reload settings, Open settings file, Exit

Everything can be done from the menu, you never have to edit a file.

## If something else changes your plan

If another program, Windows, or another tool changes your power plan behind its back, you get a notification and the app puts it back. You can turn the notifications off, or tell it to leave the plan alone, in Settings. It can tell something else changed the plan, but it can't tell what.

The Force modes hold firm too, so if you force a plan, it stays on that plan.

## Hotkey

You can set a global hotkey from Settings (it has to include Ctrl or Alt). It cycles Auto, forced gaming plan and forced idle plan, and shows a notification so you know where you landed. It's not set by default so it can't clash with anything.

## Settings file

Settings are saved in `GamePowerPlan.cfg`, in the same folder as the exe. It's created the first time you run the app. If you do edit it by hand, use Reload settings in the tray menu to apply the changes. If the exe is somewhere Windows won't let it write (like Program Files), it uses `%APPDATA%\GamePowerPlan\` instead. The Store version always uses the second location.

## Build it

You don't need to install anything. Windows already comes with a C# compiler. Download `GamePowerPlan.cs`, `GamePowerPlan.manifest` and `build.bat` into one folder and run `build.bat`. Or open a terminal there and run this on one line:

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /out:GamePowerPlan.exe /win32manifest:GamePowerPlan.manifest /r:System.Windows.Forms.dll /r:System.Drawing.dll GamePowerPlan.cs
```

Then run `GamePowerPlan.exe`. If it's already running, exit it from the tray first, because Windows won't let you replace a running exe.

## Things to know

- Tested on Windows 10 and Windows 11.
- It doesn't touch your games. No injecting, no reading game memory, no overlays, no drivers. It only asks Windows for each program's file path and changes the power plan. I can't promise every anti-cheat will be happy with any background program though, so use it at your own risk.
- The exe isn't code signed, so Windows SmartScreen may warn about an unknown publisher, and some antivirus tools can get twitchy about a program that scans processes and adds a startup entry. That's why there's no prebuilt exe here. The source is right here so you can check it, use the Store version, or build it yourself.
- Games installed outside the usual folders won't be spotted until you add their folder from the Games menu.
- No network code, and it collects nothing. [Privacy policy](https://nimbonk.fyi/privacy/).
- The Store version asks for "full trust" because it has to read the paths of running programs and change the power plan, which sandboxed apps can't do. It doesn't read memory or inject anything. It's all in GamePowerPlan.cs.
- The msix folder is the Store packaging.

## Licence

MIT. See the LICENSE file.
