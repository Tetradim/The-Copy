using TetherBridge.Desktop.FullTunnel;
var directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"TetherBridge","Tunnel");
TunnelPathTrust.Verify(directory,Path.Combine(directory,"tun2socks.exe"),Path.Combine(directory,"wintun.dll"));
Console.WriteLine("PASS: installed tunnel dependency ACL and path checks");
