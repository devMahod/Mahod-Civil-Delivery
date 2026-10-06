# Civil 3D 2026 compatibility — candidate 1.2.35

**Status:** `COMPILED_GUI_ACCEPTANCE_PENDING`  
**Platform file version:** 1.3.25.0  
**Date:** 2026-09-02

The 2026 component is real and is not compiled from 2027 Civil assemblies.

## Reference chain

- AutoCAD core references come from the local AutoCAD 2026 installation (`25.1`).
- Civil references come from Autodesk's `Civil3D.NET` package `13.8.280`, targeting
  `net8.0`. The restored `AeccDbMgd.dll` reports file version `13.8.280.0`.
- The package is compile-only (`ExcludeAssets="runtime"`). Autodesk host assemblies
  are not copied into the plug-in bundle.
- The 2027 component remains a separate `net10.0-windows` build against the locally
  installed Civil 3D 2027 references.

## Independently verified offline

```powershell
dotnet build MahodAI.Civil3D.Plugin/MahodAI.Civil3D.Plugin.csproj -c Release -t:Rebuild -p:AutoCADVersion=2026 -p:DeployPlugin=false
dotnet test MahodAI.Core.Tests -c Release -p:AutoCADVersion=2026 -p:DeployPlugin=false
dotnet test MahodAI.Civil3D.Plugin.Tests -c Release -p:AutoCADVersion=2026 -p:DeployPlugin=false --filter "Category!=RequiresCivil3D"
```

The 2026-08-31 result (Core 440/440 and filtered Plugin 1533/1533) belongs to an
earlier source state. Candidate 1.2.35 must record fresh counts after its final
sequential lane; no older count is release evidence. Tests tagged `RequiresCivil3D`
are intentionally excluded outside a Civil host.

## Packaging contract

The installer must contain both modules:

```text
MahodAI.bundle/Contents/MahodAI.Civil3D.Plugin.dll       (2027, net10)
MahodAI.bundle/Contents/2026/MahodAI.Civil3D.Plugin.dll  (2026, net8)
```

`PackageContents.xml` maps AutoCAD series `R26.0` to the 2027 module and `R25.1`
to the 2026 module. `Test-CandidateIdentity.ps1` compares both platform DLLs and
both Core DLLs from current Release outputs through every staged release hop.

## Not yet proven

No GUI/load claim is made for candidate 1.2.35 on Civil 3D 2026. A successful
compile and host-free test run do not prove Autodesk can load the module or that the
palette and commands behave correctly in a live 2026 process. That remains a manual
gate on a writable drawing copy.
