# Building

## Requirements

- **Visual Studio 2022** with C++ Desktop workload
- **Windows Driver Kit (WDK)** 10.0.26100.0 or later
- **.NET 9 SDK**
- **Windows 10/11 x64**

## Build

```powershell
.\build.ps1                          # Release build
.\build.ps1 -Configuration Debug     # Debug build
```

The script builds all components in order:

1. **Driver** (`KernelFlirt.sys`) — C/WDM kernel driver
2. **Loader** (`KfLoader.exe`) — C console app
3. **Relay** (`KfRelay.exe`) — C console app
4. **UI** (`KernelFlirt.exe`) — C#/WPF application + SDK
5. **Plugins** (17 DLLs) — C# plugin assemblies

## Output

```
bin/
  Driver/     KernelFlirt.sys
  Loader/     KfLoader.exe
  Relay/      KfRelay.exe
  UI/
    KernelFlirt.exe
    plugins/          ← all plugin DLLs
    themes/           ← 9 color theme files
    retdec/           ← RetDec decompiler
    share/            ← RetDec support files
```

## Driver Signing

For development, run `build.ps1` as Administrator — it will sign the driver with a test certificate. On the VM, enable test signing:

```cmd
bcdedit /set testsigning on
```
