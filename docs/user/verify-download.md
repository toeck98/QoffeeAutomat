# Verify your download

QoffeeAutomat is released on GitHub. Every release asset can be checked in three independent ways. Do this before you
install software that runs as a Windows service with administrator rights.

Replace `<version>` with the version you downloaded, for example `1.0.0`.

## What a release contains

| File | Purpose |
|------|---------|
| `QoffeeAutomat-Setup-<version>-x64.exe`, `QoffeeAutomat-Setup-<version>-arm64.exe` | Installer for 64-bit Intel/AMD or ARM64 Windows. |
| `QoffeeAutomat-<version>-win-x64.zip`, `QoffeeAutomat-<version>-win-arm64.zip` | Portable version without installer. |
| `qoffeeautomat_<version>_amd64.deb`, `qoffeeautomat_<version>_arm64.deb` | Debian/Ubuntu package ([Install on Linux](install-linux.md)). |
| `QoffeeAutomat-<version>-linux-x64.tar.gz`, `QoffeeAutomat-<version>-linux-arm64.tar.gz` | Linux tarball with install script. |
| `qoffeeautomat.zip` | The Home Assistant integration; HACS downloads it for you. |
| `SHA256SUMS.txt` | SHA-256 checksum of every other file. |
| `sbom.spdx.json` | Software bill of materials (SPDX). |

## 1. Checksums

Download `SHA256SUMS.txt` into the same folder as the files and compare the hashes.

PowerShell:

```powershell
(Get-FileHash .\QoffeeAutomat-Setup-<version>-x64.exe -Algorithm SHA256).Hash
Select-String -Path .\SHA256SUMS.txt -Pattern 'QoffeeAutomat-Setup-<version>-x64.exe'
```

The two hashes must be identical (PowerShell prints upper case, the file lower case; case does not matter).

Linux or macOS (checks every file that is present):

```bash
sha256sum --ignore-missing -c SHA256SUMS.txt
```

A checksum only shows that the file was not damaged or changed on the way **from the same place**. It does not prove who
built it. For that use the attestation and the signature below.

## 2. Build provenance attestation

Every asset has a GitHub artifact attestation (see "Using artifact attestations" in the GitHub documentation):
a signed statement that the file was built by the release workflow of this repository from a given commit. You need the
[GitHub CLI](https://cli.github.com/) (`gh`); the check works without logging in to a GitHub account for public
repositories, but `gh` may ask you to run `gh auth login` once.

```powershell
gh attestation verify .\QoffeeAutomat-Setup-<version>-x64.exe --repo toeck98/QoffeeAutomat
```

A successful check prints "Verification succeeded" and the workflow, the repository and the commit that built the file.
To be stricter, also require the release workflow:

```powershell
gh attestation verify .\QoffeeAutomat-Setup-<version>-x64.exe --repo toeck98/QoffeeAutomat `
  --signer-workflow toeck98/QoffeeAutomat/.github/workflows/release.yml
```

Do the same for the other files you use. If the check fails, do not install the file and
report it as described in [security policy](https://github.com/toeck98/QoffeeAutomat/security/policy).

## 3. Authenticode signature (when the release is signed)

Releases are signed through the [SignPath Foundation](https://signpath.org/) once the project has been accepted there.
Releases that are not signed (for example before the application is accepted) still carry checksums and attestations;
check the signature as shown below to find out whether a file is signed.

PowerShell:

```powershell
Get-AuthenticodeSignature .\QoffeeAutomat-Setup-<version>-x64.exe | Format-List Status, StatusMessage, SignerCertificate
```

`Status` must be `Valid`, and the subject of the signer certificate must name the SignPath Foundation or the project.
Without PowerShell: right-click the file, Properties, tab "Digital Signatures", select the signature, Details. The same
works for `QoffeeAutomat.Agent.exe` and `QoffeeAutomat.SessionHost.exe` inside the installation folder or the portable zip.
`NotSigned` on an unsigned release is expected. The embedded uninstaller (`unins000.exe`) is not signed.

## SmartScreen

Windows SmartScreen judges a download by its signature and by how many people have already run it. A new or unsigned
installer therefore often shows "Windows protected your PC" and "Unknown publisher" (the same applies to a signed file
until it has built up a reputation).

If you have verified the checksum and the attestation above, you can continue: click "More info", check that the
file name is the one you verified, then click "Run anyway". Downloaded zip files may carry the "mark of the web";
if Windows blocks the executables, run `Unblock-File` on the zip before you extract it:

```powershell
Unblock-File .\QoffeeAutomat-<version>-win-x64.zip
```

Never turn SmartScreen off for this.

## The integration for Home Assistant

HACS downloads `qoffeeautomat.zip` from the release. To check it by hand, use the same three-step procedure; the
zip contains the files of `custom_components/qoffeeautomat` at its root and has no Authenticode signature (it is not an
executable), so checksum and attestation are the checks that apply.
