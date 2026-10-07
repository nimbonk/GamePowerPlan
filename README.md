# GamePowerPlan

A tiny tray app for Windows that puts your PC on the high performance power plan while you're playing a game, and back on balanced when you're not.

I made it because electric is getting expensive and I didn't want my PC sitting on full performance all day for no reason. I just wanted it to switch by itself when I launch a game and switch back when I'm done. That's it.

I built this and tested it on my own PC (Ryzen, Windows 10 and Windows 11). Read the source, it's one file.

It's made by [Nimbonk](https://nimbonk.fyi). The Microsoft Store version is coming soon.

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
- The exe isn't code signed, so Windows SmartScreen may warn about an unknown publisher, and some antivirus tools can get twitchy about a program that scans processes and adds a startup entry. The source is right here so you can check it, or build it yourself.
- Games installed outside the usual folders won't be spotted until you add their folder from the Games menu.

## Is it safe?

GamePowerPlan is open source: everything it does is in `GamePowerPlan.cs`, and you can read it or build it yourself with `build.bat`.

- It has no network code and collects nothing. See the [privacy policy](https://nimbonk.fyi/privacy/).
- The plain exe is not code signed, because certificates cost money. Windows SmartScreen may warn about it, and a few antivirus engines can flag small unsigned tools as a false positive.
- The Microsoft Store version is signed by Microsoft, so it avoids those warnings.
- Each release lists a SHA-256 hash and a VirusTotal scan, so you can check your download matches.

## Why does the Store version need full trust?

GamePowerPlan is a .NET desktop tray app packaged as a full trust app. It needs the `runFullTrust` capability to use Win32 APIs that UWP apps can't:

- It reads the file paths of running programs (`OpenProcess`, `QueryFullProcessImageName`) to detect games.
- It switches the Windows power plan (`PowerSetActiveScheme`, `powercfg.exe`).
- It uses a tray icon and an optional global hotkey.

It does not read process memory, inject code, use the network or collect any data. The full source is in this repo, so you can check all of this yourself.

## Store package

The `msix` folder has what's needed to build the Microsoft Store package. Run `msix\build-msix.bat` (it needs the Windows SDK). In the Store version, "Start with Windows" opens Windows' own Startup apps settings, because the usual registry method doesn't work inside a Store package.

## Licence

MIT. See the LICENSE file.
