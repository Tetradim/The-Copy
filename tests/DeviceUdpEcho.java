import java.net.*;
public class DeviceUdpEcho {
 public static void main(String[] args) throws Exception {
  try (DatagramSocket socket=new DatagramSocket(19122,InetAddress.getByName("127.0.0.1"))) {
   socket.setSoTimeout(45000); System.out.println("READY"); byte[] b=new byte[65507];
   for(int i=0;i<3;i++) { DatagramPacket p=new DatagramPacket(b,b.length); socket.receive(p); socket.send(new DatagramPacket(p.getData(),p.getLength(),p.getSocketAddress())); }
   System.out.println("ECHO_COMPLETE");
  }
 }
}
