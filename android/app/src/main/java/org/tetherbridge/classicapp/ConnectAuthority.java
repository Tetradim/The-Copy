package org.tetherbridge.classicapp;

public final class ConnectAuthority {
    public final String host;
    public final int port;
    private ConnectAuthority(String host, int port) { this.host = host; this.port = port; }

    public static ConnectAuthority parse(String value) {
        if (value == null || value.isEmpty()) return null;
        String host, portText;
        if (value.charAt(0) == '[') {
            int end = value.indexOf(']');
            if (end < 2 || end + 1 >= value.length() || value.charAt(end + 1) != ':') return null;
            host = value.substring(1, end);
            if (!host.contains(":")) return null;
            portText = value.substring(end + 2);
        } else {
            int colon = value.indexOf(':');
            if (colon <= 0 || colon != value.lastIndexOf(':')) return null;
            host = value.substring(0, colon);
            portText = value.substring(colon + 1);
        }
        if (host.trim().isEmpty() || host.indexOf('[') >= 0 || host.indexOf(']') >= 0 || !portText.matches("[0-9]{1,5}")) return null;
        int port;
        try { port = Integer.parseInt(portText); } catch (NumberFormatException e) { return null; }
        return port >= 1 && port <= 65535 ? new ConnectAuthority(host, port) : null;
    }
}
