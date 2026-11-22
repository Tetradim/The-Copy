import java.net.*;
public class DeviceHoldPort {
  public static void main(String[] args) throws Exception {
    try (ServerSocket server = new ServerSocket(10890, 1, InetAddress.getByName("127.0.0.1"))) {
      System.out.println("PORT_HELD"); Thread.sleep(25000);
    }
  }
}
