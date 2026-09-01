# lib/

Third-party and internal assemblies that are not available as NuGet packages
and must therefore travel with the source.

## AppRegistryEditor.dll

Internal Xenxible utility. `DeviceClusterConsoleApp` uses it in
`Program.cs` (`GetConnectionString`) to read the SQL connection string from
the Windows registry:

    HKEY_CURRENT_USER\Software\XenxibleIdentifier\connectionstring

It was previously referenced from `C:\Users\<user>\Documents\` via a relative
path that climbed five directories out of the repository. That resolved only
on the original developer's machine, at one specific clone location; anywhere
else the build failed with a missing assembly and no explanation. The DLL now
lives here so the build works from any clone.

If this assembly ever gains a NuGet package, replace the `Reference` /
`HintPath` in `DeviceClusterConsoleApp.csproj` with a `PackageReference` and
delete this copy.

`DeviceClusterServiceApp` reads the same registry key but does not use this
assembly - it calls `Microsoft.Win32.Registry` directly (`Program.cs`,
`GetConnectionString`, around line 456). The console app could very likely do
the same and drop this dependency altogether, which would let this folder go
away. That has not been attempted here.
