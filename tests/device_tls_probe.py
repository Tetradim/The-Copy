import os, re, shutil, subprocess, urllib.parse
adb = shutil.which('adb') or os.path.join(os.environ.get('ANDROID_HOME', os.path.join(os.environ.get('LOCALAPPDATA',''),'Android','Sdk')), 'platform-tools', 'adb.exe')
dotnet = os.environ.get('DOTNET_EXE') or shutil.which('dotnet') or 'dotnet'
def run(*args): return subprocess.run([adb, *args], capture_output=True, text=True, check=True).stdout.strip()
serial = run('get-serialno')
result = run('-s', serial, 'shell', 'content', 'query', '--uri', 'content://org.tetherbridge.classicapp.usbpairing/pairing', '--projection', 'payload')
match = re.search(r'payload=(tetherbridge://\S+)', result)
if not match: raise RuntimeError('Could not read a pairing payload from the Android app')
query = urllib.parse.parse_qs(urllib.parse.urlsplit(match.group(1)).query)
env = os.environ.copy(); env['TB_TEST_PIN'] = query['fingerprint'][0]; env['TB_TEST_TOKEN'] = query['token'][0]
port = run('-s', serial, 'forward', 'tcp:0', 'tcp:10888')
if not port.isdigit() or not 1 <= int(port) <= 65535: raise RuntimeError('ADB did not allocate a valid port')
try:
    subprocess.run([dotnet, 'run', '--no-build', '--project', 'tests/RelayTests/RelayTests.csproj', '--', 'tls-device', port], env=env, check=True)
finally:
    run('-s', serial, 'forward', '--remove', 'tcp:' + port)
