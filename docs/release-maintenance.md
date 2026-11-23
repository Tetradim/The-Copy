# Build and release TetherBridge

## Build checks

[Build and verify](https://github.com/Tetradim/The-Copy/actions/workflows/verify.yml) runs on pull requests and pushes to main. The Windows job builds Desktop and Tunnel Lab with the .NET 10 SDK, runs the regressions and simulated phone and recovery checks, and installs and uninstalls both Windows packages in temporary folders. The Android job builds a debug APK, runs JVM unit tests and verifies its debug signature. Both jobs upload test artifacts. Use the hardware checklist below before distributing a release.

From the repository root on Windows, install the .NET 10 SDK, Python 3, JDK 17 and Android SDK Platform and Build Tools 35. Set JAVA_HOME and ANDROID_HOME to your local directories. Run:

~~~powershell
dotnet build windows/TetherBridge.TunnelLab -c Release
python scripts/run-regression-tests.py
dotnet run --project tests/RelayTests -c Release
.\gradlew.bat :android:app:testDebugUnitTest :android:app:assembleDebug
.\scripts\build-release-windows.ps1 -OutputDirectory dist\release-0.6.12
~~~

Extract the desired Windows ZIP and run the enclosed install.ps1. The framework-dependent package requires the x64 .NET 10 **Desktop Runtime**. Choose the self-contained package on a machine without that runtime. Update self-contained deployments when .NET security fixes ship because their runtime travels with the app. To switch package variants within the same version, uninstall the installed variant first. Compare each downloaded ZIP with SHA256SUMS.txt from the same verified release. The installer also checks the individual files before installing.

## Hardware acceptance

Use an attended Windows test session with a connection you can restore locally. Keep a remote session's existing PdaNet link active until a separate route or an on-site recovery is available.

| Device or scenario | Record before approving a release |
| --- | --- |
| Samsung Galaxy Tab A9+ 5G (SM-X218U, Android 16) | Connect with the Android USB bridge and Desktop Connect. Run the desktop HTTPS check and browse. Unplug and reattach the cable, then verify automatic recovery or the displayed recovery action. |
| OnePlus 13R (CPH2647, Android 16) | Pair in Windows, start the Android Bluetooth bridge, connect and run the HTTPS check. Cycle Bluetooth or move the phone out of range, then verify reconnect and disconnect. |
| Desktop recovery | Install both variants in turn on a test system. Check app launch, upgrade, normal disconnect, sleep and resume, forced desktop exit and reboot. After each recovery confirm that temporary forwarding and proxy settings are released, and the recorded baseline works. |
| Experimental Tunnel Lab | In a separate attended session, test direct HTTPS, UDP and DNS, followed by forced exit and restoration of the pre-test routes and DNS. Keep full routing opt-in until this passes on hardware. |

Record app versions, OS build, model, transport and outcome. Remove pairing data and device identifiers from anything uploaded publicly.

## Signing and publication

Use the same Android application ID and release signing identity as the installed release. Keep the private key and passwords outside Git and CI artifacts. Verify the signed APK with apksigner verify --verbose --print-certs and compare its signer fingerprint with the previous release. GitHub builds a debug APK with an ephemeral signer, so it may require a separate test installation when an existing APK uses a different key.

For Windows, sign the published TetherBridge executable and the installer script with the authorized Authenticode identity and an RFC 3161 timestamp before running scripts/package-windows.ps1. Recreate the ZIP and its SHA-256 after signing, then repeat the isolated install/uninstall check. Use Get-AuthenticodeSignature to confirm the signer. The build workflow generates unsigned Windows test packages and performs no release publication. Publish only assets from the tested commit, with their checksums and release notes.

## Rollback

Disconnect the bridge and exit Desktop. Use the candidate package's install.ps1 -Uninstall, then run the previous package's installer and start the previous Desktop version. Keep a copy of the previous verified ZIP and its checksums before upgrading. Do not remove app data to resolve a shortcut or installer problem. If an Android update must be rolled back to a smaller version code, Android normally requires uninstalling it first. Record the pairing settings and be prepared to pair again before doing so.
