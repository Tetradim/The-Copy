import os,re,socket,subprocess,time,xml.etree.ElementTree as E
adb=os.path.join(os.environ.get('ANDROID_HOME',os.path.join(os.environ.get('LOCALAPPDATA',''),'Android','Sdk')),'platform-tools','adb.exe')
def run(*args): return subprocess.check_output([adb,*args],text=True).strip()
port=run('forward','tcp:0','tcp:10890')
try:
 deadline=time.monotonic()+5
 while True:
  try:
   with socket.create_connection(('127.0.0.1',int(port)),timeout=1) as probe:
    probe.sendall(b'GET /health HTTP/1.1\r\nHost: phone\r\nConnection: close\r\n\r\n')
    if b'200 OK' in probe.recv(512): break
  except OSError: pass
  if time.monotonic()>deadline: raise RuntimeError('USB bridge did not start')
  time.sleep(.1)
 with socket.create_connection(('127.0.0.1',int(port)),timeout=4) as client:
  client.settimeout(5); client.sendall(b'CONNECT-UDP 127.0.0.1:19122 HTTP/1.1\r\nHost: test\r\n\r\n')
  header=b''
  while b'\r\n\r\n' not in header:
   data=client.recv(1)
   if not data: raise RuntimeError('UDP tunnel did not open')
   header+=data
  if b'200 Connection Established' not in header: raise RuntimeError('UDP tunnel rejected')
  run('shell','uiautomator','dump','/sdcard/tb-udp.xml')
  xml=run('shell','cat','/sdcard/tb-udp.xml')
  for n in E.fromstring(xml).iter('node'):
   if n.get('text')=='STOP BRIDGE':
    v=list(map(int,re.findall(r'\d+',n.get('bounds')))); run('shell','input','tap',str((v[0]+v[2])//2),str((v[1]+v[3])//2)); break
  else: raise RuntimeError('Stop button unavailable')
  try:
   if client.recv(1): raise RuntimeError('Unexpected data after stop')
  except socket.timeout: raise RuntimeError('UDP tunnel remained open after the bridge stopped')
  print('PASS: stopping Android bridge closes active UDP tunnels')
finally: run('forward','--remove','tcp:'+port)
