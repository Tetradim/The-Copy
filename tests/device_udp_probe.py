import os,re,subprocess,shutil,sys,urllib.parse
adb=shutil.which('adb') or os.path.join(os.environ.get('ANDROID_HOME',os.path.join(os.environ.get('LOCALAPPDATA',''),'Android','Sdk')),'platform-tools','adb.exe')
dotnet=os.environ.get('DOTNET_EXE') or shutil.which('dotnet') or 'dotnet'
def run(*args): return subprocess.check_output([adb,*args],text=True).strip()
serial=run('get-serialno'); env=os.environ.copy(); env.pop('TB_TEST_PIN',None); env.pop('TB_TEST_TOKEN',None); phone_port='10889'
if '--tls' in sys.argv:
    output=run('-s',serial,'shell','content','query','--uri','content://org.tetherbridge.classicapp.usbpairing/pairing','--projection','payload')
    match=re.search(r'payload=(tetherbridge://\S+)',output)
    if not match: raise RuntimeError('Could not read pairing payload')
    query=urllib.parse.parse_qs(urllib.parse.urlsplit(match.group(1)).query)
    env['TB_TEST_PIN']=query['fingerprint'][0]; env['TB_TEST_TOKEN']=query['token'][0]; phone_port='10888'
port=run('-s',serial,'forward','tcp:0','tcp:'+phone_port)
if not port.isdigit() or not 1<=int(port)<=65535: raise RuntimeError('Invalid allocated port')
try: subprocess.run([dotnet,'run','--no-build','--project','tests/RelayTests/RelayTests.csproj','--','udp-device',port],env=env,check=True)
finally: run('-s',serial,'forward','--remove','tcp:'+port)
