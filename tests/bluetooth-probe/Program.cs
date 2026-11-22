using System.Net;
using System.Net.Sockets;
using TetherBridge.Desktop.Transports;
using TetherBridge.Desktop.Services;
var port=int.Parse(Environment.GetEnvironmentVariable("TB_USB_PORT")!);
var transport=new BandwidthLimitedTransport(new TcpPhoneTransport("127.0.0.1",port,framedUsb:true),64*1024);
var reserve=new TcpListener(IPAddress.Loopback,0);reserve.Start();int h=((IPEndPoint)reserve.LocalEndpoint).Port;reserve.Stop();reserve=new TcpListener(IPAddress.Loopback,0);reserve.Start();int s=((IPEndPoint)reserve.LocalEndpoint).Port;reserve.Stop();
var session=new PhoneBridgeSession(transport,null);var http=new LocalRelayProxy();var socks=new LocalSocks5Relay();http.Start(session,h,_=>{});socks.Start(session,s,_=>{});
try {using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));using var client=new HttpClient(new HttpClientHandler { Proxy=new WebProxy($"http://127.0.0.1:{h}") });var text=await client.GetStringAsync("https://example.com/",timeout.Token);if(!text.Contains("Example Domain"))throw new Exception("Bad HTTPS");
 var dns=new SocksDnsResolver(s,IPAddress.Parse("1.1.1.1"));var q=new byte[]{0x4a,0x42,1,0,0,1,0,0,0,0,0,0,7,101,120,97,109,112,108,101,3,99,111,109,0,0,1,0,1};var response=await dns.ResolveAsync(q,timeout.Token);if(!DnsMessageValidation.MatchesResponse(q,response))throw new Exception("Bad DNS");Console.WriteLine("PASS: bandwidth-limited Samsung USB bridge carries HTTPS and UDP DNS");
}finally{http.Stop();socks.Stop();}
