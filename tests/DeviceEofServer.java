import java.net.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
public class DeviceEofServer {
 public static void main(String[] args) throws Exception {
  try (ServerSocket server = new ServerSocket(19121, 1, InetAddress.getByName("127.0.0.1"))) {
   server.setSoTimeout(45000); System.out.println("READY");
   try (Socket socket = server.accept()) {
    socket.setSoTimeout(10000); InputStream in=socket.getInputStream();
    while(in.read()!=-1) { }
    socket.getOutputStream().write("response-after-eof".getBytes(StandardCharsets.US_ASCII));
    socket.shutdownOutput(); System.out.println("RESPONSE_SENT");
   }
  }
 }
}
