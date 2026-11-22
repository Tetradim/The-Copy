import java.net.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
public class DeviceIpv6Connect {
  public static void main(String[] args) throws Exception {
    ServerSocket server = new ServerSocket(0,1,InetAddress.getByName("::1")); server.setSoTimeout(5000);
    Thread origin=new Thread(() -> { try(Socket s=server.accept()) { s.getOutputStream().write("ipv6-ok".getBytes(StandardCharsets.US_ASCII)); } catch(Exception e){e.printStackTrace();} }); origin.start();
    try(Socket client=new Socket("127.0.0.1",10890)) {
      client.setSoTimeout(5000); client.getOutputStream().write(("CONNECT [::1]:"+server.getLocalPort()+" HTTP/1.1\r\nHost: localhost\r\n\r\n").getBytes(StandardCharsets.US_ASCII));
      ByteArrayOutputStream data=new ByteArrayOutputStream(); int ch; while((ch=client.getInputStream().read())!=-1) data.write(ch);
      String response=data.toString("US-ASCII"); if(!response.contains("200 Connection Established") || !response.endsWith("ipv6-ok")) throw new IOException("IPv6 CONNECT failed: "+response);
      System.out.println("PASS: Android proxy connects to bracketed IPv6 target");
    } finally {server.close(); origin.join(5000);}
  }
}
