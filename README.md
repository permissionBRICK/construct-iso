# Native Ubuntu autoinstall ISO builder

A standalone .NET 10 application that builds Construct install media on
Windows, Linux or macOS. The application runs entirely in managed code: no WSL, Docker,
mount operation, administrator rights, `xorriso`, `openssl`, or other executable is needed.
Self-contained publishing also removes the requirement to install .NET on the destination.

The first supported/tested source is **Ubuntu Server 24.04.4 live-server amd64**. This is a
targeted patcher for Ubuntu's GRUB/GPT hybrid layout, not a general ISO authoring library.
It validates the source layout and rejects unsupported layouts and already patched images.
Use a stock ISO; source downloading and verification remain the caller's responsibility.

## Build and use

From the repository root, on a development machine with the .NET 10 SDK:

```sh
dotnet publish src/Construct.Iso -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
  -o src/Construct.Iso/bin/publish/win-x64
```

Copy `Construct.Iso.exe` from that directory to the Windows machine. The optional `.pdb`
is only for debugging. Publish for `linux-x64`, `linux-arm64`, `osx-x64` or `osx-arm64` to
produce an executable for those platforms from the same source.

In PowerShell, with the executable in the checkout root:

```powershell
$env:BOOTSTRAP_PUBKEY_FILE = (Resolve-Path .\keys\bootstrap_ed25519.pub).Path
$env:VM_HOST = 'my-agent-vm'
$env:VM_USER = 'agent'
$env:VM_PASS = 'your-seed-password'
.\Construct.Iso.exe C:\ISOs\ubuntu-24.04.4-live-server-amd64.iso C:\ISOs\my-agent-vm-autoinstall.iso
Remove-Item Env:VM_PASS
```

For reusable Hyper-V media, set `$env:VM_HOSTNAME_SOURCE = 'hyperv-kvp'`. Its first-boot
unit adopts the VM name, just as the existing shell builder does. Static mode is selected
by default, or explicitly with `$env:VM_HOSTNAME_SOURCE = 'static'`.

On Linux/macOS with the SDK, the equivalent is:

```sh
BOOTSTRAP_PUBKEY_FILE=reference/bootstrap_ed25519.pub VM_HOST=my-agent-vm \
  dotnet run --project src/Construct.Iso -c Release -- ubuntu.iso my-agent-vm-autoinstall.iso
```

Both positional paths are required. The output directory must exist. Existing output files
are never overwritten. A cancelled or failed build removes its temporary output; a complete
image is published with a rename in the output directory.

| Environment variable | Default / purpose |
|---|---|
| `BOOTSTRAP_PUBKEY_FILE` | Required: SSH public key file |
| `VM_USER` | `agent` |
| `VM_PASS` | `agent`; hashed with SHA-512 crypt and a random salt |
| `VM_HOST` | `agent-vm`; static hostname |
| `VM_REALNAME` | `The Construct` |
| `SOURCE_ID` | `ubuntu-server-minimal`; must exist on the input ISO |
| `VM_HOSTNAME_SOURCE` | `static` or `hyperv-kvp` |

## What gets patched

The installer behavior matches `reference/build-autoinstall-iso.sh`: minimized Ubuntu, direct
storage layout, preset user, SSH and bootstrap key, console provisioning hint, and optional
Hyper-V hostname adoption with passwordless sudo for the generic seed user. The guest shell
script remains shell because it runs **inside Ubuntu**, not on the build machine.

The tool streams a copy of the source and inserts a small payload immediately before the
appended EFI partition. The existing installer file extents and boot-loader binaries stay
fixed. It does not unpack/recompress the squashfs images or hold the ISO in RAM.

The seed files are placed at **`/boot/grub/user-data` and `/boot/grub/meta-data`**, and the
new default GRUB entry points NoCloud to `/cdrom/boot/grub/`. Using an existing directory
avoids creating or relocating path tables. Original manual menu entries are retained.
Both timeout and default settings from the original config are removed so the autoinstall
entry remains the default with a five-second timeout.

All four directory views are updated: ISO9660/Rock Ridge and Joliet, both at the whole-disc
and partition-offset roots. The patcher updates volume sizes, the EFI El Torito address,
the protective MBR size, and both GPT headers/tables with their CRCs. `md5sum.txt` receives
the new GRUB digest and the two seed digests. BIOS and EFI boot images remain byte-identical.
Build space is approximately the size of the output ISO; memory use is bounded to the copy
buffer and metadata rather than the multi-gigabyte installer.

The initial implementation deliberately rejects other boot catalogs/partition layouts,
metadata files above 16 MiB, and directories without room for two additional records.
The Construct's Windows installers use this tool. They prefer a sibling `construct-iso`
checkout (or `CONSTRUCT_ISO_SOURCE_DIR`) with a .NET 10 SDK, otherwise download a pinned,
checksum-verified self-contained release. The shell reference remains available for Linux/
Proxmox development. Coordinate seed behavior changes with The Construct's shell builder;
`reference/` is a test snapshot, not a runtime dependency.

## Installer protocol and releases

`Construct.Iso --request-stdin` reads a JSON object from stdin. Required fields are
`SourceIso`, `OutputIso`, and `BootstrapPublicKeyPath`. Optional fields are `User`, `Password`,
`Hostname`, `RealName`, `SourceId`, `HostnameSource`, and `Overwrite`. Defaults match the
environment interface. Credentials never need to appear in the process argument list.
`Overwrite: true` replaces existing output only after a complete new image has been written;
a failed build leaves existing media intact. Never use it on a source/output alias.

Only pushes to **this repository's main branch** build and publish releases. Actions tests
Windows, Linux and macOS, builds a self-contained Windows x64 executable, uploads an Actions
artifact, and publishes `Construct.Iso-win-x64.zip` plus `SHA256SUMS` in an immutable
`build-<commit>` release. The Construct pins that tag and both checksums in
`config/iso-builder.json`. Public release assets can be downloaded without a GitHub account;
Actions artifact downloads require authentication and expire.

## Verification

```sh
dotnet test tests/Construct.Iso.Tests -c Release
# Real-image checks: Linux development machine, xorriso + Python 3, ~10 GiB scratch.
bash tests/native-iso.test.sh /path/to/ubuntu-24.04.4-live-server-amd64.iso
```

Unit tests compare password hashes against independent OpenSSL results, exercise seed
validation/escaping, enforce parity between embedded templates and the shell builder, and
check failure/cancellation behavior. Real-image checks use **xorriso only as an independent
test reader**: extract/check every listed file, compare seeds in all four filesystem views,
compare original boot images, validate GPT CRCs, and reject a second patching pass.
The integration script also sets `CONSTRUCT_TEST_ISO` to enable the test that cancels after
the temporary output is created. That test is explicitly skipped when no real ISO is supplied.
CI runs the remaining tests on Windows, Linux and macOS; it does not download an ISO.

Firmware/installer validation is separate from filesystem validation: boot in both BIOS and
UEFI, then install onto a disposable blank disk and verify SSH, the seed identity and the
provisioning banner. Hyper-V KVP adoption and Windows execution require testing on Windows;
QEMU firmware tests do not establish those results.

Validation on 2026-09-06: 22 tests passed with the real ISO supplied; all 295 installer
checksum entries passed; all four filesystem views exposed identical patched files; original
BIOS/EFI boot images and MBR code were preserved. A disposable QEMU/OVMF VM completed an
unattended UEFI installation and rebooted; bootstrap-key SSH login, `native-test` hostname,
the `agent` account/password, active SSH service and provisioning banner were verified.
BIOS optical boot and UEFI USB-disk emulation both reached Ubuntu's autoinstall processing;
the firmware-only VMs had no writable target disk and stopped at disk selection as expected. Self-contained
Windows x64 and Linux x64 builds succeeded, and the Linux binary built an ISO with `PATH`
and `DOTNET_ROOT` pointing to nonexistent directories. The Windows binary has not been run
on Windows in this validation session.

Format references: [GNU xorriso](https://www.gnu.org/software/xorriso/) documents the existing
boot-replay approach; the managed patcher implements only the Ubuntu layout validated above.
