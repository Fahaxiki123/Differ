# App-local Visual C++ runtime

`vcruntime140.dll` is an unmodified x64 Microsoft redistributable from the installed Visual Studio 2026 Community **retail** redist directory:

`VC/Redist/MSVC/14.51.36231/x64/Microsoft.VC145.CRT/vcruntime140.dll`

- File/product version: **14.51.36247.0**.
- Bytes: **178616**.
- SHA-256: **D1F4225DF2CD877DBF130D5668A021DCE3F94118455FF5EC952061C30AFC9CE7**.
- Authenticode validation on 2026-10-24: **Valid**, Microsoft Windows Software Compatibility Publisher / Microsoft Corporation.
- Purpose: SharpSvn 1.14005.390 statically imports `VCRUNTIME140.dll`; the .NET runtime's differently named `vcruntime140_cor3.dll` does not satisfy that import. `Ijwhost.dll` remains provided by the pinned SharpSvn NuGet runtime assets.
- The project copies this DLL next to `Differ.App.exe` in build/publish output. No system directories, registry keys, installed VC Runtime or other programs are changed.

Distribution uses the Visual Studio 2026 license and official REDIST list, which permits unmodified retail files under `VC/redist`; debug_nonredist/preview files are excluded. Full official license text and the redist list are retained in `docs/third-party` and included by the existing notices manifest. Source URLs:

- https://visualstudio.microsoft.com/license-terms/vs2026-ga-community/
- https://visualstudio.microsoft.com/license-terms/vs2026-ga-visualcpp-v14-redist-runtime/
- https://learn.microsoft.com/en-us/visualstudio/releases/2026/redistribution
- https://learn.microsoft.com/en-us/cpp/windows/choosing-a-deployment-method?view=msvc-170

Microsoft generally recommends central runtime servicing. App-local deployment is used here to preserve the approved current-user/no-administrator offline installation. **Differ releases own updates of this private DLL**: monitor Visual C++ security/servicing releases, replace only from a licensed Microsoft retail redistributable source, verify x64 architecture, signature, dependencies, version and SHA-256, then update this record and the notices manifest. Do not assume Windows Update services this private copy. Uninstall removes only this application's own copy.

Static `dumpbin /DEPENDENTS` verification: this DLL imports only Windows Universal CRT API sets and `KERNEL32.dll`; `Ijwhost.dll` and `WebView2Loader.dll` introduce no additional VC runtime DLL dependency. Clean-machine validation is tracked separately; a successful developer-machine load is not a clean-machine proof.
