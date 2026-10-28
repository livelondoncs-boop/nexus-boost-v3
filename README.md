# NEXUS BOOST v3

WPF desktop app for Windows 10/11 x64.

## Implemented
- Dark dashboard UI.
- CPU and RAM usage polling every 2 seconds.
- CPU and GPU model detection.
- Optional GPU load and CPU/GPU temperature readings through LibreHardwareMonitor, when supported by hardware/drivers/permissions.
- Recent CPU/RAM sample history.
- Detects selected running games by process name: CS2, Fortnite, VALORANT, Apex Legends, PUBG, Minecraft Java.
- Applies built-in Windows High performance or Balanced power plan when available, with confirmation.
- Shows available power plans and opens Windows power settings.
- Session-only restore of previous power plan.
- Does not disable security services, kill processes, edit registry, overclock hardware, or fabricate FPS.

## Build locally
Requirements: Windows 10/11, .NET 8 SDK or Visual Studio 2022 with .NET desktop workload.

```powershell
dotnet restore
dotnet run
```

Publish a single-file self-contained executable:
```powershell
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```
Output folder: `bin\Release\net8.0-windows\win-x64\publish\`.

## Build .exe automatically with GitHub Actions
1. Push this folder to a GitHub repository.
2. Open Actions and run the `Build Windows EXE` workflow.
3. Download `NEXUS-BOOST-win-x64` from workflow artifacts.

## Limitations
- GPU sensors can be unavailable depending on hardware, driver, permissions, or whether the sensor is exposed by LibreHardwareMonitor.
- FPS and frame time are not yet integrated. Do not interpret placeholders as live values.
- Game profiles currently provide UI selection and Windows power-plan actions; they do not edit game settings or GPU driver settings.
- Restore point is in memory and clears when app closes.
- A power plan change alone does not guarantee a performance increase.
