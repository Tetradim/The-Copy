import os, subprocess, shutil
adb=shutil.which('adb') or os.path.join(os.environ.get('ANDROID_HOME',os.path.join(os.environ.get('LOCALAPPDATA',''),'Android','Sdk')),'platform-tools','adb.exe')
dotnet=os.environ.get('DOTNET_EXE') or shutil.which('dotnet') or 'dotnet'
def run(*args): return subprocess.check_output([adb,*args],text=True).strip()
serial=run('get-serialno'); port=run('-s',serial,'forward','tcp:0','tcp:10889')
if not port.isdigit() or not 1<=int(port)<=65535: raise RuntimeError('Invalid allocated port')
try: subprocess.run([dotnet,'run','--no-build','--project','tests/RelayTests/RelayTests.csproj','--','framed-device',port],check=True)
finally: run('-s',serial,'forward','--remove','tcp:'+port)
