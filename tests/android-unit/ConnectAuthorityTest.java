import org.tetherbridge.classicapp.ConnectAuthority;
public class ConnectAuthorityTest {
    private static void check(String input, String host, int port) {
        ConnectAuthority target = ConnectAuthority.parse(input);
        if (target == null || !target.host.equals(host) || target.port != port) throw new AssertionError("Invalid parse: " + input);
    }
    private static void reject(String input) { if (ConnectAuthority.parse(input) != null) throw new AssertionError("Accepted invalid target: " + input); }
    public static void main(String[] args) {
        check("example.com:443", "example.com", 443); check("127.0.0.1:8080", "127.0.0.1", 8080);
        check("[::1]:1234", "::1", 1234); check("[2001:db8::1]:443", "2001:db8::1", 443);
        reject("[::1]"); reject("::1:443"); reject("example.com:0"); reject("example.com:65536"); reject("example.com:nope"); reject(":80");
        System.out.println("PASS: CONNECT authority validation covers DNS, IPv4 and bracketed IPv6");
    }
}
