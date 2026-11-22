import java.net.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
public class DeviceHalfClose {
  public static void main(String[] args) throws Exception {
    ServerSocket server = new ServerSocket(0, 1, InetAddress.getByName("127.0.0.1"));
    Thread origin = new Thread(() -> { try (Socket s=server.accept()) { while(s.getInputStream().read()!=-1) {} s.getOutputStream().write("reply".getBytes(StandardCharsets.US_ASCII)); s.shutdownOutput(); } catch(Exception e) {e.printStackTrace();} }); origin.start();
    try(Socket client = new Socket("127.0.0.1",10890)) {
      client.setSoTimeout(5000);
      client.getOutputStream().write(("CONNECT 127.0.0.1:"+server.getLocalPort()+" HTTP/1.1\r\nHost: test\r\n\r\n").getBytes(StandardCharsets.US_ASCII));
      InputStream in=client.getInputStream(); int match=0; byte[] end={13,10,13,10}; while(match<4) {int c=in.read(); if(c<0) throw new IOException("CONNECT closed"); match=c==end[match] ? match+1 : 0;}
      client.getOutputStream().write("request".getBytes(StandardCharsets.US_ASCII)); client.shutdownOutput();
      ByteArrayOutputStream out=new ByteArrayOutputStream(); int c; while((c=in.read())!=-1) out.write(c);
      String response=out.toString("US-ASCII"); if(!response.equals("reply")) throw new IOException("Expected reply after half-close, got: "+response);
      System.out.println("PASS: Android bridge preserves response after half-close");
    } finally {server.close(); origin.join(5000);}
  }
}
