import os, re, shutil, socket, subprocess, time, xml.etree.ElementTree as ET
adb = shutil.which('adb') or os.path.join(os.environ.get('ANDROID_HOME', os.path.join(os.environ.get('LOCALAPPDATA', ''), 'Android', 'Sdk')), 'platform-tools', 'adb.exe')
def run(*args): return subprocess.run([adb,*args], capture_output=True, text=True, check=True).stdout
def tap(label):
    run('shell','uiautomator','dump','/sdcard/tb-check.xml')
    xml = run('shell','cat','/sdcard/tb-check.xml')
    root = ET.fromstring(xml)
    for node in root.iter('node'):
        if node.get('text') == label:
            vals = list(map(int, re.findall(r'\d+',node.get('bounds'))))
            run('shell','input','tap',str((vals[0]+vals[2])//2),str((vals[1]+vals[3])//2)); return
    raise RuntimeError('Button not found: ' + label)
def health():
    deadline = time.monotonic() + 5
    while time.monotonic() < deadline:
        try:
            with socket.create_connection(('127.0.0.1',19088), timeout=1) as sock:
                sock.sendall(b'GET /health HTTP/1.1\r\nHost: phone\r\nConnection: close\r\n\r\n')
                if b'200 OK' in sock.recv(4096): return
        except OSError: pass
        time.sleep(.1)
    raise RuntimeError('USB bridge did not answer health check')
tap('START USB BRIDGE'); health(); print('PASS: USB bridge starts')
tap('STOP BRIDGE'); tap('START USB BRIDGE'); health(); print('PASS: USB bridge restarts after listener was used')
