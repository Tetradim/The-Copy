from pathlib import Path
from xml.sax.saxutils import escape
from reportlab.lib import colors
from reportlab.lib.styles import ParagraphStyle
from reportlab.platypus import SimpleDocTemplate, Paragraph, PageBreak, Table, TableStyle

out = Path(__file__).parent
ink = colors.HexColor("#162437")
muted = colors.HexColor("#48576A")
accent = colors.HexColor("#1B5268")
styles = {
    "title": ParagraphStyle("title", fontName="Helvetica-Bold", fontSize=23, leading=27, textColor=ink, spaceAfter=10),
    "sub": ParagraphStyle("sub", fontName="Helvetica", fontSize=10, leading=14, textColor=muted, spaceAfter=15),
    "head": ParagraphStyle("head", fontName="Helvetica-Bold", fontSize=13, leading=16, textColor=accent, spaceBefore=10, spaceAfter=5, keepWithNext=True),
    "body": ParagraphStyle("body", fontName="Helvetica", fontSize=10.5, leading=14.5, textColor=ink, spaceAfter=8),
    "small": ParagraphStyle("small", fontName="Helvetica", fontSize=9.25, leading=12.5, textColor=muted, spaceAfter=7),
    "cell": ParagraphStyle("cell", fontName="Helvetica", fontSize=9.5, leading=13, textColor=ink),
    "column": ParagraphStyle("column", fontName="Helvetica-Bold", fontSize=9.5, leading=13, textColor=colors.white),
}
story = []

def write(kind, content):
    story.append(Paragraph(content, styles[kind]))

def block(title, body):
    write("head", title)
    write("body", body)

write("title", "PdaNet and TetherBridge USB and Bluetooth Review")
write("sub", "Technical review  |  November 22, 2026  |  Windows and Android")
write("body", "<b>Decision for the OnePlus 13R.</b> USB is unavailable on that phone according to the owner, so Bluetooth is the acceptance path. TetherBridge Bluetooth carried web and DNS traffic in isolated Samsung tests. Its OnePlus compatibility, sustained use and full Windows routing still need proof.")
block("PdaNet review scope", "The Windows installer is <b>PdaNetA5232b.exe</b>. I inspected the installed <b>PdaNetPC.exe</b>, the bundled <b>pneteth.sys</b> driver and the bundled Android APK. I used binary headers, imports, C++ type metadata and public help. The original source project was unavailable, so this portion is a binary and behavior review.")
block("Windows and Android implementation", "PdaNetPC.exe contains Microsoft-style C++ type metadata and has no CLR header. It imports native Windows networking, setup and IP helper APIs. The bundled pneteth.sys imports the Windows kernel and NDIS. The Android APK contains classes.dex and reports version code 5232 and version name 5.23. DEX establishes the executable format. It does not identify whether the original source used Java or Kotlin.")
block("USB findings", "The vendor documents USB debugging, driver installation and recovery, and possible conflicts with another ADB process. The installed network driver supports the conclusion that PdaNet includes driver-backed networking. The exact Windows packet path, forwarding protocol, DNS selection and restoration sequence for the owner's current session remain unverified. The owner reports PdaNet works on this laptop.")
block("Bluetooth findings", "PdaNet documents Bluetooth Dial-Up Networking, or DUN. Its Windows XP instructions describe pairing and a dial-up network connection through a Bluetooth modem, and mention a newer desktop connection command. That supports DUN as the published interface. The historic guide does not establish the current Windows 11 and Android 16 behavior, which services the OnePlus exposes, or the app's authentication and reconnection internals.")
write("small", 'Vendor references: <link href="https://pdanet.co/install/" color="#1B5268">Downloads</link> · <link href="https://pdanet.co/help/usb.php" color="#1B5268">USB troubleshooting</link> · <link href="https://pdanet.co/help/devices.php" color="#1B5268">Device and Bluetooth modes</link> · <link href="https://pdanet.co/help/bt/winxp.php" color="#1B5268">Historic Windows Bluetooth setup</link>.')

story.append(PageBreak())
write("title", "TetherBridge Source Review")
write("sub", "Windows 0.6.9 and Android 0.6.7 · Current checkout reviewed · Earlier Samsung results labeled separately")
block("USB architecture and strengths", "The desktop allocates a loopback ADB forward to the Android framed USB listener. The frame layer preserves boundaries and supports half-close. The desktop exposes HTTP and SOCKS5 and checks candidate health before dropping a working session. USB recovery rechecks the Android service after refreshing the forward. It reports failure when the service still needs to be started on Android.")
block("Bluetooth architecture and strengths", "Windows enumerates paired devices and opens custom RFCOMM services. The connection requests Bluetooth authentication and encryption, then uses pinned TLS and an application pairing token. Six desktop slots carry data and two are reserved for health and status. Android registers eight matching services and forwards them to its local TLS and bridge listeners. This requires a matching TetherBridge app on both ends and exposes no PdaNet DUN endpoint.")
block("High priority Bluetooth can stay offline after a drop", "The desktop creates the Bluetooth session without a reconnect callback. Health probes can ride out brief failures, but repeated failures trigger teardown. A later radio recovery does not automatically rebuild that session. Add opt-in reconnect for the selected paired device, with bounded backoff and cancellation on manual disconnect. Verify Bluetooth off/on, sleep, Android service stop and restart on the OnePlus. Evidence: MainForm.cs at 447 and 725; PhoneBridgeSession.cs at 5.")
block("High priority Bluetooth data setup has no short deadline", "Health and status probes have a 15-second budget. HTTP and SOCKS data clients hand the Bluetooth connect path the session cancellation token after initial header negotiation. A slow RFCOMM lookup, connection or TLS handshake can occupy a data slot until timeout elsewhere or session shutdown. Add a connection-only deadline and assert the slot is returned on every failure. Evidence: BluetoothRfcommTransport.cs at 39 and 93; LocalRelayProxy.cs at 98 and 130; LocalSocks5Relay.cs at 106 and 242.")
block("Medium priority USB cleanup can lose its retry record", "The ADB service clears its owned-forward record before looking up or removing the mapping. If that operation fails, the same session has no owned record for a retry. Keep the record until removal is confirmed or the exact mapping is absent. Test a transient failure followed by successful cleanup while preserving other forwards. Evidence: AdbService.cs at 92 and 105.")

story.append(PageBreak())
write("title", "Side by Side Comparison")
write("sub", "Functionality and implementation evidence for USB and Bluetooth")
matrix = [
    ["Area", "PdaNet", "TetherBridge"],
    ["USB setup", "Vendor documents USB debugging and Windows drivers. The owner's existing setup works.", "Uses Android platform tools, an authorized ADB device and the TetherBridge USB service. ADB forwarding is recorded per session."],
    ["USB protocol", "Internal forwarding and packet framing are unknown from the inspected binaries.", "App-defined framed transport over an allocated local ADB forward. Prior Samsung tests exercised HTTPS, TCP and UDP DNS, and half-close."],
    ["Bluetooth setup", "Vendor documents Bluetooth DUN and a modem/dial-up connection flow. Current OnePlus behavior is unverified.", "Windows pairs the device and selects it in TetherBridge. Android runs Bluetooth Bridge. Both sides use matching RFCOMM services and pairing credentials."],
    ["Bluetooth implementation", "Exact security, data transport, concurrency limits and recovery behavior are unknown from the files reviewed.", "Authenticated RFCOMM, pinned TLS, token checks and framed streams. The desktop has six data slots and two reserved probe slots."],
    ["Scope of Windows traffic", "Includes a Windows network driver. Legacy Bluetooth documentation exposes a Windows network connection through DUN.", "The released app exposes HTTP and SOCKS5. Traffic from applications that ignore those proxies needs Tunnel Lab. Its non-proxy routing and system DNS remain unverified on the devices."],
    ["Recovery", "Vendor documents USB driver/debugging remedies. Crash and Bluetooth reconnect internals were not observed.", "USB can retry the forward and confirm service health. Bluetooth detects faults but lacks automatic session rebuild after teardown. Forced-exit and restart tests remain open."],
    ["OnePlus 13R", "PdaNet's DUN workflow provides a comparison target. Its behavior on this phone has not been recorded here.", "The owner's USB constraint makes Bluetooth the priority. Samsung results establish feasibility of the transport but leave the OnePlus outcome open."],
]
table = Table([[Paragraph(escape(v), styles["column" if r == 0 else "cell"]) for v in row]
               for r, row in enumerate(matrix)], colWidths=[101, 199, 210], repeatRows=1, hAlign="LEFT")
table.setStyle(TableStyle([
    ("BACKGROUND", (0, 0), (-1, 0), accent),
    ("ROWBACKGROUNDS", (0, 1), (-1, -1), [colors.white, colors.HexColor("#F0F5F7")]),
    ("VALIGN", (0, 0), (-1, -1), "TOP"),
    ("LEFTPADDING", (0, 0), (-1, -1), 8), ("RIGHTPADDING", (0, 0), (-1, -1), 8),
    ("TOPPADDING", (0, 0), (-1, -1), 8), ("BOTTOMPADDING", (0, 0), (-1, -1), 8),
    ("LINEBELOW", (0, -1), (-1, -1), .5, colors.HexColor("#CFD9E0")),
]))
story.append(table)

story.append(PageBreak())
write("title", "Bluetooth Acceptance on the OnePlus")
write("sub", "Tests that distinguish a working bridge from a PdaNet replacement")
block("1 Connect without USB", "Install compatible TetherBridge builds, pair the OnePlus 13R CPH2647 with Windows, and use pairing details from the Android screen without ADB. Confirm health and status, record OS and build versions, and test that Windows connects to the intended paired device when more than one is available.")
block("2 Verify traffic and channel limits", "Exercise HTTPS, TCP and UDP DNS, downloads and concurrent apps. Hold six data streams open and confirm health and status still work. Repeat the HTTPS and DNS sequence under sustained use. Confirm that a failed handset authentication or a stalled connect releases its slot and has a useful error.")
block("3 Prove full Windows routing", "In an isolated Windows environment, run the tunnel over the verified Bluetooth bridge. Test HTTPS with Windows proxy bypassed, a separate UDP flow and ordinary Windows name resolution. Confirm the selected interface and phone-side egress, then compare routes and DNS with the baseline after stopping. Only this stage demonstrates the intended whole-computer behavior.")
block("4 Verify recovery and compare the real result", "Toggle Bluetooth, sleep and resume Windows, stop and restart the Android service, then force the desktop to exit in the isolated environment. Record whether connection recovery is automatic, how long it takes and whether Windows networking is restored. If PdaNet is already available over Bluetooth on the OnePlus, run the same user-facing scenarios and record the differences. Keep the Samsung as the USB regression device.")
block("Evidence and limitations", "Prior project logs record isolated Samsung USB and Bluetooth successes. This review inspected the current source and binaries without running a new hardware session. The findings identify concrete source behavior and test gaps. They do not establish the OnePlus result or PdaNet's internal ranking for speed, security or reliability.")
write("small", r"Source base: the repository root. Windows entry points are in MainForm.cs; transport code in BluetoothRfcommTransport.cs and FramedUsbStream.cs; Android listener code in BluetoothRfcommServer.kt and FramedUsbServer.kt. Test record: docs/parity-progress.md. Routing test procedure: docs/full-tunnel-design.md.")

def footer(canvas, doc):
    canvas.setFont("Helvetica", 8.5)
    canvas.setFillColor(muted)
    canvas.drawString(51, 32, "PdaNet and TetherBridge technical review")
    canvas.drawRightString(561, 32, str(doc.page))

SimpleDocTemplate(str(out / "PdaNet_TetherBridge_USB_Bluetooth_Review.pdf"),
                  pagesize=(612, 792), leftMargin=51, rightMargin=51, topMargin=52,
                  bottomMargin=51, title="PdaNet and TetherBridge USB and Bluetooth Review").build(
                      story, onFirstPage=footer, onLaterPages=footer)
