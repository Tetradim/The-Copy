import java.net.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
public class DeviceUdpTunnelTest {
 public static void main(String[] args) throws Exception {
  DatagramSocket echo = new DatagramSocket(0, InetAddress.getByName("127.0.0.1")); echo.setSoTimeout(8000);
  Thread worker=new Thread(() -> { try { byte[] b=new byte[65507]; for(int i=0;i<3;i++) { DatagramPacket p=new DatagramPacket(b,b.length); echo.receive(p); System.out.println("Echo got datagram length " + p.getLength()); echo.send(new DatagramPacket(p.getData(),p.getLength(),p.getSocketAddress())); } } catch(Exception e){ System.err.println("Echo error: " + e.getClass().getSimpleName()); } }); worker.setDaemon(true); worker.start();
  try(Socket client=new Socket("127.0.0.1",10890)) {
   client.setSoTimeout(6000); OutputStream raw=client.getOutputStream();
   raw.write(("CONNECT-UDP 127.0.0.1:"+echo.getLocalPort()+" HTTP/1.1\r\nHost: test\r\n\r\n").getBytes(StandardCharsets.US_ASCII)); raw.flush();
   ByteArrayOutputStream header=new ByteArrayOutputStream(); int c, match=0; byte[] end={13,10,13,10};
   while(match<4) { c=client.getInputStream().read(); if(c<0) throw new IOException("UDP tunnel closed before response"); header.write(c); match=c==end[match]?match+1:0; }
   if(!header.toString("US-ASCII").contains("200 Connection Established")) throw new IOException("UDP tunnel rejected");
   DataOutputStream out=new DataOutputStream(raw); DataInputStream in=new DataInputStream(client.getInputStream());
   for(byte[] payload: new byte[][] {"hello-udp".getBytes(StandardCharsets.US_ASCII), new byte[40000], new byte[0]}) {
    out.writeInt(payload.length); out.write(payload); out.flush(); int n=in.readInt(); byte[] reply=new byte[n]; in.readFully(reply);
    System.out.println("Received reply length " + n); if(!java.util.Arrays.equals(payload,reply)) throw new IOException("UDP echo payload mismatch");
   }
   System.out.println("PASS: Android UDP tunnel round-trips small, 40000-byte and empty datagrams");
  } finally {echo.close();worker.join(1000);}
 }
}
